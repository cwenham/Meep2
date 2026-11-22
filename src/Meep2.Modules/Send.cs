using System.Net.Http.Headers;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Send without an address replies to where a message came from, through its lease: an HTTP response for a request
/// from Listen.HTTP, or a message on a connection from Listen.TCP, Listen.WebSocket or Send itself. In Meeplang:
/// <c>Send text="Hello, {msg.Query['name']}"</c>, or <c>Send</c> to send the message itself back.
/// </summary>
/// <remarks>
/// <para>
/// Like every Send, it's a passthrough module: messages carry on downstream straight away, so modules after it
/// shouldn't expect the reply to have gone yet (Q157).
/// </para>
/// <para>
/// Without <c>text</c>, Send replies with the message itself: a body it has, such as a request's or a fetched
/// response's (streamed through if it was streamed), or else its text. On a WebSocket, bytes whose media type isn't
/// text go as a binary message, and everything else as text (Q160).
/// </para>
/// <para>
/// An error is answered with <c>error-reply</c>, "Something went wrong." unless set, and status 500 unless
/// <c>error-status</c> says otherwise. These are worked out from the error, so <c>error-reply="Failed: {msg.Error}"</c>
/// includes its description. <c>details=true</c> sends everything known about it instead, which can show addresses
/// and exception text to whoever asked (Q98). An error Send answers is still logged, unless a module after it handles
/// the error, but it doesn't count towards stopping the pipeline, so bad requests can't stop a server (Q100).
/// </para>
/// <para>
/// An HTTP request can only be answered once; trying again is an error. A connection can be written to any number of
/// times. On TCP, each write ends the way the connection's messages do (a line, or Listen's <c>end</c>) unless
/// <c>end</c> says otherwise. <c>final=true</c> closes the connection after writing. A WebSocket client that's too
/// slow to keep up has replies dropped and logged (Q153).
/// </para>
/// </remarks>
[Verb("Send", Inflection = "Reply")]
[Role(VerbRole.Outlet)]
public sealed class SendReply(SendReplyParameters parameters) : APassthrough<Message>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        var leases = message.Leases.Where(lease => lease is HttpRequestLease or IConnectionLease).ToList();
        if (leases.Count == 0)
        {
            // An error with no request behind it, such as one from Listen itself, is passed on in Send's words.
            context.EmitError(message is ErrorMessage error
                ? $"Send had no address, and no request to reply to with this error: {error.Error}"
                : "Send without an address replies to the request or connection a message came from, but this one didn't come from one. Give Send an address to send it somewhere.");
            return;
        }

        string? text;
        int status;
        string? contentType;
        try
        {
            if (message is ErrorMessage error)
            {
                text = parameters.Details ? Describe(error) : parameters.ErrorReply.Evaluate(message);
                status = parameters.ErrorStatus?.Evaluate(message) ?? 500;
                contentType = Bodies.PlainText;
            }
            else
            {
                text = parameters.Text?.Evaluate(message);
                status = parameters.Status?.Evaluate(message) ?? 200;
                contentType = parameters.ContentType?.Evaluate(message);
            }
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        foreach (var lease in leases)
        {
            try
            {
                switch (lease)
                {
                    case HttpRequestLease http:
                        if (!await AnswerAsync(http, message, text, status, contentType, cancellationToken).ConfigureAwait(false))
                        {
                            context.EmitError($"The request for {http.Url} has already been answered, so Send couldn't answer it again.");
                            continue;
                        }
                        break;
                    case IConnectionLease connection:
                        await connection.ReplyAsync(message, text, parameters.End, parameters.Final, cancellationToken).ConfigureAwait(false);
                        break;
                }
                if (message is ErrorMessage answered)
                    context.Answered(answered);
            }
            catch (QueueFullException exception)
            {
                Dropped(context, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException)
            {
                context.EmitError($"Couldn't reply to {lease}: {exception.Message}", exception);
            }
        }
    }

    /// <summary>
    /// Logs a message dropped for a client too slow to keep up (Q153). It's logged, but doesn't count towards stopping
    /// the pipeline, so slow clients can't stop a server.
    /// </summary>
    internal static void Dropped(ModuleContext context, string why)
    {
        var error = new ErrorMessage { Error = why };
        context.Answered(error);
        context.Emit(error);
    }

    private static async Task<bool> AnswerAsync(HttpRequestLease http, Message message, string? text, int status, string? contentType, CancellationToken cancellationToken)
    {
        if (text is not null)
            return await http.AnswerAsync(status, contentType ?? Bodies.PlainText, Encoding.UTF8.GetBytes(text), null, cancellationToken).ConfigureAwait(false);

        switch (message)
        {
            case IHasBody { IsStreamed: true } streamed:
            {
                await using var body = streamed.OpenBody();
                return await http.AnswerAsync(status, contentType ?? streamed.ContentType, default, body, cancellationToken).ConfigureAwait(false);
            }
            case IHasBody body:
                return await http.AnswerAsync(status, contentType ?? body.ContentType, await Frames.BytesOfAsync(body, cancellationToken).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false);
            case JsonMessage json:
                return await http.AnswerAsync(status, contentType ?? "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Text), null, cancellationToken).ConfigureAwait(false);
            default:
                return await http.AnswerAsync(status, contentType ?? Bodies.PlainText, Encoding.UTF8.GetBytes(message.ToText()), null, cancellationToken).ConfigureAwait(false);
        }
    }

    // Everything known about an error: its description, and the exception behind it with its stack trace.
    private static string Describe(ErrorMessage error) =>
        error.Exception is { } exception ? $"{error.Error}\n\n{exception}" : error.Error;
}

/// <summary>Parameters for <see cref="SendReply"/>, a Send without an address.</summary>
public sealed record SendReplyParameters
{
    /// <summary>What to send, worked out for each message. Without it, Send sends the message itself.</summary>
    public Computed<string>? Text { get; init; }

    /// <summary>The HTTP status: 200 unless set.</summary>
    public Computed<int>? Status { get; init; }

    /// <summary>The HTTP media type. Without it, the message's own, or text.</summary>
    public Computed<string>? ContentType { get; init; }

    /// <summary>What to send for an error, worked out from the error (Q98).</summary>
    [ForErrors]
    public Computed<string> ErrorReply { get; init; } = Computed<string>.Constant("Something went wrong.");

    /// <summary>The HTTP status for an error: 500 unless set.</summary>
    [ForErrors]
    public Computed<int>? ErrorStatus { get; init; }

    /// <summary>
    /// Sends everything known about an error, instead of <see cref="ErrorReply"/>: its description, and the exception
    /// behind it. Meant for tools and testing, since it can show internal details to whoever asked.
    /// </summary>
    public bool Details { get; init; }

    /// <summary>
    /// What to write after the text on a TCP connection. Without it, what ends the connection's own messages: a line
    /// break, or the <c>end</c> its Listen or Send was given.
    /// </summary>
    public string? End { get; init; }

    /// <summary>Closes the connection after writing to it.</summary>
    public bool Final { get; init; }
}

/// <summary>
/// Send's Broadcast inflection: sends each message to every client connected to a Listen.WebSocket, named with
/// <c>as</c> (Q152). In Meeplang: <c>Listen "ws://0.0.0.0:8080/chat/" as Room</c>, then <c>Send "Room"</c>.
/// </summary>
/// <remarks>
/// A client too slow to keep up has messages dropped and logged rather than holding up the others (Q153). Errors are
/// left alone, so they carry on to whatever handles them.
/// </remarks>
[Verb("Send", Inflection = "Broadcast")]
[Role(VerbRole.Outlet)]
public sealed class SendBroadcast(SendBroadcastParameters parameters) : APassthrough<Message>, IChosenByParameters<SendBroadcastParameters>
{
    /// <summary>Takes the name of a Listen: a word, rather than an address.</summary>
    public static string? WhyNot(SendBroadcastParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var to = parameters.To;
        return to.Length > 0 && (char.IsLetter(to[0]) || to[0] == '_') && to.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? null
            : $"sends to a Listen by its name, and {to} isn't a name";
    }

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage error)
        {
            context.LeftAlone(error);
            return;
        }
        if (!context.Shared.TryGetValue(WebSocketListen.RoomKey(parameters.To), out var found) || found is not WebSocketListen.Room room)
        {
            context.EmitError($"Send has nowhere called {parameters.To} to send to. Name a Listen.WebSocket with as {parameters.To}, in this pipeline.");
            return;
        }

        Frame frame;
        try
        {
            frame = await Frames.ForAsync(message, parameters.Text?.Evaluate(message), cancellationToken).ConfigureAwait(false);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        foreach (var connection in room.Connections.Values)
        {
            try
            {
                connection.Queue(frame);
            }
            catch (QueueFullException exception)
            {
                SendReply.Dropped(context, exception.Message);
            }
            catch (IOException)
            {
                // It closed in the meantime.
            }
        }
    }
}

