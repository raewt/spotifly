using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using TurboJpegWrapper;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Spotifly.WallpaperHost;

internal sealed class SceneCaptureService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SceneCapture? _capture;
    private WallpaperProject? _desiredProject;
    private bool _visible = true;
    private long _frameVersion;
    private byte[]? _latestFrame;
    private TaskCompletionSource<long> _nextFrame = NewFrameSignal();
    private string? _lastError;

    public byte[]? LatestFrame
    {
        get
        {
            lock (this)
            {
                return _latestFrame;
            }
        }
    }

    public long FrameVersion => Interlocked.Read(ref _frameVersion);
    public string? ActiveProjectId => _capture?.ProjectId;
    public int CaptureWidth => _capture?.Width ?? 0;
    public int CaptureHeight => _capture?.Height ?? 0;
    public double EncodeMilliseconds => _capture?.EncodeMilliseconds ?? 0;
    public string? LastError => _lastError;

    public async Task<bool> EnsureStartedAsync(WallpaperProject project, CancellationToken cancellationToken)
    {
        _desiredProject = project;
        if (!_visible)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_capture?.ProjectId == project.Id)
            {
                return true;
            }

            await StopCoreAsync();
            string? engine = FindWallpaperEngine();
            if (engine is null)
            {
                return false;
            }

            var capture = new SceneCapture(project, engine, PublishFrame, ReportError);
            bool started;
            try
            {
                started = await capture.StartAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                ReportError(exception);
                await capture.DisposeAsync();
                return false;
            }
            if (!started)
            {
                await capture.DisposeAsync();
                _lastError ??= "Wallpaper Engine did not expose a capturable Scene window.";
                return false;
            }

            _capture = capture;
            _lastError = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActivityAsync(bool visible, WallpaperProject? project)
    {
        _visible = visible;
        _desiredProject = project;
        if (!visible || project is null)
        {
            await _gate.WaitAsync();
            try { await StopCoreAsync(); }
            finally { _gate.Release(); }
            return;
        }

        try { await EnsureStartedAsync(project, CancellationToken.None); } catch { }
    }

    public async Task<long> WaitForFrameAsync(long afterVersion, CancellationToken cancellationToken)
    {
        while (FrameVersion <= afterVersion)
        {
            Task<long> wait;
            lock (this)
            {
                if (_frameVersion > afterVersion)
                {
                    return _frameVersion;
                }
                wait = _nextFrame.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
        return FrameVersion;
    }

    private void PublishFrame(byte[] jpeg)
    {
        TaskCompletionSource<long> completed;
        long version;
        lock (this)
        {
            _latestFrame = jpeg;
            version = ++_frameVersion;
            completed = _nextFrame;
            _nextFrame = NewFrameSignal();
        }
        completed.TrySetResult(version);
    }

    private void ReportError(Exception exception) => _lastError = exception.ToString();

    private async Task StopCoreAsync()
    {
        SceneCapture? capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            await capture.DisposeAsync();
        }
        lock (this) _latestFrame = null;
    }

    private static TaskCompletionSource<long> NewFrameSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? FindWallpaperEngine()
    {
        foreach (string processName in new[] { "wallpaper64", "wallpaper32" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null && File.Exists(path)) return path;
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        foreach (string library in WallpaperCatalog.FindSteamLibraries())
        {
            string path = Path.Combine(library, "steamapps", "common", "wallpaper_engine", "wallpaper64.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed class SceneCapture : IAsyncDisposable
    {
        private const int CaptureWidth = 1920;
        private const int CaptureHeight = 1080;
        private const int CaptureFps = 30;
        private const int JpegQuality = 92;
        private readonly WallpaperProject _project;
        private readonly string _engine;
        private readonly Action<byte[]> _publish;
        private readonly Action<Exception> _reportError;
        private readonly string _windowName = $"Spotifly Scene {Environment.ProcessId}";
        private readonly object _frameLock = new();
        private readonly TJCompressor _compressor = new();
        private readonly HashSet<int> _ownedEngineProcesses = new();
        private IDirect3DDevice? _runtimeDevice;
        private SharpDX.Direct3D11.Device? _nativeDevice;
        private SharpDX.Direct3D11.Texture2D? _stagingTexture;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private long _lastFrameTicks;
        private double _encodeMilliseconds;
        private bool _disposed;
        private readonly List<(AudioSessionControl Session, bool WasMuted, float Volume)> _silencedSessions = new();

        public SceneCapture(WallpaperProject project, string engine, Action<byte[]> publish, Action<Exception> reportError)
        {
            _project = project;
            _engine = engine;
            _publish = publish;
            _reportError = reportError;
        }

        public string ProjectId => _project.Id;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double EncodeMilliseconds => Volatile.Read(ref _encodeMilliseconds);

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            string projectFile = Path.Combine(_project.RootPath, "project.json");
            if (!File.Exists(projectFile) || !GraphicsCaptureSession.IsSupported())
            {
                return false;
            }

            foreach (string staleWindow in FindWindowTitles("Spotifly Scene "))
            {
                await CloseWallpaperWindowAsync(staleWindow);
            }

            HashSet<int> engineProcessesBefore = GetWallpaperEngineProcessIds();
            var start = new ProcessStartInfo
            {
                FileName = _engine,
                WorkingDirectory = Path.GetDirectoryName(_engine)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
            {
                "-control", "openWallpaper", "-file", projectFile,
                "-playInWindow", _windowName, "-width", CaptureWidth.ToString(),
                "-height", CaptureHeight.ToString(), "-x", "-32000", "-y", "-32000", "-borderless"
            })
            {
                start.ArgumentList.Add(argument);
            }
            using Process? command = Process.Start(start);
            if (command is null) return false;

            nint window = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (window == 0 && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                window = FindWindowByTitle(_windowName);
                if (window == 0) await Task.Delay(120, cancellationToken);
            }
            if (window == 0) return false;
            if (engineProcessesBefore.Count == 0)
            {
                foreach (int processId in GetWallpaperEngineProcessIds())
                {
                    _ownedEngineProcesses.Add(processId);
                }
            }
            GraphicsCaptureItem item = GraphicsCaptureInterop.CreateItemForWindow(window);
            var captureSize = item.Size;
            Width = captureSize.Width;
            Height = captureSize.Height;
            ExcludeFromTaskbar(window);
            SilenceWallpaperAudioWithoutMuting();
            (_runtimeDevice, _nativeDevice) = GraphicsCaptureInterop.CreateDevice();
            var description = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = captureSize.Width,
                Height = captureSize.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                BindFlags = SharpDX.Direct3D11.BindFlags.None,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            try
            {
                _stagingTexture = new SharpDX.Direct3D11.Texture2D(_nativeDevice, description);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Cannot create Scene staging texture {captureSize.Width}x{captureSize.Height}.", exception);
            }
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _runtimeDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, captureSize);
            _session = _framePool.CreateCaptureSession(item);
            _framePool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            return true;
        }

        private void SilenceWallpaperAudioWithoutMuting()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                AudioSessionManager manager = device.AudioSessionManager;
                manager.RefreshSessions();
                for (int index = 0; index < manager.Sessions.Count; index++)
                {
                    AudioSessionControl session = manager.Sessions[index];
                    try
                    {
                        using Process process = Process.GetProcessById((int)session.GetProcessID);
                        if (!process.ProcessName.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase) &&
                            !process.ProcessName.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase))
                        {
                            session.Dispose();
                            continue;
                        }
                        bool wasMuted = session.SimpleAudioVolume.Mute;
                        float volume = session.SimpleAudioVolume.Volume;
                        session.SimpleAudioVolume.Volume = 0f;
                        session.SimpleAudioVolume.Mute = false;
                        _silencedSessions.Add((session, wasMuted, volume));
                    }
                    catch { session.Dispose(); }
                }
            }
            catch (Exception exception) { _reportError(exception); }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;
            using Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
            if (!Monitor.TryEnter(_frameLock)) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _lastFrameTicks < Stopwatch.Frequency / CaptureFps) return;
                _lastFrameTicks = now;
                using SharpDX.Direct3D11.Texture2D source = GraphicsCaptureInterop.GetTexture(frame.Surface);
                SharpDX.Direct3D11.DeviceContext context = _nativeDevice!.ImmediateContext;
                context.CopyResource(source, _stagingTexture!);
                SharpDX.DataBox mapped = context.MapSubresource(
                    _stagingTexture!, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    long encodeStarted = Stopwatch.GetTimestamp();
                    byte[] jpeg = _compressor.Compress(
                        mapped.DataPointer,
                        mapped.RowPitch,
                        frame.ContentSize.Width,
                        frame.ContentSize.Height,
                        TJPixelFormat.BGRA,
                        TJSubsamplingOption.Chrominance420,
                        JpegQuality);
                    double elapsed = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
                    double previous = Volatile.Read(ref _encodeMilliseconds);
                    Volatile.Write(ref _encodeMilliseconds, previous <= 0 ? elapsed : previous * 0.9 + elapsed * 0.1);
                    _publish(jpeg);
                }
                finally
                {
                    context.UnmapSubresource(_stagingTexture!, 0);
                }
            }
            catch (Exception exception) { _reportError(exception); }
            finally { Monitor.Exit(_frameLock); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
            lock (_frameLock)
            {
                _session?.Dispose();
                _framePool?.Dispose();
                _stagingTexture?.Dispose();
                _nativeDevice?.Dispose();
                _compressor.Dispose();
            }
            foreach ((AudioSessionControl session, bool wasMuted, float volume) in _silencedSessions)
            {
                try
                {
                    session.SimpleAudioVolume.Volume = volume;
                    session.SimpleAudioVolume.Mute = wasMuted;
                }
                catch { }
                session.Dispose();
            }
            _silencedSessions.Clear();

            await CloseWallpaperWindowAsync(_windowName);
            await StopOwnedEngineProcessesAsync();
        }

        private async Task StopOwnedEngineProcessesAsync()
        {
            if (_ownedEngineProcesses.Count == 0) return;
            await Task.Delay(350);
            foreach (int processId in _ownedEngineProcesses)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (!process.HasExited &&
                        (process.ProcessName.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase) ||
                         process.ProcessName.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase)))
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                }
                catch { }
            }
            _ownedEngineProcesses.Clear();
        }

        private static HashSet<int> GetWallpaperEngineProcessIds()
        {
            var processIds = new HashSet<int>();
            foreach (string processName in new[] { "wallpaper64", "wallpaper32" })
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try { processIds.Add(process.Id); }
                    finally { process.Dispose(); }
                }
            }
            return processIds;
        }

        private static void ExcludeFromTaskbar(nint window)
        {
            const int GwlExStyle = -20;
            const nint WsExToolWindow = 0x00000080;
            const nint WsExAppWindow = 0x00040000;
            const uint SwpNoSize = 0x0001;
            const uint SwpNoZOrder = 0x0004;
            const uint SwpNoActivate = 0x0010;
            const uint SwpFrameChanged = 0x0020;
            const int SwHide = 0;
            const int SwShowNoActivate = 4;

            try
            {
                _ = ShowWindow(window, SwHide);
                nint style = GetWindowLongPtr(window, GwlExStyle);
                _ = SetWindowLongPtr(window, GwlExStyle, (style | WsExToolWindow) & ~WsExAppWindow);
                _ = ShowWindow(window, SwShowNoActivate);
                _ = SetWindowPos(window, 0, -32000, -32000, 0, 0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            catch { }
        }

        private async Task CloseWallpaperWindowAsync(string windowName)
        {
            var close = new ProcessStartInfo
            {
                FileName = _engine,
                WorkingDirectory = Path.GetDirectoryName(_engine)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "-control", "closeWallpaper", "-location", windowName })
            {
                close.ArgumentList.Add(argument);
            }
            try
            {
                using Process? process = Process.Start(close);
                if (process is not null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        private static IReadOnlyList<string> FindWindowTitles(string prefix)
        {
            var titles = new List<string>();
            EnumWindows((window, _) =>
            {
                int length = GetWindowTextLength(window);
                if (length <= 0) return true;
                var buffer = new System.Text.StringBuilder(length + 1);
                _ = GetWindowText(window, buffer, buffer.Capacity);
                string title = buffer.ToString();
                if (title.StartsWith(prefix, StringComparison.Ordinal)) titles.Add(title);
                return true;
            }, 0);
            return titles;
        }

        private static nint FindWindowByTitle(string title)
        {
            nint match = 0;
            EnumWindows((window, _) =>
            {
                int length = GetWindowTextLength(window);
                if (length <= 0) return true;
                var buffer = new System.Text.StringBuilder(length + 1);
                _ = GetWindowText(window, buffer, buffer.Capacity);
                if (buffer.ToString().Equals(title, StringComparison.Ordinal))
                {
                    match = window;
                    return false;
                }
                return true;
            }, 0);
            return match;
        }

        private delegate bool EnumWindowsProc(nint window, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int maximum);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint window);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint window, int command);
    }
}
