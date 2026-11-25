using Meep2.Runtime.Internal;
using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime;

/// <summary>
/// Verify's credential inflection: checks an SSH certificate against the trust store (its authority, signature, dates,
/// revocation and the grants that apply to it) and that whoever presented it holds its key, and emits a
/// <see cref="VerifiedMessage"/> saying whether it passed and the permissions it established. In Meeplang:
/// <c>Verify from=msg.Headers["Meep-Credential"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A failed check is a result, not an error: the message emitted says every reason it didn't pass, so a pipeline can
/// answer a 401. It takes whatever isn't a certificate or a permission, and says so if it can't read a credential from it.
/// </para>
/// <para>
/// Proof of possession (Q253): a request signed with the credential's key (<c>Signature-Input</c> and <c>Signature</c>,
/// RFC 9421), or with <c>bearer=true</c>, a credential whose issuer allows it to be used without proof
/// (<c>permit-bearer@meep</c>). Otherwise it fails with <c>NoProofOfPossession</c>.
/// </para>
/// </remarks>
[Verb("Verify", Inflection = "Credential")]
[Emits(typeof(VerifiedMessage))]
[ChosenForEachMessage]
public sealed class VerifyCredential(VerifyParameters parameters) : AInflection<Message>, IChosenByParameters<VerifyParameters>
{
    /// <summary>Takes anything that isn't a certificate or a permission.</summary>
    public static string? WhyNot(VerifyParameters parameters, Message message) =>
        VerifySubject.KindOf(parameters, message) == VerificationKind.Credential ? null : "takes credentials";

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(Verify(message, context));
        return ValueTask.CompletedTask;
    }

    private VerifiedMessage Verify(Message message, ModuleContext context)
    {
        var read = VerifySubject.Read<Credential>(parameters, message);
        if (read.Subject is not { } credential)
            return Verification.Unchecked(message, VerificationKind.Credential, VerificationFailureCode.Malformed, read.Problem!);
        if (Trust.Of(context) is not { } store)
            return Verification.Unchecked(credential, VerificationKind.Credential, VerificationFailureCode.UnknownAuthority, Trust.NoStore);

        var verification = new Verification(credential, VerificationKind.Credential, store, DateTime.UtcNow);
        CredentialCheck.Check(verification, credential, message);
        if (Ancestry.Of(message).OfType<IHttpRequest>().FirstOrDefault() is { } request && RequestSignature.IsSigned(request))
        {
            RequestSignature.Check(verification, credential, request, parameters);
        }
        else if (parameters.Bearer && credential.PermitsBearer)
        {
            verification.Proof = Proof.Bearer;
        }
        else
        {
            verification.Fail(VerificationFailureCode.NoProofOfPossession, parameters.Bearer
                ? $"Nothing proves whoever presented {credential.KeyId}'s credential holds its key, and its issuer hasn't allowed it to be used without proof ({Credential.PermitBearerExtension})."
                : $"Nothing proves whoever presented {credential.KeyId}'s credential holds its key: there's no signature over the request, and this Verify doesn't take bearer credentials (bearer=true).");
        }
        return verification.ToMessage();
    }
}

/// <summary>
/// Verify's certificate inflection: builds a chain from an X.509 certificate to a root in the trust store, checks that
/// every certificate on it is in date, allowed to do what it does and not revoked by the store's CRLs, and emits a
/// <see cref="VerifiedMessage"/> saying whether it passed. In Meeplang: <c>Verify from=msg.Headers["X-Client-Cert"] bearer=true</c>.
/// </summary>
/// <remarks>
/// A certificate is proven by the TLS handshake that presented it (phase 7's mutual TLS), or, with <c>bearer=true</c>,
/// taken without proof. Otherwise it fails with <c>NoProofOfPossession</c>, after the chain has been checked.
/// </remarks>
[Verb("Verify", Inflection = "Certificate")]
[Emits(typeof(VerifiedMessage))]
[ChosenForEachMessage]
public sealed class VerifyCertificate(VerifyParameters parameters) : AInflection<Message>, IChosenByParameters<VerifyParameters>
{
    /// <summary>Takes certificates, and PEM or base64 DER that <c>from=</c> or the message's text gives.</summary>
    public static string? WhyNot(VerifyParameters parameters, Message message) =>
        VerifySubject.KindOf(parameters, message) == VerificationKind.Certificate ? null : "takes certificates";

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(Verify(message, context));
        return ValueTask.CompletedTask;
    }

    private VerifiedMessage Verify(Message message, ModuleContext context)
    {
        var read = VerifySubject.Read<Certificate>(parameters, message);
        if (read.Subject is not { } certificate)
            return Verification.Unchecked(message, VerificationKind.Certificate, VerificationFailureCode.Malformed, read.Problem!);
        if (Trust.Of(context) is not { } store)
            return Verification.Unchecked(certificate, VerificationKind.Certificate, VerificationFailureCode.UntrustedRoot, Trust.NoStore);

        var verification = new Verification(certificate, VerificationKind.Certificate, store, DateTime.UtcNow);
        CertificateCheck.Check(verification, certificate, parameters.Usage);
        if (certificate.ProvenByTls)
            verification.Proof = Proof.Tls;
        else if (parameters.Bearer)
            verification.Proof = Proof.Bearer;
        else
            verification.Fail(VerificationFailureCode.NoProofOfPossession, $"Nothing proves whoever presented {certificate.Subject} holds its key: it didn't come from a TLS handshake, and this Verify doesn't take certificates without one (bearer=true).");
        return verification.ToMessage();
    }
}