/// <summary>Parameters for <see cref="SendBroadcast"/>.</summary>
public sealed record SendBroadcastParameters
{
    /// <summary>The name of the Listen.WebSocket to send to, as given with <c>as</c>.</summary>
    [Positional]
    public required string To { get; init; }

    /// <summary>What to send, worked out for each message. Without it, Send sends the message itself.</summary>
    public Computed<string>? Text { get; init; }
}

/// <summary>
/// Send's WebSocket inflection: sends each message to a ws:// or wss:// address, and emits what the server sends back
/// as <see cref="WebSocketText"/> or <see cref="WebSocketBinary"/> (Q158). In Meeplang:
/// <c>Send "wss://feed.example.com/prices" text="{msg.Text}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// One connection to each address is shared by every Send in the process (Q156). It opens with the first message,
/// trying <c>tries</c> times, <c>retry-delay</c> apart (3 tries, 1 second apart, unless set), and reconnects the same
/// way when it drops (Q157). A message that can't be sent within <c>timeout</c> becomes an error, made from the
/// message.
/// </para>
/// <para>
/// Messages carry on downstream straight away, so modules after Send shouldn't expect them to have been sent yet
/// (Q157). What the server sends comes out under every Send using the connection, carrying a lease on it, so a Send
/// without an address replies to the server. With <c>connections=true</c>, Send also emits
/// <see cref="WebSocketConnected"/> and <see cref="WebSocketDisconnected"/>.
/// </para>
/// <para>Errors are left alone, so they carry on to whatever handles them.</para>
/// </remarks>
[Verb("Send", Inflection = "WebSocket")]
[Role(VerbRole.Outlet)]
[Emits(typeof(WebSocketText))]
[Emits(typeof(WebSocketBinary))]
[Emits(typeof(WebSocketConnected), When = nameof(SendWebSocketParameters.Connections))]
[Emits(typeof(WebSocketDisconnected), When = nameof(SendWebSocketParameters.Connections))]
public sealed class SendWebSocket(SendWebSocketParameters parameters) : ConnectionSend<SendWebSocketParameters>(parameters), IChosenByParameters<SendWebSocketParameters>
{
    /// <summary>Takes ws:// and wss:// addresses.</summary>
    public static string? WhyNot(SendWebSocketParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var url = parameters.Url.Evaluate(message);
        return IsWebSocket(url, out _) ? null : $"needs a ws:// or wss:// address, not {url}";
    }

