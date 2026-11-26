using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// What a module's context forwards to its place in the pipeline, the same for sources and for modules that handle
/// messages.
/// </summary>
/// <typeparam name="TNode">The kind of place the module has, so each context reaches its own node's members.</typeparam>
internal abstract class NodeContext<TNode>(TNode node) : ModuleContext, IPipelineContext
    where TNode : Node
{
    public PipelineOptions Options => node.Pipeline.Options;

    /// <summary>The module's place in the pipeline.</summary>
    protected TNode Node => node;

    public override ModuleInfo Module => node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    public override string PipelineName => node.Pipeline.Name;

    public override System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared => node.Pipeline.Shared;

    public override void CountBytes(long received, long sent) => node.Counters.CountBytes(received, sent);

    public override void Listening(string? address) => node.PeerCounts.ListeningAt = address;

    public override string? HostId => node.Pipeline.Options.HostId;

    public override string? Origin => node.Origin;

    public override Tally Arrival(string from) => node.PeerCounts.ArrivalFrom(from);

    public override Tally Destination(string endpoint) => node.PeerCounts.DestinationAt(endpoint);

    public override void CountDenied() => node.Counters.Denied();

    internal override string? WhyNotHeld(string permission, Message? message, PermissionTarget? target) =>
        node.Pipeline.Gates.WhyNot(message ?? node.Pipeline.Credential, Named(permission), target, Proofs);

    // Permissions as verbs name them, read once each: a verb checks the same few for every message.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PermissionName> s_named = new(StringComparer.Ordinal);

    private static PermissionName Named(string permission)
    {
        if (s_named.TryGetValue(permission, out var named))
            return named;
        named = PermissionName.Parse(permission);
        if (s_named.Count < 1024)
            s_named.TryAdd(permission, named);
        return named;
    }

    internal override IReadOnlyList<Credential> HeldBy(string permission, Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var gates = node.Pipeline.Gates;
        var store = gates.Store;
        var basis = gates.Resolve(PermissionName.Parse(permission), store, Proofs);
        if (store is null || basis.Problem is not null)
            return [];
        return new PermissionLines(store).Holders(message, new Requirement(basis.Permission) { Name = basis.Name, Proofs = basis.Proofs });
    }

    // The kinds of proof the module's checks need: its inflection's, or the line's @proof.
    private ProofSet Proofs => node is InflectionNode inflection ? inflection.Proofs : ProofSet.Of(node.Options.Proofs);
}

/// <summary>A module's context that reaches its pipeline's options, for the runtime's own verbs, such as <c>Verify</c>.</summary>
internal interface IPipelineContext
{
    /// <summary>The pipeline's options.</summary>
    PipelineOptions Options { get; }
}
