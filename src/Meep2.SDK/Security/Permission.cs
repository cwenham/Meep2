using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// A permission and what it requires: which authorities may confer it, and which other permissions a credential
/// must also have. The trust store's definitions are these, and a pipeline's <c>#permission</c> directive makes one
/// that narrows a definition (Q257).
/// </summary>
/// <remarks>
/// Anyone can make one; it means nothing until <c>Verify.Permission</c> checks it is well defined (and, when asked,
/// met), and emits a <see cref="VerifiedMessage"/> saying so.
/// </remarks>
public sealed record Permission : Message
{
    /// <summary>Makes a permission with no requirements.</summary>
    public Permission(PermissionName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The permission's name and scope.</summary>
    public PermissionName Name { get; init; }

    /// <summary>
    /// The authorities that may confer it, by their names in the trust store. Empty means any authority the store
    /// trusts.
    /// </summary>
    public IReadOnlyList<string> Authorities { get; init; } = [];

    /// <summary>Other permissions a credential must also have for this one to be met.</summary>
    public IReadOnlyList<PermissionName> Requires { get; init; } = [];

    /// <inheritdoc/>
    public override string ToText() => Name.ToString();
}
