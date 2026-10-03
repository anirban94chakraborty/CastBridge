using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using CastBridge.Media.Server;
using CastBridge.Media.Transcode;

namespace CastBridge.Media.Live;

public enum LiveQuality
{
    Mp3_192 = 192,
    Mp3_256 = 256,
    Mp3_320 = 320,
}

public sealed record CaptureDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Casts whatever this PC is playing.
///
/// The chain is: WASAPI loopback capture (no virtual audio driver needed) -&gt; FFmpeg over stdin,
/// encoding to a headerless MP3 stream -&gt; stdout -&gt; subscribers on the media server. A receiver can
/// play that as an endless live stream, at the cost of a couple of seconds of latency.
/// </summary>
public sealed class LiveCaptureService : ILiveStreamSource, IAsyncDisposable
{
    public const string DefaultSessionId = "live";

    /// <summary>MP3 muxer writes a Xing header by default, which makes a live stream start late.</summary>
    private const string Mp3Flags = "-c:a libmp3lame -write_xing 0 -id3v2_version 0";

    private readonly ILogger<LiveCaptureService>? _logger;
    private readonly FfmpegLocator _locator;
    private readonly object _sync = new();
    private readonly List<LiveSubscription> _subscribers = new();

    private WasapiRecorder? _recorder;
    private Process? _ffmpeg;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _outputPump;
    private Task? _silenceWatch;
    private Task? _idleWatch;
    private long _lastCaptureTicks;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _encoderGate = new(1, 1);

    public LiveCaptureService(FfmpegLocator locator, ILoggerFactory? loggerFactory = null)
    {
        _locator = locator;
        _logger = loggerFactory?.CreateLogger<LiveCaptureService>();
    }

    public bool IsRunning { get; private set; }

    /// <summary>Peak level of the captured audio, 0..1, for the UI meter.</summary>
    public double Level { get; private set; }

    public event EventHandler<double>? LevelChanged;

    public event EventHandler<string>? StateChanged;

    public string? LastError { get; private set; }

    public string? CaptureDeviceName { get; private set; }

    public string? CaptureDeviceId { get; set; }

    public string? FfmpegPath { get; set; }

    public LiveQuality Quality { get; set; } = LiveQuality.Mp3_256;

    public int IdleStopSeconds { get; set; } = 45;

    public int SubscriberCount
    {
        get
        {
            lock (_sync)
                return _subscribers.Count;
        }
    }

    public IReadOnlyList<string> ActiveSessions => IsRunning ? new[] { DefaultSessionId } : Array.Empty<string>();

    public string GetContentType(string sessionId) => "audio/mpeg";