    private static bool IsWebSocket(string address, out Uri? uri) =>
        Uri.TryCreate(address, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss);

    private protected override SharedClient? ClientFor(string url, ModuleContext context)
    {
        if (!IsWebSocket(url, out var uri))
        {
            context.EmitError($"Send.WebSocket needs a ws:// or wss:// address, not {url}");
            return null;
        }

        // A Meep host gets a connection from this Send alone, so its origin is exact (Q216); anywhere else, one connection
        // to the address is shared (Q156). A Send that learns the host is Meep, or that it no longer is, moves.
        // Only hosts it may tell are asked or given the origin (S11).
        var endpoint = Peers.EndpointOf(uri!);
        var hostId = context.HostId is not null && MeepHosts.MayTell(endpoint, uri!.Host) ? context.HostId : null;
        var origin = hostId is not null ? context.Origin : null;
        var own = origin is not null && MeepHosts.IsMeep(endpoint, uri!.Host, uri.Port);
        if (origin is not null)
            Joined.Leave(WebSocketClient.Key(uri!, own ? null : origin));
        return Joined.Join(WebSocketClient.Key(uri!, own ? origin : null), context, Parameters.Connections, () => new WebSocketClient(uri!, Parameters, hostId, own ? origin : null));
    }
}

/// <summary>Parameters for <see cref="SendWebSocket"/>.</summary>
public sealed record SendWebSocketParameters : SendConnectionParameters
{
    /// <summary>How often to ping the server, closing the connection if it stops answering. 30 seconds unless set; 0 turns it off.</summary>
    public TimeSpan Ping
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Ping), "The ping can't be less than zero.");
    } = TimeSpan.FromSeconds(30);

    /// <summary>How many messages the connection queues to send, beyond which Send waits. 256 unless set.</summary>
    public int MaxQueued
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxQueued), "A connection must be able to queue at least 1 message.");
    } = 256;
}

