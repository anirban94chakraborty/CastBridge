using System.Net;
using System.Net.Sockets;
using CastBridge.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace CastBridge.Media.Server;

public sealed class MediaServerOptions
{
    public int PreferredPort { get; init; } = 45455;

    /// <summary>How many consecutive ports to try when the preferred one is taken.</summary>
    public int PortSearchRange { get; init; } = 12;

    /// <summary>Media URL lifetime hint handed to receivers; long, because we are the origin.</summary>
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Serves the library, cover art and the live capture to Cast devices over HTTP.
///
/// Everything here exists because receivers are strict clients: they need byte ranges for seeking,
/// HEAD support, an exact Content-Length, CORS headers, and no chunked transfer encoding on files.
/// Kestrel is used instead of HttpListener because HttpListener needs an elevated URL ACL
/// reservation to bind a non-localhost prefix.
/// </summary>
public sealed class MediaServer : IAsyncDisposable
{
    private static readonly string[] Methods = { "GET", "HEAD" };

    private readonly MediaCatalog _catalog;
    private readonly ILiveStreamSource? _live;
    private readonly ILogger<MediaServer>? _logger;
    private readonly MediaServerOptions _options;

    private WebApplication? _app;
    private int _configuredPort;

    public MediaServer(
        MediaCatalog catalog,
        ILiveStreamSource? live = null,
        MediaServerOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _live = live;
        _options = options ?? new MediaServerOptions();
        _logger = loggerFactory?.CreateLogger<MediaServer>();
        PreferredPort = _options.PreferredPort;
        _configuredPort = _options.PreferredPort;
    }

    public int Port { get; private set; }

    /// <summary>Port to try on the next start; the app keeps this stable so firewall rules stay valid.</summary>
    public int PreferredPort { get; private set; }

    public void SetPreferredPort(int port)
    {
        if (port <= 0)
            return;

        PreferredPort = port;
        _configuredPort = port;
    }

    public bool IsRunning => _app is not null;

    public long RequestsServed { get; private set; }

    public long BytesServed { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Starts listening on all interfaces, walking up the port range if the port is busy.</summary>
    public async Task<int> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return Port;

        Exception? last = null;

        for (var attempt = 0; attempt < Math.Max(1, _options.PortSearchRange); attempt++)
        {
            var port = _configuredPort + attempt;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                // Cast receivers often open the connection with an HTTP/2 connection preface. An
                // HTTP/1-only listener accepts the TCP connection and then drops it, which looks
                // exactly like "the speaker never asked for the file". Answer both.
                kestrel.ListenAnyIP(port, listen => listen.Protocols =
                    Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2);
            });

            var app = builder.Build();
            MapEndpoints(app);

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                Port = port;
                _catalog.Port = port;
                _logger?.LogInformation("Media server listening on all interfaces, port {Port}", port);
                return port;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
            {
                last = ex;
                await app.DisposeAsync().ConfigureAwait(false);
                _logger?.LogDebug("Port {Port} unavailable: {Message}", port, ex.Message);
            }
        }

        LastError = $"Could not bind a media server port in {_configuredPort}-{_configuredPort + _options.PortSearchRange - 1}: {last?.Message}";
        throw new InvalidOperationException(LastError, last);
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;

