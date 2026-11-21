using Meep2.Meeplang.Editing;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>
/// What a hosted pipeline counts, for subscriptions: read from the pipeline while it runs, or as it was when it ended
/// (M1). Once it has ended, the daemon keeps these instead of the pipeline, so its modules, and its plugins, can go.
/// </summary>
internal abstract class PipelineCounts
{
    /// <summary>The key of each module, as the graph has it.</summary>
    public abstract IReadOnlyDictionary<ModuleInfo, string> Keys();

    /// <summary>Every module's counts, busy time and bytes.</summary>
    public abstract IReadOnlyList<ModuleReading> ReadModules();

    /// <summary>How long a module took to handle messages.</summary>
    public abstract HandlingTimes GetHandlingTimes(ModuleInfo module);

    /// <summary>Each module's counts by place: where its messages arrive from, or where they go.</summary>
    public abstract IReadOnlyList<(ModuleInfo Module, IReadOnlyList<PeerCount> Places)> ReadPeers(bool arrivals);

    /// <summary>Each link's counts.</summary>
    public abstract IReadOnlyList<LinkDetail> Links();

    /// <summary>A running pipeline's counts, read when asked for.</summary>
    public static PipelineCounts Of(LivePipeline live, Pipeline pipeline) => new Running(live, pipeline);

    /// <summary>The counts as they are now, kept, without holding the pipeline.</summary>
    public PipelineCounts Freeze()
    {
        var modules = ReadModules();
        return new Frozen(
            Keys(),
            modules,
            modules.ToDictionary(m => m.Module, m => GetHandlingTimes(m.Module)),
            ReadPeers(arrivals: true),
            ReadPeers(arrivals: false),
            Links());
    }

    private sealed class Running(LivePipeline live, Pipeline pipeline) : PipelineCounts
    {
        public override IReadOnlyDictionary<ModuleInfo, string> Keys() => live.ModuleKeys();

        public override IReadOnlyList<ModuleReading> ReadModules() => pipeline.ReadModules();

        public override HandlingTimes GetHandlingTimes(ModuleInfo module) => pipeline.GetHandlingTimes(module);

        public override IReadOnlyList<(ModuleInfo Module, IReadOnlyList<PeerCount> Places)> ReadPeers(bool arrivals) => pipeline.ReadPeers(arrivals);

        public override IReadOnlyList<LinkDetail> Links() => Graphs.LinkCounts(live, pipeline);
    }

    private sealed class Frozen(
        IReadOnlyDictionary<ModuleInfo, string> keys,
        IReadOnlyList<ModuleReading> modules,
        IReadOnlyDictionary<ModuleInfo, HandlingTimes> times,
        IReadOnlyList<(ModuleInfo Module, IReadOnlyList<PeerCount> Places)> arriving,
        IReadOnlyList<(ModuleInfo Module, IReadOnlyList<PeerCount> Places)> leaving,
        IReadOnlyList<LinkDetail> links) : PipelineCounts
    {
        public override IReadOnlyDictionary<ModuleInfo, string> Keys() => keys;

        public override IReadOnlyList<ModuleReading> ReadModules() => modules;

        public override HandlingTimes GetHandlingTimes(ModuleInfo module) => times.GetValueOrDefault(module) ?? HandlingTimes.Empty;

        public override IReadOnlyList<(ModuleInfo Module, IReadOnlyList<PeerCount> Places)> ReadPeers(bool arrivals) => arrivals ? arriving : leaving;

        public override IReadOnlyList<LinkDetail> Links() => links;
    }
}