/// <summary>
/// Send's TCP inflection: writes each message to a tcp:// address, and emits what the server sends back as
/// <see cref="TcpMessage"/>. In Meeplang: <c>Send "tcp://printer.local:9100" text="{msg.Text}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each message ends with a line break, unless <c>end</c> says otherwise, and what the server sends is split into
/// messages the same way, or by <c>length</c>, as Listen.TCP does. Connections are shared and reconnect as
/// Send.WebSocket's do (Q156, Q157), and what the server sends carries a lease on the connection, so a Send without an
/// address replies to it. With <c>connections=true</c>, Send also emits <see cref="TcpConnected"/> and
/// <see cref="TcpDisconnected"/>.
/// </para>
/// <para>Errors are left alone, so they carry on to whatever handles them.</para>
/// </remarks>
[Verb("Send", Inflection = "TCP")]
[Role(VerbRole.Outlet)]
[Emits(typeof(TcpMessage))]
[Emits(typeof(TcpConnected), When = nameof(SendTcpParameters.Connections))]
[Emits(typeof(TcpDisconnected), When = nameof(SendTcpParameters.Connections))]
public sealed class SendTcp(SendTcpParameters parameters) : ConnectionSend<SendTcpParameters>(parameters), IChosenByParameters<SendTcpParameters>
{
    /// <summary>Takes tcp:// addresses with a port, and one way of telling where the server's messages end.</summary>
    public static string? WhyNot(SendTcpParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var url = parameters.Url.Evaluate(message);
        if (!Endpoints.TryTcp(url, out _, out _))
            return $"needs a tcp:// address with a port, not {url}";
        if (parameters.End is not null && parameters.Length is not null)
            return "takes end or length to tell where a message ends, not both";
        return null;
    }

    private protected override string? End => Parameters.End;

    private protected override SharedClient? ClientFor(string url, ModuleContext context)
    {
        if (!Endpoints.TryTcp(url, out var host, out var port))
        {
            context.EmitError($"Send.TCP needs a tcp:// address with a port, not {url}");
            return null;
        }
        return Joined.Join(TcpServerClient.Key(host, port), context, Parameters.Connections, () => new TcpServerClient(url, host, port, Parameters));
    }
}

/// <summary>Parameters for <see cref="SendTcp"/>.</summary>
public sealed record SendTcpParameters : SendConnectionParameters
{
    /// <summary>
    /// The text that ends each message, both ways, such as <c>"\r\n.\r\n"</c>. Without it, or <see cref="Length"/>, each
    /// line is a message.
    /// </summary>
    public string? End
    {
        get;
        init => field = value is null || value.Length > 0 ? value : throw new ArgumentException("The end of a message needs at least one character.", nameof(End));
    }

    /// <summary>The size of every message from the server, in bytes, for a protocol whose messages are all the same length.</summary>
    public int? Length
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Length), "A message's length must be at least 1 byte.");
    }
}

