using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// What a module's context forwards to its place in the pipeline, the same for sources and for modules that handle
/// messages.
/// </summary>
/// <typeparam name="TNode">The kind of place the module has, so each context reaches its own node's members.</typeparam>
internal abstract class NodeContext<TNode>(TNode node) : ModuleContext
    where TNode : Node
{
    /// <summary>The module's place in the pipeline.</summary>
    protected TNode Node => node;

    public override ModuleInfo Module => node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    public override string PipelineName => node.Pipeline.Name;

    public override System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared => node.Pipeline.Shared;

    public override void CountBytes(long received, long sent) => node.CountBytes(received, sent);

    public override void Listening(string? address) => node.ListeningAt = address;

    public override string? HostId => node.Pipeline.Options.HostId;

    public override string? Origin => node.Origin;

    public override Tally Arrival(string from) => node.ArrivalFrom(from);

    public override Tally Destination(string endpoint) => node.DestinationAt(endpoint);
}
