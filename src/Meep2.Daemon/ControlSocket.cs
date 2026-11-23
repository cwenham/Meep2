using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Meep2.Daemon;

/// <summary>
/// The daemon's control socket: HTTP with JSON on a Unix domain socket. It reads each request, asks
/// <see cref="DaemonAccess"/> what the client may do, hands it to the API, and writes back the answer, the events or the
/// problem. <c>/ws</c> carries the same API both ways over a WebSocket (<see cref="SocketSession"/>).
/// </summary>
/// <param name="access">What each client may do.</param>
/// <param name="route">The API, whether a request came by HTTP or over a WebSocket.</param>
/// <param name="stopping">Cancelled when the daemon stops, which ends WebSocket sessions.</param>
internal sealed class ControlSocket(DaemonAccess access, Func<ApiRequest, Task<ApiReply>> route, CancellationToken stopping) : IAsyncDisposable
{
    private const string PeerKey = "meep.peer";

    /// <summary>The largest body a request to the control socket may have: 1 MiB, the same as a <c>/ws</c> frame.</summary>
    internal const int MaxBody = SocketSession.MaxFrame;

    /// <summary>
    /// The largest body a request to start a pipeline may have, from a client that may start one: enough for
    /// <see cref="DaemonApi.MaxPipelineText"/> however JSON escapes it, which can be six bytes for one (<c><</c> for
    /// <c>&lt;</c>), with room for the rest of the request.
    /// </summary>
    internal const int MaxStartBody = 6 * DaemonApi.MaxPipelineText + 64 * 1024;

    private WebApplication? _app;

