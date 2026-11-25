using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>Counts for one module since its pipeline started.</summary>
/// <param name="Received">Messages put in the module's buffer.</param>
/// <param name="Handled">Messages the module handled without throwing.</param>
/// <param name="Emitted">Messages the module emitted, new ones and errors, not counting ones it passed on unchanged.</param>
/// <param name="Passed">
/// Messages the module passed on unchanged, as a filter does with those it keeps, or a passthrough or Distribute with
/// every one. So a filter's received is its passed, plus its emitted errors, plus what it held back.
/// </param>
/// <param name="Dropped">
/// Messages dropped from the module's full buffer, lost because the module couldn't restart, or that reached it
/// after the error policy stopped its branch.
/// </param>
/// <param name="Filtered">Messages that stopped here because the module's options exclude them, such as other messages reaching an errors-only module.</param>
/// <param name="Failed">Times the module threw.</param>
/// <param name="Restarts">Times the runtime replaced the module with a fresh one after it threw.</param>
/// <param name="Expired">Messages that expired before the module could take them, or while it was handling them.</param>
/// <param name="Refused">Messages that none of the module's inflections takes. Each one became an error (Q54).</param>
/// <param name="Queued">Messages waiting in the module's buffer now.</param>
/// <param name="Denied">
/// Messages refused because they lacked a permission the module needs: at its <c>@permission</c> gate, or by its own
/// check (<see cref="SDK.Modules.ModuleContext.Demand"/>). Each became a <see cref="SDK.Security.PermissionDenied"/>
/// error, or, for an error refused, passed the module by. Also what the module refused before it became a message,
/// such as requests a Listen with <c>proof=required</c> answered with 401 (<see cref="SDK.Modules.ModuleContext.CountDenied"/>).
/// </param>
public readonly record struct ModuleStats(
    long Received,
    long Handled,
    long Emitted,
    long Passed,
    long Dropped,
    long Filtered,
    long Failed,
    long Restarts,
    long Expired,
    long Refused,
    long Queued,
    long Denied = 0);

/// <summary>The count for one link since its pipeline started (<see cref="Pipeline.GetLinkStats"/>).</summary>
/// <param name="From">The module it comes from.</param>
/// <param name="To">The module it goes to.</param>
/// <param name="Input">Which of <paramref name="To"/>'s inputs it is, counting from 1.</param>
/// <param name="Carried">
/// Messages that went along it, errors included: each one the module at its end took in or stopped (dropped,
/// expired or refused it), or let pass by to the modules under it, as an error passes a module with
/// <c>@no-errors</c>. Not the other messages that stop at a module with <c>@errors</c>. A change to the running
/// pipeline that keeps both modules keeps the link's counts.
/// </param>
/// <param name="Errors">How many of <paramref name="Carried"/> were errors.</param>
public readonly record struct LinkStats(ModuleInfo From, ModuleInfo To, int Input, long Carried, long Errors);
