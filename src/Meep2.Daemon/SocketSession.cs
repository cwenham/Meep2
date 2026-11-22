using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Meep2.Daemon;

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
/// <para>
/// A subscription (<c>{"id": "s", "method": "POST", "path": "/subscribe", "json": {…}}</c>) is a stream that the client
/// changes as it goes, with <c>{"id": "s", "change": {…}}</c>, a merge patch to what it asked for, and
/// <c>{"id": "s", "resend": true}</c>, to have everything sent whole again. One that asked for CBOR sends its events as
/// binary frames, each a CBOR map with the same id, event and data. See <see cref="Subscriptions.Subscription"/>.
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

    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Subscriptions.Subscription> _subscriptions = new(StringComparer.Ordinal);

    // Changes that came for a request before it turned out to be a subscription and started, applied once it has.
    private readonly Dictionary<string, List<JsonElement>> _early = new(StringComparer.Ordinal);
    private readonly Lock _subscribing = new();
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
        if (request.TryGetProperty("change", out _) || request.TryGetProperty("resend", out _))
        {
            Subscriptions.Subscription? subscription;
            lock (_subscribing)
            {
                if (!_subscriptions.TryGetValue(id, out subscription) && _streams.ContainsKey(id))
                {
                    // Sent straight after the subscription, which hasn't started yet.
                    if (!_early.TryGetValue(id, out var early))
                        _early[id] = early = [];
                    early.Add(request);
                    return;
                }
            }
            if (subscription is null)
                Reply(id, 404, problem(new DaemonException(404, $"There's no subscription {id} on this connection to change. Start one with {{\"id\": {id}, \"method\": \"POST\", \"path\": \"/subscribe\", \"json\": {{…}}}}.")));
            else
                Apply(subscription, request);
            return;
        }

        // The id is taken as the frame is read, so a frame after it for the same id finds it.
        var aborted = new CancellationTokenSource();
        if (!_streams.TryAdd(id, aborted))
        {
            aborted.Dispose();
            Reply(id, 409, problem(new DaemonException(409, $"Request {id} is still going. Give each request its own id.")));
            return;
        }
        var work = Task.Run(() => AnswerAsync(id, request, aborted));
        _working.TryAdd(work, true);
        _ = work.ContinueWith(done => _working.TryRemove(done, out _), TaskScheduler.Default);
    }

    // A change or resend for a running subscription. One that isn't a plan is refused with a problem event.
    private static void Apply(Subscriptions.Subscription subscription, JsonElement request)
    {
        try
        {
            if (request.TryGetProperty("change", out var change))
                subscription.Change(change);
            if (request.TryGetProperty("resend", out var resend) && resend.ValueKind == JsonValueKind.True)
                subscription.Resend();
        }
        catch (DaemonException exception)
        {
            subscription.Refused(exception.Message);
        }
    }

    private async Task AnswerAsync(string id, JsonElement frame, CancellationTokenSource aborted)
    {
        using var owned = aborted;
        try
        {
            var reply = await route(Read(frame, aborted.Token)).ConfigureAwait(false);
            if (reply is Answer answer)
            {
                Reply(id, answer.Status, answer.Body);
                return;
            }
            if (reply is SubscribeReply subscribe)
            {
                await SubscribeAsync(id, subscribe.Subscription, aborted).ConfigureAwait(false);
                return;
            }
            var events = (Events)reply;
            try
            {
                // Each event waits until it's on the socket, as a subscription's updates do, so a client that reads
                // slowly holds the stream back and a tap drops its oldest messages, rather than frames piling up here (S4).
                await foreach (var e in events.Stream(aborted.Token).ConfigureAwait(false))
                    await QueueAsync(Frame(id, writer => Event(writer, e.Name, e.Data)), WebSocketMessageType.Text).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the client, or the connection is closing or failed.
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
            lock (_subscribing)
                _early.Remove(id); // for a request that wasn't a subscription after all
        }
    }

    // Runs a subscription until the client cancels it or goes.
    private async Task SubscribeAsync(string id, Subscriptions.Subscription subscription, CancellationTokenSource aborted)
    {
        List<JsonElement>? early;
        lock (_subscribing)
        {
            _subscriptions[id] = subscription;
            _early.Remove(id, out early);
        }
        foreach (var request in early ?? [])
            Apply(subscription, request);
        try
        {
            await subscription.RunAsync(
                (name, data) => subscription.Cbor
                    ? QueueAsync(Subscriptions.Envelope.Cbor(id, name, data), WebSocketMessageType.Binary)
                    : QueueAsync(Subscriptions.Envelope.Json(id, name, data), WebSocketMessageType.Text),
                aborted.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the client, or the connection is closing or failed.
        }
        finally
        {
            lock (_subscribing)
                _subscriptions.Remove(id);
        }
        if (aborted.IsCancellationRequested)
        {
            var end = (Action<Subscriptions.UpdateWriter>)(writer =>
            {
                writer.StartMap(1);
                writer.Property("reason", "cancelled");
                writer.EndMap();
            });
            _outgoing.Writer.TryWrite(subscription.Cbor
                ? new Outgoing(Subscriptions.Envelope.Cbor(id, "end", end), WebSocketMessageType.Binary, null)
                : new Outgoing(Subscriptions.Envelope.Json(id, "end", end), WebSocketMessageType.Text, null));
        }
    }

    // Queues a frame, completing with its size once it's on the socket, so a stream or subscription doesn't run ahead
    // of it.
    private async Task<int> QueueAsync(byte[] frame, WebSocketMessageType type)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outgoing.Writer.TryWrite(new Outgoing(frame, type, sent)))
            throw new OperationCanceledException("The connection is closing.");
        await sent.Task.ConfigureAwait(false);
        return frame.Length;
    }

    // A frame as a request, with the client's capabilities.
    private ApiRequest Read(JsonElement frame, CancellationToken aborted)
    {
        var method = frame.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.ToUpperInvariant() : "GET";
        if (!frame.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path)
            throw new DaemonException(400, "A request needs a path, as the HTTP API has it, such as \"/pipelines\".");
        var uri = new Uri(new Uri("http://meep/"), path);
        return new ApiRequest(
            method,
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(),
            ApiRequest.QueryOf(uri.Query),
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

    private void Send(string id, string name, Action<Utf8JsonWriter> data) => Write(id, writer => Event(writer, name, data));

    private static void Event(Utf8JsonWriter writer, string name, Action<Utf8JsonWriter> data)
    {
        writer.WriteString("event", name);
        writer.WritePropertyName("data");
        data(writer);
    }

    // Queues a frame without waiting for the socket: for answers and ends, one for each request.
    private void Write(string? id, Action<Utf8JsonWriter> rest) =>
        _outgoing.Writer.TryWrite(new Outgoing(Frame(id, rest), WebSocketMessageType.Text, null));

    private static byte[] Frame(string? id, Action<Utf8JsonWriter> rest)
    {
        var buffer = ScratchBuffer.Rent();
        try
        {
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
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            ScratchBuffer.Return(buffer);
        }
    }

    // One sender, so frames never interleave. Once the socket fails, what's queued is let go, so nothing waits on it.
    private async Task SendAllAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (failed || socket.State != WebSocketState.Open)
            {
                frame.Sent?.TrySetCanceled();
                continue;
            }
            try
            {
                await socket.SendAsync(frame.Data, frame.Type, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                frame.Sent?.TrySetResult();
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // The connection is going; what's left can't be sent.
                failed = true;
                frame.Sent?.TrySetCanceled();
            }
        }
    }

    /// <summary>A frame to send, and for a subscription's, what to tell once it's sent.</summary>
    private readonly record struct Outgoing(byte[] Data, WebSocketMessageType Type, TaskCompletionSource? Sent);

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
