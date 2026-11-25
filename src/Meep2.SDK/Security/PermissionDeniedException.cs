using Meep2.SDK.Modules;

namespace Meep2.SDK.Security;

/// <summary>
/// Thrown by <see cref="ModuleContext.Demand"/> when a message doesn't hold a permission. The runtime catches it and
/// emits a <see cref="PermissionDenied"/> error in place of the message, without counting it as the module failing,
/// so a module needn't catch it.
/// </summary>
public sealed class PermissionDeniedException : Exception
{
    /// <summary>Makes the exception.</summary>
    /// <param name="needed">The permission the message didn't hold.</param>
    /// <param name="reason">Why, in words.</param>
    public PermissionDeniedException(PermissionName needed, string reason)
        : base(reason)
    {
        ArgumentNullException.ThrowIfNull(needed);
        Needed = needed;
        Reason = reason;
    }

    /// <summary>The permission the message didn't hold.</summary>
    public PermissionName Needed { get; }

    /// <summary>Why, in words.</summary>
    public string Reason { get; }
}
