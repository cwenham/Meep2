using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Meep2.Daemon;

/// <summary>
/// A WebSocket to the daemon, carrying its API both ways (Q108). Send requests with <see cref="SendAsync"/> and read
/// what comes back with <see cref="ReceiveAsync"/>: answers, and events from streams, each with the id of the request
/// it belongs to.
/// </summary>
/// <remarks>
/// A request is <c>{"id": 1, "method": "GET", "path": "/pipelines"}</c>, with a body in <c>json</c> or <c>text</c>.
/// An answer is <c>{"id": 1, "status": 200, "body": …}</c>, and a stream's events are
/// <c>{"id": 1, "event": "stats", "data": …}</c> until one called <c>end</c>. <c>{"id": 1, "cancel": true}</c>
/// stops a stream.
/// </remarks>
public sealed class DaemonSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);

    internal DaemonSocket(ClientWebSocket socket) => _socket = socket;

    /// <summary>Sends a request. Its answer, or its events, come back with the same id.</summary>
    /// <param name="id">The request's id, which the replies carry.</param>
    /// <param name="method">GET, POST or DELETE.</param>
    /// <param name="path">The path, as the HTTP API has it, such as <c>/pipelines/ticker/stats?every=1s</c>.</param>
    /// <param name="json">A JSON body, if any.</param>
    /// <param name="text">A text body, if any.</param>
    public Task SendAsync(string id, string method, string path, JsonElement? json = null, string? text = null, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteString("method", method);
            writer.WriteString("path", path);
            if (json is { } body)
            {
                writer.WritePropertyName("json");
                body.WriteTo(writer);
            }
            if (text is not null)
                writer.WriteString("text", text);
        }, cancellationToken);

    /// <summary>Stops a stream the daemon is sending.</summary>
    public Task CancelAsync(string id, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteBoolean("cancel", true);
        }, cancellationToken);

    /// <summary>Changes a subscription the daemon is sending, by a merge patch to what it asked for.</summary>
    public Task ChangeAsync(string id, JsonElement patch, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WritePropertyName("change");
            patch.WriteTo(writer);
        }, cancellationToken);

    /// <summary>Asks a subscription to send everything whole in its next update.</summary>
    public Task ResendAsync(string id, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteBoolean("resend", true);
        }, cancellationToken);

    /// <summary>
    /// The size in bytes of the last frame <see cref="ReceiveAsync"/> returned, as it came, and whether it was binary:
    /// CBOR, from a subscription that asked for it.
    /// </summary>
    public (int Bytes, bool Binary) Last { get; private set; }

    /// <summary>
    /// The next frame from the daemon, or null once the connection has closed. A binary frame, from a subscription that
    /// asked for CBOR, comes back as the same JSON.
    /// </summary>
    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            try
            {
                result = await _socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            buffer.Advance(result.Count);
        }
        while (!result.EndOfMessage);
        Last = (buffer.WrittenCount, result.MessageType == WebSocketMessageType.Binary);
        if (result.MessageType == WebSocketMessageType.Binary)
            return Subscriptions.Envelope.ToJson(buffer.WrittenMemory);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Sends a frame as it is, for a client that builds its own.</summary>
    public async Task SendRawAsync(string frame, CancellationToken cancellationToken = default)
    {
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye.", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
                // It went first.
            }
        }
        _socket.Dispose();
        _sending.Dispose();
    }

    private async Task SendFrameAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }
}
