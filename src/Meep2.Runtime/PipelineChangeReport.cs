using Meep2.SDK.Modules;

namespace Meep2.Runtime;

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
