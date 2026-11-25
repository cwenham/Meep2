using System.Diagnostics.CodeAnalysis;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.SDK.Security;

/// <summary>
/// The error a message becomes when it's refused at a gate: a module with <c>@permission</c>, a verb that requires a
/// permission (<see cref="RequiresPermissionAttribute"/>), or a module's own check
/// (<see cref="ModuleContext.Demand"/>). It follows the module's error policy, as other errors do, so an
/// <c>@errors</c> branch can answer it, such as with a 403 (Q258). Its parent is the message refused.
/// </summary>
/// <remarks>
/// Only the runtime makes one. A copy made with <c>with</c> is allowed, since a denial grants nothing.
/// </remarks>
public sealed record PermissionDenied : ErrorMessage
{
    /// <summary>Made by the runtime when a gate refuses a message.</summary>
    [SetsRequiredMembers]
    internal PermissionDenied(ModuleInfo gate, IReadOnlyList<PermissionName> needed, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(needed);
        ArgumentNullException.ThrowIfNull(reasons);
        Gate = gate;
        Needed = needed;
        Reasons = reasons;
        Error = Describe(gate, needed, reasons);
    }

    /// <summary>The module that refused the message.</summary>
    public ModuleInfo Gate { get; }

    /// <summary>The permissions the module needed that the message didn't have, each matching a reason in <see cref="Reasons"/>.</summary>
    public IReadOnlyList<PermissionName> Needed { get; }

    /// <summary>Why the message didn't have each permission in <see cref="Needed"/>, in the same order, in words.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>The error's text: which module refused the message, and why for each permission it lacked.</summary>
    internal static string Describe(ModuleInfo gate, IReadOnlyList<PermissionName> needed, IReadOnlyList<string> reasons)
    {
        var text = new StringBuilder($"{gate} needs {string.Join(" and ", needed)}, which this message doesn't have.");
        foreach (var reason in reasons)
            text.Append(' ').Append(reason);
        return text.ToString();
    }

    /// <summary>Prints the denial's identity and text, as an error does.</summary>
    protected override bool PrintMembers(StringBuilder builder) => base.PrintMembers(builder);
}