        if (app is null)
            return;

        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Stopping the media server failed");
        }

        await app.DisposeAsync().ConfigureAwait(false);
    }

    public string BaseUrl(string? targetDeviceAddress = null) =>
        $"http://{_catalog.ResolveHost(targetDeviceAddress)}:{Port}";

    private void MapEndpoints(WebApplication app)
    {
        // Log everything that arrives, including paths we do not serve: when a receiver connects and
        // then nothing plays, the interesting question is usually "what did it actually ask for".
        app.Use(async (ctx, next) =>
        {
            _logger?.LogDebug(
                ">> {Method} {Path} range={Range} agent={Agent} proto={Protocol}",
                ctx.Request.Method,
                ctx.Request.Path.Value,
                ctx.Request.Headers.Range.ToString(),
                ctx.Request.Headers.UserAgent.ToString(),
                ctx.Request.Protocol);
            await next();
        });

        app.MapMethods("/media/{id}", Methods, async (HttpContext ctx, string id) =>
        {
            // Receivers are strict and silent: a request that fails is invisible from the device
            // side, so every arrival is logged to make "it never asked" distinguishable from
            // "it asked and we answered wrongly".
            _logger?.LogDebug(
                "{Method} /media/{Id} range={Range} agent={Agent} proto={Protocol}",
                ctx.Request.Method,
                id,
                ctx.Request.Headers.Range.ToString(),
                ctx.Request.Headers.UserAgent.ToString(),
                ctx.Request.Protocol);

            var entry = _catalog.Get(id);
            if (entry is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("unknown media id", ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            ApplyCors(ctx);

            if (entry.Kind == MediaEntryKind.Live)
            {
                await ServeLiveAsync(ctx, entry).ConfigureAwait(false);
                return;
            }

            if (entry.Path is null || !File.Exists(entry.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("file no longer exists", ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            await ServeFileAsync(ctx, entry.Path, entry.ContentType).ConfigureAwait(false);
        });

        app.MapMethods("/art/{id}", Methods, async (HttpContext ctx, string id) =>
        {
            ApplyCors(ctx);
            var path = _catalog.GetArtPath(id);
            if (path is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };

            await ServeFileAsync(ctx, path, contentType).ConfigureAwait(false);
        });

        app.MapMethods("/live/{id}", Methods, async (HttpContext ctx, string id) =>
        {
            ApplyCors(ctx);

            var entry = _catalog.Get(id);
            if (entry is null || entry.Kind != MediaEntryKind.Live)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("unknown live session", ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            await ServeLiveAsync(ctx, entry).ConfigureAwait(false);
        });

        app.MapGet("/health", (HttpContext ctx) =>
        {
            ApplyCors(ctx);
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                port = Port,
                mediaEntries = _catalog.Entries.Count,
                liveSessions = _live?.ActiveSessions ?? Array.Empty<string>(),
                requestsServed = RequestsServed,
                bytesServed = BytesServed,
            });

            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(payload, ctx.RequestAborted);
        });

        app.MapGet("/", (HttpContext ctx) =>
        {
            ApplyCors(ctx);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            return ctx.Response.WriteAsync(
                $"CastBridge media server\n\nPort: {Port}\nEntries: {_catalog.Entries.Count}\n" +
                $"Reachable from: {_catalog.ResolveHost()}\n\n" +
                "This server only serves the music you cast from this PC.\n",
                ctx.RequestAborted);
        });
    }

    private static void ApplyCors(HttpContext ctx)
    {
        // Receivers are browser engines; without CORS the media element refuses the stream.
        ctx.Response.Headers[HeaderNames.AccessControlAllowOrigin] = "*";
        ctx.Response.Headers[HeaderNames.AccessControlAllowHeaders] = "*";
        ctx.Response.Headers[HeaderNames.AccessControlAllowMethods] = "GET, HEAD, OPTIONS";
        ctx.Response.Headers[HeaderNames.AccessControlExposeHeaders] =
            $"{HeaderNames.ContentLength}, {HeaderNames.ContentRange}, {HeaderNames.AcceptRanges}";
    }

    private async Task ServeFileAsync(HttpContext ctx, string path, string contentType)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Cannot stat {Path}", path);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var rangeHeader = ctx.Request.Headers[HeaderNames.Range].ToString();
        var parseResult = RangeHeaderParser.TryParse(rangeHeader, length, out var range, out var reason);
        if (reason is not null)
            _logger?.LogDebug("Range header '{Header}' not honoured: {Reason}", rangeHeader, reason);

        var isHead = HttpMethods.IsHead(ctx.Request.Method);
        var response = ctx.Response;
        response.ContentType = contentType;
        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        response.Headers[HeaderNames.CacheControl] = $"public, max-age={(int)_options.CacheLifetime.TotalSeconds}";

        if (parseResult == RangeParseResult.Unsatisfiable)
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers[HeaderNames.ContentRange] = $"bytes */{length}";
            response.ContentLength = null;
            return;
        }

        long offset = 0;
        var bytesToSend = length;

        if (parseResult == RangeParseResult.Partial)
        {
            offset = range.Start;
            bytesToSend = range.Length;
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers[HeaderNames.ContentRange] = range.ToContentRange(length);
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        response.ContentLength = bytesToSend;

        if (isHead || bytesToSend == 0)
        {
            CountRequest(0);
            return;
        }

        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            if (offset > 0)
                file.Seek(offset, SeekOrigin.Begin);

            await CopyExactAsync(file, response.Body, bytesToSend, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The speaker closed the connection (seek, stop, or a new load); completely normal.
            _logger?.LogDebug("Speaker disconnected while reading {Path}", Path.GetFileName(path));
        }
        catch (IOException ex)
        {
            // Broken pipe when the receiver aborts mid-transfer.
            _logger?.LogDebug(ex, "Transfer of {Path} ended early", Path.GetFileName(path));
        }
    }

    private async Task ServeLiveAsync(HttpContext ctx, MediaEntry entry)
    {
        if (_live is null || entry.LiveSessionId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("live capture is not available", ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        var subscription = await _live.SubscribeAsync(entry.LiveSessionId, ctx.RequestAborted).ConfigureAwait(false);
        if (subscription is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("live capture could not start", ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        await using (subscription)
        {
            var response = ctx.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = _live.GetContentType(entry.LiveSessionId);

            // No Content-Length: the stream is endless, so the receiver must treat it as live.
            response.Headers[HeaderNames.CacheControl] = "no-store";
            response.Headers[HeaderNames.Expires] = "0";

            if (HttpMethods.IsHead(ctx.Request.Method))
                return;

            var buffer = new byte[16 * 1024];
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var read = await subscription.ReadAsync(buffer, ctx.RequestAborted).ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    await response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted).ConfigureAwait(false);
                    await response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                    BytesServed += read;
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Live listener disconnected");
            }
            catch (IOException ex)
            {
                _logger?.LogDebug(ex, "Live stream ended early");
            }
        }

        CountRequest(0);
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = count;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CountRequest(long bytes)
    {
        RequestsServed++;
        BytesServed += bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
