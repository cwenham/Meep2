namespace Meep2.Runtime;

/// <summary>Counts for one module since its pipeline started.</summary>
/// <param name="Received">Messages put in the module's buffer.</param>
/// <param name="Handled">Messages the module handled without throwing.</param>
/// <param name="Emitted">Messages the module emitted, not counting ones it passed on unchanged.</param>
/// <param name="Dropped">Messages dropped from the module's full buffer, or lost because the module couldn't restart.</param>
/// <param name="Filtered">Messages that stopped here because the module couldn't handle them.</param>
/// <param name="Failed">Times the module threw.</param>
/// <param name="Restarts">Times the runtime replaced the module with a fresh one after it threw.</param>
public readonly record struct ModuleStats(
    long Received,
    long Handled,
    long Emitted,
    long Dropped,
    long Filtered,
    long Failed,
    long Restarts);
