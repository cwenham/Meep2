using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Meep2.Daemon;

/// <summary>A request to the daemon's API, whether it came by HTTP or over a WebSocket.</summary>
/// <param name="Method">GET, POST or DELETE.</param>
/// <param name="Parts">The path's parts, unescaped.</param>
/// <param name="Query">The query's values.</param>
/// <param name="Text">A text body, if any.</param>
/// <param name="Json">A JSON body, if any.</param>
/// <param name="Capabilities">What the client may do.</param>
/// <param name="Aborted">Cancelled if the client goes away.</param>
internal sealed record ApiRequest(
    string Method,
    string[] Parts,
    IReadOnlyDictionary<string, string> Query,
    string? Text,
    JsonElement? Json,
    Capabilities Capabilities,
    CancellationToken Aborted);

/// <summary>What the API gives back: an <see cref="Answer"/>, or a stream of <see cref="Events"/>.</summary>
internal abstract record ApiReply;

/// <summary>A status, and JSON if there's anything to say.</summary>
internal sealed record Answer(int Status, Action<Utf8JsonWriter>? Body) : ApiReply
{
    public static Answer Json<T>(int status, T value, JsonTypeInfo<T> type) =>
        new(status, writer => JsonSerializer.Serialize(writer, value, type));
}

/// <summary>Events until the stream ends or the client stops listening. The last is called end, unless the client stopped it.</summary>
internal sealed record Events(Func<CancellationToken, IAsyncEnumerable<ApiEvent>> Stream) : ApiReply;

/// <summary>One event in a stream: its name, and its data as JSON.</summary>
internal readonly record struct ApiEvent(string Name, Action<Utf8JsonWriter> Data);

/// <summary>
/// The daemon's API over a WebSocket (Q108): any number of requests at once, both ways, on one connection. Each frame
/// is a JSON object, as text.
/// </summary>
/// <remarks>
/// <para>A request names its method and path as the HTTP API has them, with an id of the client's choosing:</para>
/// <code>{"id": 1, "method": "GET", "path": "/pipelines/ticker/stats?every=1s"}</code>
/// <para>
/// A body goes in <c>json</c>, any JSON value, or <c>text</c>, a string, as for
/// <c>{"id": 2, "method": "POST", "path": "/pipelines/ticker/modules/WriteLine/inject", "text": "hello"}</c>. The
/// method is GET unless it says otherwise.
/// </para>
/// <para>
/// The daemon answers with the same id. An answer has a status, and a body unless there's nothing to say:
/// <c>{"id": 1, "status": 200, "body": {…}}</c>. A refusal's body is a problem: <c>{"error": "…"}</c>. A stream
/// sends events instead, <c>{"id": 1, "event": "stats", "data": {…}}</c>, until one called <c>end</c>. The client
/// stops a stream with <c>{"id": 1, "cancel": true}</c>, and the daemon then sends an end whose reason is
/// <c>cancelled</c>.
/// </para>
/// </remarks>
internal sealed class SocketSession(
    WebSocket socket,
    Capabilities capabilities,
    Func<ApiRequest, Task<ApiReply>> route,
    Func<DaemonException, Action<Utf8JsonWriter>> problem)
{
    /// <summary>The longest frame a client may send.</summary>
    public const int MaxFrame = 1 << 20;

    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, bool> _working = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sending = SendAllAsync(cancellationToken);
        try
        {
            await ReceiveAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The client went, or the daemon is stopping.
        }
        finally
        {
            foreach (var stream in _streams.Values)
                await stream.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_working.Keys).ConfigureAwait(false);
            _outgoing.Writer.TryComplete();
            await sending.ConfigureAwait(false);
            await CloseAsync(cancellationToken.IsCancellationRequested ? "The daemon is stopping." : "Goodbye.").ConfigureAwait(false);
        }
    }

    private async Task ReceiveAllAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (socket.State == WebSocketState.Open)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxFrame)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"Frames can be at most {MaxFrame} bytes.", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                return;
            Handle(buffer.WrittenMemory);
        }
    }

    // One frame from the client: a request, or a stream to cancel.
    private void Handle(ReadOnlyMemory<byte> frame)
    {
        JsonElement request;
        try
        {
            using var document = JsonDocument.Parse(frame);
            request = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            Reply(null, 400, problem(new DaemonException(400, $"That frame isn't JSON: {exception.Message}")));
            return;
        }
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("id", out var idValue)
            || idValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            Reply(null, 400, problem(new DaemonException(400, "Each frame is a JSON object with an id, a string or a number, as in {\"id\": 1, \"method\": \"GET\", \"path\": \"/pipelines\"}.")));
            return;
        }
        var id = idValue.GetRawText();
        if (request.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True)
        {
            if (_streams.TryGetValue(id, out var stream))
                stream.Cancel();
            return;
        }
        var work = Task.Run(() => AnswerAsync(id, request));
        _working.TryAdd(work, true);
        _ = work.ContinueWith(done => _working.TryRemove(done, out _), TaskScheduler.Default);
    }

    private async Task AnswerAsync(string id, JsonElement frame)
    {
        using var aborted = new CancellationTokenSource();
        if (!_streams.TryAdd(id, aborted))
        {
            Reply(id, 409, problem(new DaemonException(409, $"Request {id} is still going. Give each request its own id.")));
            return;
        }
        try
        {
            var reply = await route(Read(frame, aborted.Token)).ConfigureAwait(false);
            if (reply is Answer answer)
            {
                Reply(id, answer.Status, answer.Body);
                return;
            }
            var events = (Events)reply;
            try
            {
                await foreach (var e in events.Stream(aborted.Token).ConfigureAwait(false))
                    Send(id, e.Name, e.Data);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                // Cancelled by the client, or the connection is closing.
            }
            if (aborted.IsCancellationRequested)
            {
                Send(id, "end", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "cancelled");
                    writer.WriteEndObject();
                });
            }
        }
        catch (DaemonException exception)
        {
            Reply(id, exception.Status, problem(exception));
        }
        finally
        {
            _streams.TryRemove(id, out _);
        }
    }

    // A frame as a request, with the client's capabilities.
    private ApiRequest Read(JsonElement frame, CancellationToken aborted)
    {
        var method = frame.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.ToUpperInvariant() : "GET";
        if (!frame.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path)
            throw new DaemonException(400, "A request needs a path, as the HTTP API has it, such as \"/pipelines\".");
        var uri = new Uri(new Uri("http://meep/"), path);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            query[key] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return new ApiRequest(
            method,
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(),
            query,
            frame.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
            frame.TryGetProperty("json", out var json) ? json : null,
            capabilities,
            aborted);
    }

    private void Reply(string? id, int status, Action<Utf8JsonWriter>? body) => Write(id, writer =>
    {
        writer.WriteNumber("status", status);
        if (body is not null)
        {
            writer.WritePropertyName("body");
            body(writer);
        }
    });

    private void Send(string id, string name, Action<Utf8JsonWriter> data) => Write(id, writer =>
    {
        writer.WriteString("event", name);
        writer.WritePropertyName("data");
        data(writer);
    });

    private void Write(string? id, Action<Utf8JsonWriter> rest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            if (id is null)
                writer.WriteNullValue();
            else
                writer.WriteRawValue(id, skipInputValidation: true);
            rest(writer);
            writer.WriteEndObject();
        }
        _outgoing.Writer.TryWrite(buffer.WrittenSpan.ToArray());
    }

    // One sender, so frames never interleave.
    private async Task SendAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                    continue;
                await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The connection is going; what's left can't be sent.
        }
    }

    private async Task CloseAsync(string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // It went first.
        }
    }
}