/// <summary>
/// Send's HTTP inflection: POSTs each message to an http:// or https:// address, and emits the response as
/// <see cref="HttpResponse"/>, as Fetch does for GET. In Meeplang: <c>Send "https://api.example.com/claims"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The body is <c>text</c> if given, or else the message itself: a body it has, with its media type, JSON as JSON, or
/// its text. <c>content-type</c> overrides the media type.
/// </para>
/// <para>
/// A status that isn't a success becomes an <see cref="HttpErrorMessage"/>. A network failure, a timeout, or a body over
/// the size limit becomes an <see cref="ErrorMessage"/>. Messages carry on downstream straight away, as with every Send,
/// so the response comes after them. Errors are left alone, so they carry on to whatever handles them.
/// </para>
/// </remarks>
[Verb("Send", Inflection = "HTTP")]
[Role(VerbRole.Outlet)]
[Emits(typeof(HttpResponse))]
public sealed class SendHttp(SendHttpParameters parameters) : APassthrough<Message>, IChosenByParameters<SendHttpParameters>, ISendsTo
{
    /// <inheritdoc/>
    public Computed<string> Address => parameters.Url;

    /// <summary>Takes http and https addresses.</summary>
    public static string? WhyNot(SendHttpParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var url = parameters.Url.Evaluate(message);
        return Addresses.IsWeb(url, out _) ? null : $"needs an http or https address, not {url}";
    }

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage error)
        {
            context.LeftAlone(error);
            return;
        }
        if (Outgoing(message, context) is not { } request)
            return;
        var (url, uri, text, contentType) = request;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(parameters.Timeout);
        var endpoint = Peers.EndpointOf(uri);
        var tally = endpoint is null ? Tally.None : context.Destination(endpoint);
        HttpResponseMessage? answer = null;
        try
        {
            using var content = await ContentAsync(message, text, contentType, context, tally, timeout.Token).ConfigureAwait(false);
            // A body is counted as it's sent, each time a redirect sends it again; a stream as it's read.
            var reached = await HttpFetch.SendAsync(HttpMethod.Post, uri, content, HttpCompletionOption.ResponseContentRead, context, timeout.Token, sending: sent => context.CountBytes(0, sent)).ConfigureAwait(false);
            using var response = answer = reached.Response;
            tally = reached.Counted(endpoint, tally, context);
            tally.Count(sent: 1, received: 1, bytesSent: content is StreamContent ? 0 : content.Headers.ContentLength ?? 0);
            tally.Status((int)response.StatusCode);
            var body = await HttpFetch.ReadBodyAsync(response.Content, url, parameters.MaxSize, timeout.Token).ConfigureAwait(false);
            context.CountBytes(body.Length, 0);
            tally.Count(bytesReceived: body.Length);
            context.Emit(HttpFetch.MessageFor(url, response, body));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            HttpFetch.Failed(answer, tally);
            context.EmitError($"{url} didn't answer within {HttpFetch.Describe(parameters.Timeout)}");
        }
        catch (HttpRequestException exception)
        {
            HttpFetch.Failed(answer, tally);
            context.EmitError($"Couldn't send to {url}: {exception.Message}", exception);
        }
        catch (HttpFetch.TooLargeException exception)
        {
            HttpFetch.Failed(answer, tally);
            context.EmitError(exception.Message, exception);
        }
    }

    // The address, text and content type worked out for a message. Null, having emitted why, if they can't be, or
    // the address isn't on the web.
    private Request? Outgoing(Message message, ModuleContext context)
    {
        string url;
        string? text;
        string? contentType;
        try
        {
            url = parameters.Url.Evaluate(message);
            text = parameters.Text?.Evaluate(message);
            contentType = parameters.ContentType?.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return null;
        }
        if (Addresses.IsWeb(url, out var uri))
            return new Request(url, uri!, text, contentType);
        context.EmitError($"Send.HTTP needs an http or https address, not {url}");
        return null;
    }

    // What one message sends: where to, and the text and its type, if they're given.
    private sealed record Request(string Url, Uri Uri, string? Text, string? ContentType);

    private static async Task<HttpContent> ContentAsync(Message message, string? text, string? contentType, ModuleContext context, Tally tally, CancellationToken cancellationToken)
    {
        if (text is not null)
            return WithType(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), contentType ?? Bodies.PlainText);
        switch (message)
        {
            case IHasBody { IsStreamed: true } streamed:
                return WithType(new StreamContent(new CountingStream(streamed.OpenBody(), (read, _) =>
                {
                    context.CountBytes(0, read);
                    tally.Count(bytesSent: read);
                })), contentType ?? streamed.ContentType);
            case IHasBody body:
                return WithType(new ReadOnlyMemoryContent(await Frames.BytesOfAsync(body, cancellationToken).ConfigureAwait(false)), contentType ?? body.ContentType);
            case JsonMessage json:
                return WithType(new ByteArrayContent(Encoding.UTF8.GetBytes(json.Text)), contentType ?? "application/json; charset=utf-8");
            default:
                return WithType(new ByteArrayContent(Encoding.UTF8.GetBytes(message.ToText())), contentType ?? Bodies.PlainText);
        }
    }

    private static HttpContent WithType(HttpContent content, string? contentType)
    {
        if (contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var media))
            content.Headers.ContentType = media;
        return content;
    }
}

