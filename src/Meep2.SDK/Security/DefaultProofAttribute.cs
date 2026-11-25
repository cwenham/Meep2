namespace Meep2.SDK.Security;

/// <summary>
/// The kinds of proof an inflection's gate needs by default: how the credentials a message was verified with must have
/// been proven, not only presented. A pipeline can say otherwise with <c>@proof</c> on the module, which replaces this,
/// and <c>@proof=any</c> accepts a bearer credential too. <c>Authorise</c> and <c>Revoke</c> need a signed request, a
/// connection's proof or TLS, so a credential read from a file can't change the trust store unless a pipeline says so.
/// </summary>
/// <remarks>
/// It applies to every permission the inflection needs, and to its own checks in code
/// (<see cref="Modules.ModuleContext.Demand"/>). A message that more than one inflection takes by its type needs any of
/// the kinds each of them names.
/// </remarks>
/// <param name="proofs">The kinds of proof accepted, any of them.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DefaultProofAttribute(params Proof[] proofs) : Attribute
{
    /// <summary>The kinds of proof a signed or proven credential has: a signed request, a connection's proof or TLS.</summary>
    public static IReadOnlyList<Proof> Signed { get; } = [Proof.RequestSignature, Proof.Connection, Proof.Tls];

    /// <summary>The kinds of proof accepted.</summary>
    public IReadOnlyList<Proof> Proofs { get; } = proofs;

    /// <summary>The kinds <paramref name="moduleType"/> declares, or none if it doesn't, meaning any.</summary>
    public static IReadOnlyList<Proof> Of(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        return moduleType.GetCustomAttributes(typeof(DefaultProofAttribute), inherit: false) is [DefaultProofAttribute attribute, ..]
            ? attribute.Proofs
            : [];
    }
}
