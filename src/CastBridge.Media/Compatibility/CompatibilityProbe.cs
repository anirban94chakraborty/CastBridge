using System.Buffers.Binary;
using CastBridge.Core;
using CastBridge.Core.Cast;
using Microsoft.Extensions.Logging;

namespace CastBridge.Media.Compatibility;

/// <summary>What a probe learned about one device.</summary>
public enum PlaybackVerdict
{
    /// <summary>Unknown yet: nobody has tested this device.</summary>
    Unknown,

    /// <summary>The speaker accepted the test and actually downloaded the audio.</summary>
    Ready,

    /// <summary>The device advertises that it will not run a media receiver (opencast=false).</summary>
    NotSupported,

    /// <summary>The device took the launch request but answered LAUNCH_ERROR.</summary>
    LaunchRefused,

    /// <summary>The device accepted the load but never asked for the audio.</summary>
    NoData,

    /// <summary>The connection was accepted, then dropped mid-request.</summary>
    Dropped,

    /// <summary>The probe could not talk to the device at all.</summary>
    Unreachable,
}

public sealed record CompatibilityResult(
    string DeviceId,
    string DeviceName,
    PlaybackVerdict Verdict,
    string Detail,
    DateTimeOffset CheckedUtc)
{
    public bool CanPlay => Verdict == PlaybackVerdict.Ready;

    /// <summary>One line for a status bar or a CLI table.</summary>
    public string Summary => $"{DeviceName}: {Describe(Verdict)} — {Detail}";

    public static string Describe(PlaybackVerdict verdict) => verdict switch
    {
        PlaybackVerdict.Ready => "can play from this PC",
        PlaybackVerdict.NotSupported => "cannot play from this PC",
        PlaybackVerdict.LaunchRefused => "refused the media receiver",
        PlaybackVerdict.NoData => "accepted the request but never fetched audio",
        PlaybackVerdict.Dropped => "dropped the connection mid-request",
        PlaybackVerdict.Unreachable => "unreachable",
        _ => "not tested yet",
    };
}

/// <summary>
/// Answers one question definitively: will this speaker actually play audio from this PC?
///
/// The honest way to answer is to do it, not to infer it from the device model. The probe casts a
/// half second of near-silence and watches its own media server for the bytes coming back. If the
/// device fetches them, casting works. If it does not, we know exactly which kind of "no" it was,
/// which is something a user can act on and a protocol error message cannot tell you.
///
/// Probes are cached, because launching a receiver app on someone's speaker should not happen on
/// every redraw, and every probe leaves the device stopped afterwards.
/// </summary>
public sealed class CompatibilityProbe
{
    private static readonly TimeSpan WaitForFetch = TimeSpan.FromSeconds(5);

    private readonly CastDeviceManager _devices;
    private readonly MediaPipeline _media;
    private readonly Server.MediaServer _server;
    private readonly ILogger<CompatibilityProbe>? _logger;
    private readonly Dictionary<string, CompatibilityResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CompatibilityProbe(
        CastDeviceManager devices,
        MediaPipeline media,
        Server.MediaServer server,
        ILoggerFactory? loggerFactory = null)
    {
        _devices = devices;
        _media = media;
        _server = server;
        _logger = loggerFactory?.CreateLogger<CompatibilityProbe>();
    }

    public CompatibilityResult? Cached(string deviceId) =>
        _cache.TryGetValue(deviceId, out var result) ? result : null;

    public void Forget(string deviceId) => _cache.Remove(deviceId);

    /// <summary>Tests a device, or returns the cached verdict if it has been tested recently.</summary>
    public async Task<CompatibilityResult> ProbeAsync(CastDevice device, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && Cached(device.Id) is { } cached)
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && Cached(device.Id) is { } again)
                return again;

            var result = await RunAsync(device, cancellationToken).ConfigureAwait(false);
            _cache[device.Id] = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CompatibilityResult> RunAsync(CastDevice device, CancellationToken cancellationToken)
    {
        // The cheap answer first. A device that says opencast=false will not run the receiver, and
        // there is no point waking it to be told so.
        if (!DevicePlaybackSupport.FromDevice(device).CanCastAnything)
        {
            return Verdict(device, PlaybackVerdict.NotSupported,
                "reports opencast=false, so it will not run Google's Default Media receiver");
        }

        var file = Path.Combine(Path.GetTempPath(), $"castbridge-probe-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(file, CreateNearSilenceWave());

            var before = _server.BytesServed;
            var item = new MediaItem { Path = file, Title = "CastBridge connection test" };

            try
            {
                await _devices.CastQueueAsync(device.Id, new[] { item }, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The protocol reports a dropped connection as TaskCanceledException, which is an
                // OperationCanceledException; only our own cancellation is a real cancellation.
                return Verdict(device, ClassifyLaunchFailure(ex), ex.Message);
            }

            // The receiver fetches asynchronously. Poll our own server rather than trusting the
            // device's "Playing" status, which these devices report even when they fetch nothing.
            var fetched = await WaitForBytesAsync(before, cancellationToken).ConfigureAwait(false);
            return fetched
                ? Verdict(device, PlaybackVerdict.Ready, $"the speaker downloaded the test audio from {_server.Port}")
                : Verdict(device, PlaybackVerdict.NoData,
                    "it accepted the request but never asked this PC for the audio");
        }
        finally
        {
            // Leave the speaker exactly as we found it, whatever happened above.
            try
            {
                await _devices.StopAsync(device.Id, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not stop {Device} after probing", device.Name);
            }

            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Could not delete the probe file");
            }
        }
    }

    private async Task<bool> WaitForBytesAsync(long before, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WaitForFetch;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (_server.BytesServed > before)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Turns a protocol exception into the kind of "no" it is. The distinction matters: a refused
    /// launch is a permanent hardware answer, while a dropped connection usually means the device is
    /// busy or is a group endpoint that will not take commands directly.
    /// </summary>
    private static PlaybackVerdict ClassifyLaunchFailure(Exception ex)
    {
        var message = ex.Message;

        if (message.Contains("could not be started", StringComparison.OrdinalIgnoreCase))
            return PlaybackVerdict.LaunchRefused;

        if (message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No connection", StringComparison.OrdinalIgnoreCase))
            return PlaybackVerdict.Dropped;

        return PlaybackVerdict.Unreachable;
    }

    private static CompatibilityResult Verdict(CastDevice device, PlaybackVerdict verdict, string detail) =>
        new(device.Id, device.Name, verdict, detail, DateTimeOffset.Now);

    /// <summary>
    /// Half a second of 16-bit mono audio at a volume low enough to be inaudible. The point is that
    /// the device downloads bytes, not that anyone hears a test tone.
    /// </summary>
    internal static byte[] CreateNearSilenceWave()
    {
        const int sampleRate = 44100;
        const ushort channels = 1;
        const int seconds = 1;
        const ushort bitsPerSample = 16;

        var samples = sampleRate * seconds * channels;
        var dataSize = samples * (bitsPerSample / 8);

        var wave = new byte[44 + dataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(0), 0x46464952);  // "RIFF"
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(36 + dataSize));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(8), 0x45564157);  // "WAVE"
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(12), 0x20746D66); // "fmt "
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);          // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);           // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), (ushort)(channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), bitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(36), 0x61746164);  // "data"
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)dataSize);

        // A constant non-zero DC offset: inaudible as a tone, but unmistakably audio if it is fetched.
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + (i * 2)), 8);

        return wave;
    }
}