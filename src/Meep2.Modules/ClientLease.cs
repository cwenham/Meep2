using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>A lease on a connection Send opened, carried by what the server sends.</summary>
internal sealed class ClientLease(Subscription subscription) : Lease, IConnectionLease
{
    public async Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken)
    {
        if (IsClosed)
            throw new IOException($"{this} has closed");
        // Counted for the Send whose connection it is, whichever module replies.
        var sent = await subscription.Client.ReplyAsync(message, text, end, final, cancellationToken).ConfigureAwait(false);
        subscription.Outlet.CountBytes(0, sent);
        subscription.Outlet.Destination(subscription.Client.Endpoint).Count(sent: 1, bytesSent: sent);
    }

    public override string ToString() => $"the connection to {subscription.Client.Address}";

    // The connection is shared, so closing one Send's lease leaves it open.
    protected override ValueTask OnClosedAsync() => ValueTask.CompletedTask;
}
