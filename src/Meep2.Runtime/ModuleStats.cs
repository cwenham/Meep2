namespace Meep2.Runtime;

/// <summary>Counts for one module since its pipeline started.</summary>
/// <param name="Received">Messages put in the module's buffer.</param>
/// <param name="Handled">Messages the module handled without throwing.</param>
/// <param name="Emitted">Messages the module emitted, not counting ones it passed on unchanged.</param>
/// <param name="Dropped">
/// Messages dropped from the module's full buffer, lost because the module couldn't restart, or that reached it
/// after the error policy stopped its branch.
/// </param>
/// <param name="Filtered">Messages that stopped here because the module's options exclude them, such as other messages reaching an errors-only module.</param>
/// <param name="Failed">Times the module threw.</param>
/// <param name="Restarts">Times the runtime replaced the module with a fresh one after it threw.</param>
/// <param name="Expired">Messages that expired before the module could take them, or while it was handling them.</param>
/// <param name="Refused">Messages that none of the module's inflections takes. Each one became an error (Q54).</param>
public readonly record struct ModuleStats(
    long Received,
    long Handled,
    long Emitted,
    long Dropped,
    long Filtered,
    long Failed,
    long Restarts,
    long Expired,
    long Refused);
