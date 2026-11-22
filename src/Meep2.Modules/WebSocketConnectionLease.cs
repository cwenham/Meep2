using System.Net.WebSockets;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>A client's connection to <see cref="WebSocketListen"/>.</summary>
internal sealed class WebSocketConnectionLease(WebSocketLink link, string url, string? remote) : Lease, IConnectionLease
{
    private volatile CloseReason _closeWith = new(WebSocketCloseStatus.NormalClosure, "Goodbye.");

    public WebSocketLink Link => link;

    public string Url => url;

    public string? Remote => remote;

    /// <summary>The code and reason to close with, when it closes.</summary>
    public void CloseWith(WebSocketCloseStatus status, string reason) => _closeWith = new CloseReason(status, reason);

    /// <summary>Queues a message without waiting. A full queue drops it (Q153).</summary>
    public void Queue(Frame frame)
    {
        if (IsClosed || !link.IsOpen)
            throw new IOException($"{this} has closed");
        if (!link.TryQueue(frame))
        {
            link.Peer?.Count(failed: 1);
            throw new QueueFullException($"{this} is too slow to keep up, so a message to it was dropped");
        }
    }

    public async Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken)
    {
        Queue(await Frames.ForAsync(message, text, cancellationToken).ConfigureAwait(false));
        if (final)
            await CloseAsync().ConfigureAwait(false);
    }

    public override string ToString() => $"the WebSocket connection from {remote ?? "a client"}";

    protected override async ValueTask OnClosedAsync()
    {
        var reason = _closeWith;
        await link.CloseAsync(reason.Status, reason.Description).ConfigureAwait(false);
    }

    private sealed record CloseReason(WebSocketCloseStatus Status, string Description);
}