    /// <summary>
    /// Makes ready to listen on <paramref name="socket"/>: someone else's daemon may be on it, or an old one may have
    /// left it behind.
    /// </summary>
    /// <exception cref="DaemonException">Another daemon is using the socket, or its path is too long.</exception>
    public static void Prepare(string socket)
    {
        var limit = OperatingSystem.IsLinux() ? 107 : 103;
        if (Encoding.UTF8.GetByteCount(socket) > limit)
            throw new DaemonException(0, $"The socket path {socket} is longer than the {limit} characters this system allows. Set MEEP_HOME to a shorter folder.");
        // A folder only you can open, so nobody else can reach the socket.
        Meep2.Shared.PrivateFolders.Create(Path.GetDirectoryName(socket)!);
        if (!File.Exists(socket))
            return;
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(socket));
        }
        catch (SocketException)
        {
            File.Delete(socket); // left by a daemon that didn't stop cleanly
            return;
        }
        throw new DaemonException(0, $"A meep daemon is running already, on {socket}.");
    }

    /// <summary>Listens on <paramref name="socket"/>, which only its owner can open.</summary>
    public async Task StartAsync(string socket, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Bodies are read before a route's capability is checked, so they're kept small, as /ws frames are (S9).
            // Starting a pipeline from its text, for a client allowed to, raises the limit for that request (Q236).
            kestrel.Limits.MaxRequestBodySize = MaxBody;
            kestrel.ListenUnixSocket(socket, listen => listen.Use(next => connection =>
            {
                // Who's connecting, as the system tells it, for HandleAsync to decide what they may do.
                if (connection.Features.Get<IConnectionSocketFeature>()?.Socket is { } peer)
                    connection.Items[PeerKey] = PeerCredentials.UserOf(peer);
                return next(connection);
            }));
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        _app.Run(HandleAsync);
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A problem, as the API writes it.</summary>
    public static Action<Utf8JsonWriter> ProblemOf(DaemonException exception) =>
        writer => JsonSerializer.Serialize(writer, new Problem(exception.Message, exception.Details.Count > 0 ? exception.Details : null), DaemonJson.Default.Problem);

    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            var grant = access.GrantOf(context.Request.Headers.Authorization.ToString(), PeerOf(context));
            var capabilities = grant.Capabilities;
            // A stream or socket opened with a token ends when the token is revoked, as when the client goes (Q250).
            var aborted = context.RequestAborted;
            using var ending = grant.Revoked.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(aborted, grant.Revoked) : null;
            if (ending is not null)
                context.RequestAborted = ending.Token;
            try
            {
                if (context.Request.Path == "/ws")
                {
                    await SocketAsync(context, grant).ConfigureAwait(false);
                    return;
                }
                var request = await ReadRequestAsync(context, capabilities).ConfigureAwait(false);
                await ReplyAsync(context, await route(request).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client went away, or its token was revoked.
            }
            finally
            {
                context.RequestAborted = aborted;
            }
        }
        catch (DaemonException exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = exception.Status;
                context.Response.ContentType = "application/json; charset=utf-8";
                await WriteAsync(context, ProblemOf(exception)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away.
        }
    }

    // The user the system says is on the other end of the connection, if it said.
    private static uint? PeerOf(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items is { } items && items.TryGetValue(PeerKey, out var value) ? value as uint? : null;

    private static async Task ReplyAsync(HttpContext context, ApiReply reply)
    {
        switch (reply)
        {
            case Answer answer:
                context.Response.StatusCode = answer.Status;
                if (answer.Body is not null)
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await WriteAsync(context, answer.Body).ConfigureAwait(false);
                }
                break;
            case SubscribeReply:
                throw new DaemonException(400, "A subscription needs a WebSocket, so it can be changed as it goes. Connect to /ws and send {\"id\": 1, \"method\": \"POST\", \"path\": \"/subscribe\", \"json\": {…}}.");
            case Events events:
                await StartEventsAsync(context).ConfigureAwait(false);
                await foreach (var e in events.Stream(context.RequestAborted).ConfigureAwait(false))
                    await SendEventAsync(context, e).ConfigureAwait(false);
                break;
        }
    }

    // The most a request's body may be: more for starting a pipeline, from a client allowed to, than for anything else.
    private static int BodyLimit(HttpRequest request, Capabilities capabilities) =>
        HttpMethods.IsPost(request.Method) && request.Path == "/pipelines" && capabilities.HasFlag(Capabilities.Control) ? MaxStartBody : MaxBody;

    // An HTTP request as the API sees it. Its body is JSON if it says so, and otherwise text.
    private static async Task<ApiRequest> ReadRequestAsync(HttpContext context, Capabilities capabilities)
    {
        var request = context.Request;
        var body = await ReadBodyAsync(context, BodyLimit(request, capabilities)).ConfigureAwait(false);
        var isJson = body is not null && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
        return new ApiRequest(
            request.Method,
            request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(),
            ApiRequest.QueryOf(request.Query),
            isJson ? null : body,
            isJson ? ParseJson(body!) : null,
            capabilities,
            context.RequestAborted);
    }

    // A request's body, up to limit bytes, or null if it has none.
    private static async Task<string?> ReadBodyAsync(HttpContext context, int limit)
    {
        var request = context.Request;
        if (request.ContentLength > limit)
            throw new DaemonException(413, $"A request's body can be at most {limit} bytes."); // before any of it is read
        if (limit != MaxBody && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = limit;
        if (!(request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0))
            return null;
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        try
        {
            return await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new DaemonException(413, $"A request's body can be at most {limit} bytes.", innerException: exception);
        }
    }

    private static JsonElement ParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new DaemonException(400, $"That isn't JSON: {exception.Message}");
        }
    }

    // A WebSocket that carries the API both ways (Q108).
    private async Task SocketAsync(HttpContext context, AccessGrant grant)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw new DaemonException(400, "/ws is a WebSocket. Connect to it with a WebSocket client, or use the HTTP API.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var why = () => grant.Revoked.IsCancellationRequested ? "Its token was revoked." : stopping.IsCancellationRequested ? "The daemon is stopping." : "Goodbye.";
        await new SocketSession(socket, grant.Capabilities, route, ProblemOf).RunAsync(why, ending.Token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext context, Action<Utf8JsonWriter> body)
    {
        var buffer = ScratchBuffer.Rent();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
                body(writer);
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            ScratchBuffer.Return(buffer);
        }
    }

    private static async Task StartEventsAsync(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // One server-sent event, its data a line of JSON.
    private static async Task SendEventAsync(HttpContext context, ApiEvent e)
    {
        var buffer = ScratchBuffer.Rent();
        try
        {
            buffer.Write(Encoding.UTF8.GetBytes($"event: {e.Name}\ndata: "));
            using (var writer = new Utf8JsonWriter(buffer))
                e.Data(writer);
            buffer.Write("\n\n"u8);
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            ScratchBuffer.Return(buffer);
        }
    }
}
