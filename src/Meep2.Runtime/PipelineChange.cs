using Meep2.Runtime.Internal;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>
/// What a running pipeline should become (goal 4): a new set of modules and connections, and which of the new
/// modules are the same modules as ones running now. Apply it with <see cref="Pipeline.ApplyAsync"/>.
/// </summary>
/// <remarks>
/// Modules of <see cref="Next"/> that aren't kept are added; running modules that no new one keeps are removed. The
/// Meeplang loader works out which modules to keep from a pipeline file's old and new text, by name, then structure,
/// then position (Q61).
/// </remarks>
public sealed class PipelineChange
{
    private readonly Dictionary<ModuleInfo, (ModuleInfo Running, bool Reconfigure)> _kept = new(ReferenceEqualityComparer.Instance);

    /// <summary>Starts a change to the pipeline <paramref name="next"/> describes.</summary>
    public PipelineChange(PipelineBuilder next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Next = next.Definition();
    }

    internal PipelineDefinition Next { get; }

    /// <summary>
    /// Says that <paramref name="next"/>, a module of the new pipeline, is <paramref name="running"/>, a module of
    /// the running one, so it carries on with its buffer, counts, taps and state.
    /// </summary>
    /// <param name="parametersChanged">
    /// Whether its parameters changed, so its instances are offered the new ones, and replaced if they don't take
    /// them. Otherwise its instances carry on as they are.
    /// </param>
    public PipelineChange Keep(ModuleInfo running, ModuleInfo next, bool parametersChanged = false)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(next);
        if (!Next.Specs.Any(spec => ReferenceEquals(spec.Info, next)))
            throw new ArgumentException($"{next} isn't one of the new pipeline's modules.", nameof(next));
        _kept[next] = (running, parametersChanged);
        return this;
    }

    internal (ModuleInfo Running, bool Reconfigure)? KeptAs(ModuleInfo next) => _kept.TryGetValue(next, out var kept) ? kept : null;
}

/// <summary>What a change did to a running pipeline.</summary>
/// <param name="Added">New modules.</param>
/// <param name="Removed">Modules taken out, as they were known before the change.</param>
/// <param name="Reconfigured">Modules that took new parameters and kept their state.</param>
/// <param name="Replaced">Modules made again, with new instances: their parameters changed and they couldn't take them, or their inflections or buffer size changed.</param>
/// <param name="Unchanged">Modules that carried on as they were, though their place or number may have changed.</param>
public sealed record PipelineChangeReport(
    IReadOnlyList<ModuleInfo> Added,
    IReadOnlyList<ModuleInfo> Removed,
    IReadOnlyList<ModuleInfo> Reconfigured,
    IReadOnlyList<ModuleInfo> Replaced,
    IReadOnlyList<ModuleInfo> Unchanged)
{
    /// <summary>Whether the change did anything beyond renumbering.</summary>
    public bool ChangedAnything => Added.Count + Removed.Count + Reconfigured.Count + Replaced.Count > 0;
}

/// <summary>A pipeline's modules, connections and settings, as a builder has them.</summary>
internal sealed record PipelineDefinition(string Name, PipelineOptions Options, NodeSpec[] Specs, (ModuleInfo From, ModuleInfo To)[] Connections);
