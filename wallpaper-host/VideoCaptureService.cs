using System.Diagnostics;
using TurboJpegWrapper;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Spotifly.WallpaperHost;

internal sealed class VideoCaptureService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VideoCapture? _capture;
    private bool _visible = true;
    private long _frameVersion;
    private byte[]? _latestFrame;
    private TaskCompletionSource<long> _nextFrame = NewFrameSignal();
    private string? _lastError;

    public byte[]? LatestFrame
    {
        get
        {
            lock (this) return _latestFrame;
        }
    }

    public long FrameVersion => Interlocked.Read(ref _frameVersion);
    public string? ActiveProjectId => _capture?.ProjectId;
    public int CaptureWidth => _capture?.Width ?? 0;
    public int CaptureHeight => _capture?.Height ?? 0;
    public double EncodeMilliseconds => _capture?.EncodeMilliseconds ?? 0;
    public bool IsPlaying => _capture?.IsPlaying ?? false;
    public string? LastError => _lastError;

    public async Task<bool> EnsureStartedAsync(WallpaperProject project, CancellationToken cancellationToken)
    {
        if (!_visible) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_capture?.ProjectId == project.Id)
            {
                _capture.Resume();
                return true;
            }

            await StopCoreAsync();
            string? path = WallpaperCatalog.SafeCombine(project.RootPath, project.EntryFile);
            if (path is null || !File.Exists(path)) return false;

            var capture = new VideoCapture(project.Id, path, PublishFrame, ReportError);
            try
            {
                if (!await capture.StartAsync(cancellationToken))
                {
                    await capture.DisposeAsync();
                    return false;
                }
            }
            catch (Exception exception)
            {
                ReportError(exception);
                await capture.DisposeAsync();
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
        await _gate.WaitAsync();
        try
        {
            if (project is null)
            {
                await StopCoreAsync();
                return;
            }
            if (!visible)
            {
                _capture?.Pause();
                return;
            }
        }
        finally
        {
            _gate.Release();
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
                if (_frameVersion > afterVersion) return _frameVersion;
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
        VideoCapture? capture = _capture;
        _capture = null;
        if (capture is not null) await capture.DisposeAsync();
        lock (this) _latestFrame = null;
    }

    private static TaskCompletionSource<long> NewFrameSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class VideoCapture : IAsyncDisposable
    {
        private const int CaptureFps = 30;
        private const int JpegQuality = 91;
        private readonly string _projectId;
        private readonly string _path;
        private readonly Action<byte[]> _publish;
        private readonly Action<Exception> _reportError;
        private readonly object _frameLock = new();
        private readonly TJCompressor _compressor = new();
        private readonly TaskCompletionSource<bool> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MediaPlayer? _player;
        private IDirect3DDevice? _runtimeDevice;
        private SharpDX.Direct3D11.Device? _nativeDevice;
        private VideoFrame? _targetFrame;
        private SharpDX.Direct3D11.Texture2D? _stagingTexture;
        private long _lastFrameTicks;
        private double _encodeMilliseconds;
        private bool _disposed;

        public VideoCapture(string projectId, string path, Action<byte[]> publish, Action<Exception> reportError)
        {
            _projectId = projectId;
            _path = path;
            _publish = publish;
            _reportError = reportError;
        }

        public string ProjectId => _projectId;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double EncodeMilliseconds => Volatile.Read(ref _encodeMilliseconds);
        public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            var player = new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                Volume = 0,
                IsVideoFrameServerEnabled = true
            };
            player.CommandManager.IsEnabled = false;
            player.MediaOpened += OnMediaOpened;
            player.MediaFailed += OnMediaFailed;
            player.VideoFrameAvailable += OnVideoFrameAvailable;
            _player = player;
            player.Source = MediaSource.CreateFromUri(new Uri(_path));
            player.Play();

            try
            {
                return await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                _reportError(new InvalidOperationException("Windows Media Foundation did not produce a video frame.", exception));
                return false;
            }
        }

        public void Pause()
        {
            try { _player?.Pause(); } catch { }
        }

        public void Resume()
        {
            try { _player?.Play(); } catch { }
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            if (_disposed) return;
            try
            {
                int width = checked((int)sender.PlaybackSession.NaturalVideoWidth);
                int height = checked((int)sender.PlaybackSession.NaturalVideoHeight);
                if (width <= 0 || height <= 0) throw new InvalidOperationException("Video has no decodable picture stream.");
                Width = width;
                Height = height;
                (_runtimeDevice, _nativeDevice) = GraphicsCaptureInterop.CreateDevice();
                _targetFrame = VideoFrame.CreateAsDirect3D11SurfaceBacked(
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, width, height, _runtimeDevice);
                _stagingTexture = new SharpDX.Direct3D11.Texture2D(_nativeDevice, new SharpDX.Direct3D11.Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                    Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                    BindFlags = SharpDX.Direct3D11.BindFlags.None,
                    CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                    OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
                });
            }
            catch (Exception exception)
            {
                _reportError(exception);
                _firstFrame.TrySetResult(false);
            }
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Exception detail = args.ExtendedErrorCode;
            var exception = new InvalidOperationException(
                $"Video playback failed ({args.Error}): {args.ErrorMessage}", detail);
            _reportError(exception);
            _firstFrame.TrySetResult(false);
        }

        private void OnVideoFrameAvailable(MediaPlayer sender, object args)
        {
            if (_disposed || _targetFrame is null || _stagingTexture is null || _nativeDevice is null) return;
            if (!Monitor.TryEnter(_frameLock)) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _lastFrameTicks < Stopwatch.Frequency / CaptureFps) return;
                _lastFrameTicks = now;
                sender.CopyFrameToVideoSurface(_targetFrame.Direct3DSurface);
                using SharpDX.Direct3D11.Texture2D source = GraphicsCaptureInterop.GetTexture(_targetFrame.Direct3DSurface);
                SharpDX.Direct3D11.DeviceContext context = _nativeDevice.ImmediateContext;
                context.CopyResource(source, _stagingTexture);
                SharpDX.DataBox mapped = context.MapSubresource(
                    _stagingTexture, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    long encodeStarted = Stopwatch.GetTimestamp();
                    byte[] jpeg = _compressor.Compress(
                        mapped.DataPointer,
                        mapped.RowPitch,
                        Width,
                        Height,
                        TJPixelFormat.BGRA,
                        TJSubsamplingOption.Chrominance420,
                        JpegQuality);
                    double elapsed = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
                    double previous = Volatile.Read(ref _encodeMilliseconds);
                    Volatile.Write(ref _encodeMilliseconds, previous <= 0 ? elapsed : previous * 0.9 + elapsed * 0.1);
                    _publish(jpeg);
                    _firstFrame.TrySetResult(true);
                }
                finally
                {
                    context.UnmapSubresource(_stagingTexture, 0);
                }
            }
            catch (Exception exception)
            {
                _reportError(exception);
                _firstFrame.TrySetResult(false);
            }
            finally
            {
                Monitor.Exit(_frameLock);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            MediaPlayer? player = _player;
            _player = null;
            if (player is not null)
            {
                player.MediaOpened -= OnMediaOpened;
                player.MediaFailed -= OnMediaFailed;
                player.VideoFrameAvailable -= OnVideoFrameAvailable;
                try { player.Pause(); } catch { }
                player.Dispose();
            }
            lock (_frameLock)
            {
                _stagingTexture?.Dispose();
                _targetFrame?.Dispose();
                _nativeDevice?.Dispose();
                _compressor.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}
