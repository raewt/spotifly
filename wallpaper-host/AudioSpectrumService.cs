using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Spotifly.WallpaperHost;

internal sealed class AudioSpectrumService : IAsyncDisposable
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private const int FftSize = 2048;
    private const int BandCount = 64;
    private readonly object _audioLock = new();
    private readonly float[] _left = new float[FftSize];
    private readonly float[] _right = new float[FftSize];
    private readonly float[] _smoothed = new float[BandCount * 2];
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _broadcastLoop;
    private WasapiLoopbackCapture? _capture;
    private int _writePosition;
    private bool _bufferFilled;
    private bool _visible = true;

    public AudioSpectrumService()
    {
        _broadcastLoop = Task.Run(BroadcastLoopAsync);
    }

    public int ClientCount => _clients.Count;

    public bool IsCapturing
    {
        get
        {
            lock (_audioLock)
            {
                return _capture is not null;
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            lock (_audioLock)
            {
                return _visible;
            }
        }
    }

    public void SetVisible(bool visible)
    {
        lock (_audioLock)
        {
            _visible = visible;
        }
        UpdateCaptureState();
    }

    public async Task AddClientAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        _clients[id] = socket;
        UpdateCaptureState();

        var buffer = new byte[128];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            UpdateCaptureState();
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                }
            }
            catch
            {
            }
            socket.Dispose();
        }
    }

    private void UpdateCaptureState()
    {
        bool shouldRun;
        lock (_audioLock)
        {
            shouldRun = _visible && !_clients.IsEmpty;
            if (shouldRun == (_capture is not null))
            {
                return;
            }

            if (shouldRun)
            {
                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _writePosition = 0;
                    _bufferFilled = false;
                    Array.Clear(_left);
                    Array.Clear(_right);
                    _capture.StartRecording();
                }
                catch
                {
                    DisposeCaptureLocked();
                }
            }
            else
            {
                DisposeCaptureLocked();
                Array.Clear(_smoothed);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (_audioLock)
        {
            if (ReferenceEquals(sender, _capture))
            {
                DisposeCaptureLocked();
            }
        }
    }

    private void DisposeCaptureLocked()
    {
        WasapiLoopbackCapture? capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        WasapiLoopbackCapture? capture;
        lock (_audioLock)
        {
            capture = _capture;
        }
        if (capture is null)
        {
            return;
        }

        WaveFormat format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        int frameSize = bytesPerSample * channels;

        lock (_audioLock)
        {
            for (int offset = 0; offset + frameSize <= eventArgs.BytesRecorded; offset += frameSize)
            {
                float left = ReadSample(eventArgs.Buffer, offset, format);
                float right = channels > 1 ? ReadSample(eventArgs.Buffer, offset + bytesPerSample, format) : left;
                _left[_writePosition] = left;
                _right[_writePosition] = right;
                _writePosition = (_writePosition + 1) % FftSize;
                if (_writePosition == 0)
                {
                    _bufferFilled = true;
                }
            }
        }
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat;
        if (isFloat && format.BitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }
        if (format.BitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }
        if (format.BitsPerSample == 24)
        {
            int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xff000000);
            return sample / 8388608f;
        }
        if (format.BitsPerSample == 32)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }
        return 0f;
    }

    private async Task BroadcastLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                if (_clients.IsEmpty)
                {
                    continue;
                }

                float[] spectrum = CreateSpectrum();
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(spectrum);
                foreach ((Guid id, WebSocket socket) in _clients.ToArray())
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        _clients.TryRemove(id, out _);
                        continue;
                    }

                    try
                    {
                        await socket.SendAsync(payload, WebSocketMessageType.Text, true, _shutdown.Token);
                    }
                    catch
                    {
                        _clients.TryRemove(id, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private float[] CreateSpectrum()
    {
        var left = new float[FftSize];
        var right = new float[FftSize];
        int sampleRate;
        bool active;

        lock (_audioLock)
        {
            active = _visible && _capture is not null && (_bufferFilled || _writePosition > FftSize / 4);
            sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
            if (active)
            {
                for (int i = 0; i < FftSize; i++)
                {
                    int source = (_writePosition + i) % FftSize;
                    left[i] = _left[source];
                    right[i] = _right[source];
                }
            }
        }

        var result = new float[BandCount * 2];
        if (!active)
        {
            return result;
        }

        FillBands(left, sampleRate, result, 0);
        FillBands(right, sampleRate, result, BandCount);
        return result;
    }

    private void FillBands(float[] samples, int sampleRate, float[] result, int outputOffset)
    {
        var fft = new Complex[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            fft[i].X = samples[i] * (float)FastFourierTransform.HammingWindow(i, FftSize);
            fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, 11, fft);

        double minFrequency = 20;
        double maxFrequency = Math.Min(20000, sampleRate / 2d);
        for (int band = 0; band < BandCount; band++)
        {
            double lowFrequency = minFrequency * Math.Pow(maxFrequency / minFrequency, band / (double)BandCount);
            double highFrequency = minFrequency * Math.Pow(maxFrequency / minFrequency, (band + 1d) / BandCount);
            int lowBin = Math.Clamp((int)Math.Floor(lowFrequency * FftSize / sampleRate), 1, FftSize / 2 - 1);
            int highBin = Math.Clamp((int)Math.Ceiling(highFrequency * FftSize / sampleRate), lowBin + 1, FftSize / 2);
            double sum = 0;
            for (int bin = lowBin; bin < highBin; bin++)
            {
                double magnitude = Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y);
                sum += magnitude;
            }

            float normalized = (float)Math.Clamp(Math.Sqrt(sum / Math.Max(1, highBin - lowBin)) * 3.1, 0, 1);
            int index = outputOffset + band;
            float smoothing = normalized > _smoothed[index] ? 0.68f : 0.18f;
            _smoothed[index] += (normalized - _smoothed[index]) * smoothing;
            result[index] = MathF.Round(_smoothed[index], 4);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_audioLock)
        {
            DisposeCaptureLocked();
        }

        foreach (WebSocket socket in _clients.Values)
        {
            try { socket.Abort(); } catch { }
            socket.Dispose();
        }
        _clients.Clear();

        try { await _broadcastLoop; } catch { }
        _shutdown.Dispose();
    }
}