    /// <summary>Playback devices that can be captured (a loopback captures what a device is playing).</summary>
    public static IReadOnlyList<CaptureDeviceOption> GetCaptureDevices()
    {
        var options = new List<CaptureDeviceOption> { new(string.Empty, "System default output") };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    options.Add(new CaptureDeviceOption(device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // No audio endpoints (headless session); the default option still works.
        }

        return options;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return true;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return true;

            var ffmpeg = _locator.Resolve(FfmpegPath);
            if (ffmpeg is null)
            {
                LastError = "ffmpeg is required for live capture. Install it from Settings.";
                RaiseState(LastError);
                return false;
            }

            if (!TryStartRecorder())
                return false;

            if (!TryStartEncoder(ffmpeg))
            {
                StopRecorder();
                return false;
            }

            _cts = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
            _outputPump = Task.Run(() => PumpOutputAsync(_cts.Token), CancellationToken.None);
            _silenceWatch = Task.Run(() => SilenceWatchdogAsync(_cts.Token), CancellationToken.None);
            _idleWatch = Task.Run(() => WatchIdleAsync(_cts.Token), CancellationToken.None);

            IsRunning = true;
            LastError = null;
            RaiseState($"Live capture running ({Quality} kbps)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogError(ex, "Starting live capture failed");
            RaiseState(LastError);
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Builds a loopback recorder for the chosen output device. Loopback is how Windows lets an
    /// application hear what another application is playing, without a virtual audio cable.
    /// </summary>
    private bool TryStartRecorder()
    {
        try
        {
            var builder = new WasapiRecorderBuilder().WithLoopbackCapture().WithBufferLength(200);

            if (!string.IsNullOrWhiteSpace(CaptureDeviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(CaptureDeviceId);
                CaptureDeviceName = device.FriendlyName;
                builder = builder.WithDevice(device);
                device.Dispose();
            }
            else
            {
                CaptureDeviceName = "System default output";
            }

            _recorder = builder.Build();
            _recorder.RecordingStopped += OnRecordingStopped;
            _logger?.LogInformation("Capturing {Format} from {Device}", _recorder.WaveFormat, CaptureDeviceName);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Could not capture system audio: {ex.Message}";
            _logger?.LogError(ex, "Loopback capture failed to start");
            RaiseState(LastError);
            return false;
        }
    }

    private bool TryStartEncoder(string ffmpeg)
    {
        var format = _recorder?.WaveFormat;
        if (format is null)
            return false;

        var encoding = format.Encoding == WaveFormatEncoding.IeeeFloat ? "f32le" : "s16le";
        var arguments =
            $"-hide_banner -nostdin -loglevel error " +
            $"-f {encoding} -ar {format.SampleRate} -ac {format.Channels} -i pipe:0 " +
            $"{Mp3Flags} -b:a {(int)Quality}k -f mp3 pipe:1";

        try
        {
            _ffmpeg = FfmpegLocator.Start(ffmpeg, arguments);
            _ffmpeg.EnableRaisingEvents = true;
            _ffmpeg.Exited += (_, _) => OnEncoderExited();
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Could not start ffmpeg: {ex.Message}";
            _logger?.LogError(ex, "Encoder start failed");
            RaiseState(LastError);
            return false;
        }
    }

    /// <summary>Reads captured audio and feeds the encoder until capture stops.</summary>
    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var recorder = _recorder;
        if (recorder is null)
            return;

        try
        {
            await foreach (var buffer in recorder.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                var memory = buffer.Data;
                if (memory.Length == 0)
                    continue;

                Interlocked.Exchange(ref _lastCaptureTicks, DateTime.UtcNow.Ticks);
                UpdateLevel(memory.Span);
                await WriteToEncoderAsync(memory, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LastError = $"System audio capture stopped: {ex.Message}";
            _logger?.LogWarning(ex, "Capture loop ended");
            RaiseState(LastError);
        }
    }

    /// <summary>
    /// Keeps the encoder fed while nothing is playing. WASAPI usually delivers silence on its own, but
    /// a silent stall would make the receiver drop the stream, so gaps are filled explicitly.
    /// </summary>
    private async Task SilenceWatchdogAsync(CancellationToken cancellationToken)
    {
        var silence = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var lastTicks = Interlocked.Read(ref _lastCaptureTicks);
            if (lastTicks != 0 && DateTime.UtcNow.Ticks - lastTicks < TimeSpan.FromMilliseconds(700).Ticks)
                continue;

            await WriteToEncoderAsync(silence, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteToEncoderAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var encoder = _ffmpeg;
        if (encoder is null || encoder.HasExited)
            return;

        await _encoderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await encoder.StandardInput.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await encoder.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A dead encoder is recovered by the exit handler; do not kill the capture loop.
            _logger?.LogDebug(ex, "Writing to the encoder failed");
        }
        finally
        {
            _encoderGate.Release();
        }
    }

    private void UpdateLevel(ReadOnlySpan<byte> samples)
    {
        try
        {
            var peak = 0.0;
            var isFloat = _recorder?.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

            if (isFloat)
            {
                for (var i = 0; i + 4 <= samples.Length; i += 4)
                {
                    var sample = Math.Abs(BitConverter.ToSingle(samples[i..]));
                    if (sample > peak)
                        peak = sample;
                }
            }
            else
            {
                for (var i = 0; i + 2 <= samples.Length; i += 2)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(samples[i..]) / 32768.0);
                    if (sample > peak)
                        peak = sample;
                }
            }

            // Smooth the meter so it does not flicker.
            Level = (Level * 0.7) + (Math.Min(1.0, peak) * 0.3);
            LevelChanged?.Invoke(this, Level);
        }
        catch
        {
            // Metering is cosmetic.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LastError = $"Audio capture stopped: {e.Exception.Message}";
            _logger?.LogWarning(e.Exception, "Capture stopped unexpectedly");
            RaiseState(LastError);
        }
    }

    private void OnEncoderExited()
    {
        if (!IsRunning)
            return;

        // ffmpeg dying mid-stream would silently freeze the speakers, so restart it and keep the
        // subscribers: they see a short gap instead of a dead stream.
        _logger?.LogWarning("ffmpeg exited while streaming; restarting the encoder");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (!IsRunning)
                    return;

                var ffmpeg = _locator.Resolve(FfmpegPath);
                if (ffmpeg is null)
                    return;

                TryStartEncoder(ffmpeg);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Encoder restart failed");
            }
        });
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var encoder = _ffmpeg;
                if (encoder is null || encoder.HasExited)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var read = await encoder.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var chunk = buffer.AsSpan(0, read).ToArray();
                LiveSubscription[] targets;
                lock (_sync)
                    targets = _subscribers.ToArray();

                foreach (var target in targets)
                    target.Push(chunk);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logger?.LogDebug(ex, "Encoder output pump error");

                await SafeDelay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops capture once nobody is listening, so an idle app is not burning CPU encoding silence.</summary>
    private async Task WatchIdleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (SubscriberCount > 0 || IdleStopSeconds <= 0)
                continue;

            // A short grace period covers a receiver reconnecting after a seek or a stall.
            await SafeDelay(TimeSpan.FromSeconds(IdleStopSeconds), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (SubscriberCount == 0)
            {
                _logger?.LogInformation("Stopping live capture: no listeners for {Seconds}s", IdleStopSeconds);
                Stop();
                return;
            }
        }
    }

