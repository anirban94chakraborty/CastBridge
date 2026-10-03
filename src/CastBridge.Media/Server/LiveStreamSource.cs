namespace CastBridge.Media.Server;

/// <summary>
/// Where the media server gets live audio from. Implemented by the WASAPI loopback capture session;
/// an interface so the server (and its tests) do not depend on audio hardware.
/// </summary>
public interface ILiveStreamSource
{
    /// <summary>Opens a reader for a live session, starting capture if it is not running yet.</summary>
    Task<ILiveStreamSubscription?> SubscribeAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Content type the receiver should be told about for this session.</summary>
    string GetContentType(string sessionId);

    IReadOnlyList<string> ActiveSessions { get; }
}

public interface ILiveStreamSubscription : IAsyncDisposable
{
    /// <summary>Reads the next encoded chunk. Returns 0 bytes when the stream ends.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Bytes handed to this subscriber so far, for diagnostics.</summary>
    long BytesDelivered { get; }
}
