using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
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

            var capture = new SceneCapture(project, engine, PublishFrame);
            if (!await capture.StartAsync(cancellationToken))
            {
                await capture.DisposeAsync();
                return false;
            }

            _capture = capture;
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

    private async Task StopCoreAsync()
    {
        SceneCapture? capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            await capture.DisposeAsync();
        }
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
        private const int CaptureWidth = 1280;
        private const int CaptureHeight = 720;
        private readonly WallpaperProject _project;
        private readonly string _engine;
        private readonly Action<byte[]> _publish;
        private readonly string _windowName = $"Spotifly Scene {Environment.ProcessId}";
        private readonly object _frameLock = new();
        private IDirect3DDevice? _runtimeDevice;
        private SharpDX.Direct3D11.Device? _nativeDevice;
        private SharpDX.Direct3D11.Texture2D? _stagingTexture;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private long _lastFrameTicks;
        private bool _disposed;
        private readonly List<(AudioSessionControl Session, bool WasMuted)> _mutedSessions = new();

        public SceneCapture(WallpaperProject project, string engine, Action<byte[]> publish)
        {
            _project = project;
            _engine = engine;
            _publish = publish;
        }

        public string ProjectId => _project.Id;

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
            MuteWallpaperAudio();

            GraphicsCaptureItem item = GraphicsCaptureInterop.CreateItemForWindow(window);
            (_runtimeDevice, _nativeDevice) = GraphicsCaptureInterop.CreateDevice();
            var description = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = item.Size.Width,
                Height = item.Size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                BindFlags = SharpDX.Direct3D11.BindFlags.None,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            _stagingTexture = new SharpDX.Direct3D11.Texture2D(_nativeDevice, description);
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _runtimeDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _session = _framePool.CreateCaptureSession(item);
            _framePool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            return true;
        }

        private void MuteWallpaperAudio()
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
                        session.SimpleAudioVolume.Mute = true;
                        _mutedSessions.Add((session, wasMuted));
                    }
                    catch { session.Dispose(); }
                }
            }
            catch { }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;
            using Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
            if (!Monitor.TryEnter(_frameLock)) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _lastFrameTicks < Stopwatch.Frequency / 20) return;
                _lastFrameTicks = now;
                using SharpDX.Direct3D11.Texture2D source = GraphicsCaptureInterop.GetTexture(frame.Surface);
                SharpDX.Direct3D11.DeviceContext context = _nativeDevice!.ImmediateContext;
                context.CopyResource(source, _stagingTexture!);
                SharpDX.DataBox mapped = context.MapSubresource(
                    _stagingTexture!, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    _publish(EncodeJpeg(mapped, frame.ContentSize.Width, frame.ContentSize.Height));
                }
                finally
                {
                    context.UnmapSubresource(_stagingTexture!, 0);
                }
            }
            catch { }
            finally { Monitor.Exit(_frameLock); }
        }

        private static byte[] EncodeJpeg(SharpDX.DataBox mapped, int width, int height)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytesPerRow = width * 4;
                for (int y = 0; y < height; y++)
                {
                    nint source = mapped.DataPointer + y * mapped.RowPitch;
                    nint destination = data.Scan0 + y * data.Stride;
                    unsafe { Buffer.MemoryCopy((void*)source, (void*)destination, bytesPerRow, bytesPerRow); }
                }
            }
            finally { bitmap.UnlockBits(data); }

            using var output = new MemoryStream();
            ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 86L);
            bitmap.Save(output, encoder, parameters);
            return output.ToArray();
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
            }
            foreach ((AudioSessionControl session, bool wasMuted) in _mutedSessions)
            {
                try { session.SimpleAudioVolume.Mute = wasMuted; } catch { }
                session.Dispose();
            }
            _mutedSessions.Clear();

            await CloseWallpaperWindowAsync(_windowName);
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
    }
}