    public Task<ILiveStreamSubscription?> SubscribeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(sessionId, DefaultSessionId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<ILiveStreamSubscription?>(null);

        var subscription = new LiveSubscription(this);
        lock (_sync)
            _subscribers.Add(subscription);

        RaiseState($"Live listener connected ({SubscriberCount} active)");
        return Task.FromResult<ILiveStreamSubscription?>(subscription);
    }

    internal void RemoveSubscription(LiveSubscription subscription)
    {
        lock (_sync)
            _subscribers.Remove(subscription);

        RaiseState(IsRunning ? $"Live listener disconnected ({SubscriberCount} active)" : "Live capture stopped");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        StopRecorder();

        var encoder = _ffmpeg;
        _ffmpeg = null;
        if (encoder is not null)
        {
            try
            {
                if (!encoder.HasExited)
                {
                    encoder.StandardInput.BaseStream.Close();
                    if (!encoder.WaitForExit(1500))
                        encoder.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Board is already down.
            }
            finally
            {
                encoder.Dispose();
            }
        }

        LiveSubscription[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
            subscriber.Complete();

        Level = 0;
        LevelChanged?.Invoke(this, 0);
        RaiseState("Live capture stopped");
    }

    private void StopRecorder()
    {
        var recorder = _recorder;
        _recorder = null;

        if (recorder is null)
            return;

        try
        {
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.StopRecording();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Stopping capture failed");
        }
        finally
        {
            try
            {
                recorder.Dispose();
            }
            catch
            {
                // Already released.
            }
        }
    }

    private void RaiseState(string message)
    {
        _logger?.LogDebug("Live capture: {Message}", message);
        StateChanged?.Invoke(this, message);
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while shutting down.
        }
    }

    private static async Task SafeDelay(int milliseconds, CancellationToken cancellationToken) =>
        await SafeDelay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        Stop();

        try
        {
            if (_cts is not null)
                await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
        _startGate.Dispose();
        _encoderGate.Dispose();
    }

    /// <summary>One connected receiver. Chunks are dropped instead of queued when a listener falls behind.</summary>
    internal sealed class LiveSubscription : ILiveStreamSubscription
    {
        private readonly LiveCaptureService _owner;
        private readonly Channel<byte[]> _channel;
        private bool _disposed;

        public LiveSubscription(LiveCaptureService owner)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        public long BytesDelivered { get; private set; }

        public void Push(byte[] chunk)
        {
            if (_channel.Writer.TryWrite(chunk))
                BytesDelivered += chunk.Length;
        }

        public void Complete() => _channel.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;

                if (!_channel.Reader.TryRead(out var chunk) || chunk.Length == 0)
                    return 0;

                var copy = Math.Min(buffer.Length, chunk.Length);
                chunk.AsMemory(0, copy).CopyTo(buffer);
                return copy;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
            Complete();
            _owner.RemoveSubscription(this);
            return ValueTask.CompletedTask;
        }
    }
}
