using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Emits for a module that handles messages at any time, from any thread (<see cref="ModuleContext.Outlet"/>). It
/// holds the pipeline open while it emits, and does nothing once the pipeline has halted.
/// </summary>
internal sealed class NodeOutlet(InflectionNode node, ModuleInfo emitter) : Outlet
{
    public override void CountBytes(long received, long sent) => node.Counters.CountBytes(received, sent);

    public override Tally Destination(string endpoint) => node.PeerCounts.DestinationAt(endpoint);

    public override bool Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!node.Pipeline.TryHold())
            return false;
        try
        {
            node.Pipeline.Emit(node, emitter, message, defaultParent: null);
        }
        finally
        {
            node.Pipeline.ReleaseHold();
        }
        return true;
    }

    public override bool Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        if (!node.Pipeline.TryHold())
            return false;
        try
        {
            node.Pipeline.EmitLeased(node, emitter, message, lease, defaultParent: null);
        }
        finally
        {
            node.Pipeline.ReleaseHold();
        }
        return true;
    }
}