/// <summary>Parameters for <see cref="SendHttp"/>.</summary>
public sealed record SendHttpParameters
{
    /// <summary>The address to POST to, worked out for each message.</summary>
    [Positional]
    public required Computed<string> Url { get; init; }

    /// <summary>The body to send, worked out for each message. Without it, Send sends the message itself.</summary>
    public Computed<string>? Text { get; init; }

    /// <summary>The body's media type. Without it, the message's own, or text.</summary>
    public Computed<string>? ContentType { get; init; }

    /// <summary>How long to wait for the whole response. 100 seconds unless set.</summary>
    public TimeSpan Timeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Timeout), "The timeout must be longer than zero.");
    } = TimeSpan.FromSeconds(100);

    /// <summary>The largest response body to read, in bytes. 16 MiB unless set.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 16 * 1024 * 1024;
}

/// <summary>The shared connections one Send module uses, by address, and sending through them.</summary>
internal sealed class Connections : IAsyncDisposable
{
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private Outlet? _outlet;

    /// <summary>Leaves a connection this module joined, if it did, which closes once nothing uses it.</summary>
    public void Leave(string key)
    {
        lock (_subscriptions)
        {
            if (_subscriptions.Remove(key, out var subscription))
                subscription.Client.Leave(subscription);
        }
    }

    /// <summary>The connection for an address, joining it the first time this module uses it.</summary>
    public SharedClient Join(string key, ModuleContext context, bool connections, Func<SharedClient> create)
    {
        lock (_subscriptions)
        {
            if (_subscriptions.TryGetValue(key, out var existing))
                return existing.Client;
            var subscription = new Subscription(_outlet ??= context.Outlet, connections);
            SharedClient.Join(key, create, subscription);
            _subscriptions[key] = subscription;
            return subscription.Client;
        }
    }

    /// <summary>Sends a message, turning a failure or a timeout into an error made from it.</summary>
    /// <param name="client">The connection to send through.</param>
    /// <param name="message">The message, for its body when there's no text, and to make an error from.</param>
    /// <param name="text">What to send, worked out from the message, or null to send the message itself.</param>
    /// <param name="end">What ends the message on TCP, or null for the connection's own.</param>
    /// <param name="parameters">How hard to try: the Send's timeout, tries and the delay between them.</param>
    /// <param name="context">The Send's context, for counting and for errors.</param>
    /// <param name="cancellationToken">Cancelled when the pipeline stops.</param>
    public static async Task SendAsync(SharedClient client, Message message, string? text, string? end, SendConnectionParameters parameters, ModuleContext context, CancellationToken cancellationToken)
    {
        var timeout = parameters.Timeout;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var tally = context.Destination(client.Endpoint);
        try
        {
            var sent = await client.SendAsync(message, text, end, parameters.Tries, parameters.RetryDelay, limit.Token).ConfigureAwait(false);
            context.CountBytes(0, sent);
            tally.Count(sent: 1, bytesSent: sent);
            tally.Meep(client.MeepId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tally.Count(sent: 1, failed: 1);
            context.EmitError($"Couldn't send to {client.Address} within {HttpFetch.Describe(timeout)}.");
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            tally.Count(sent: 1, failed: 1);
            context.EmitError($"Couldn't send to {client.Address}: {exception.Message}", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions.Values)
                subscription.Client.Leave(subscription);
            _subscriptions.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