/// <summary>
/// Verify's permission inflection: checks that a permission is well defined by the trust store's permissions, and, when
/// the message has verified credentials among its ancestors or <c>against=credentials</c> is written, that they meet
/// it, and emits a <see cref="VerifiedMessage"/> saying so (Q257). In Meeplang: <c>Verify from="ReadNotes"</c>.
/// </summary>
/// <remarks>
/// Well defined: it's defined, or narrows a definition, claims no looser requirements, and everything it refers to
/// exists. Met: every line of the message's family passes through a verified credential that holds it, conferred by
/// an authority it allows, with everything it requires. A permission met is the one permission the result establishes.
/// </remarks>
[Verb("Verify", Inflection = "Permission")]
[Emits(typeof(VerifiedMessage))]
[ChosenForEachMessage]
public sealed class VerifyPermission(VerifyParameters parameters) : AInflection<Message>, IChosenByParameters<VerifyParameters>
{
    /// <summary>Takes permissions, and a permission's name or <c>#permission</c> line that <c>from=</c> or the message's text gives.</summary>
    public static string? WhyNot(VerifyParameters parameters, Message message) =>
        VerifySubject.KindOf(parameters, message) == VerificationKind.Permission ? null : "takes permissions";

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(Verify(message, context));
        return ValueTask.CompletedTask;
    }

    private VerifiedMessage Verify(Message message, ModuleContext context)
    {
        var read = VerifySubject.Read<Permission>(parameters, message);
        if (read.Subject is not { } permission)
            return Verification.Unchecked(message, VerificationKind.Permission, VerificationFailureCode.Malformed, read.Problem!);
        if (Trust.Of(context) is not { } store)
            return Verification.Unchecked(permission, VerificationKind.Permission, VerificationFailureCode.UnknownPermission, Trust.NoStore);
        // A name or #permission line written as text narrows the definition, as a pipeline's directive does: it keeps
        // the definition's authorities unless it names its own, and adds its requirements to the definition's. A
        // Permission message is checked as it stands, since it claims its own requirements.
        // The pipeline's own #permission directive narrows the definition first, as it does for the pipeline's gates.
        if (!read.IsMessage && store.Permissions.Find(permission.Name.Name) is { } definition)
            permission = Gatekeeper.Narrowed(definition, Directives.Of(context)).NarrowedBy(permission);

        var now = DateTime.UtcNow;
        var verification = new Verification(permission, VerificationKind.Permission, store, now);
        foreach (var failure in store.Permissions.Check(permission))
            verification.Fail(failure.Code, failure.Reason);
        var met = parameters.Against == PermissionAgainst.Credentials
            || (parameters.Against == PermissionAgainst.Auto && PermissionLines.HasCredentials(message));
        if (met && store.Permissions.Find(permission.Name.Name) is not null
            && new PermissionLines(store, now).WhyNotMet(message, permission) is { } missing)
            verification.Fail(VerificationFailureCode.MissingRequirement, missing);
        if (met)
            verification.Establish(permission.Name);
        return verification.ToMessage();
    }
}

/// <summary>Where Verify finds its pipeline's own <c>#permission</c> directives.</summary>
internal static class Directives
{
    /// <summary>The directives, or none.</summary>
    public static IReadOnlyList<Permission> Of(ModuleContext context) => (context as IPipelineContext)?.Options.Permissions ?? [];
}

/// <summary>Where Verify finds its pipeline's trust store.</summary>
internal static class Trust
{
    /// <summary>The reason given when there's none.</summary>
    public const string NoStore = "There's no trust store to check against: this pipeline wasn't given one, or it can't be read.";

    /// <summary>The trust store as it is now, or null if there's none.</summary>
    public static TrustStore? Of(ModuleContext context) => (context as IPipelineContext)?.Options.Trust?.Current;
}
