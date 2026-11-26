using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime;

/// <summary>
/// The permissions Meep's own security verbs require, which no pipeline can remove (Q263), and the one that lets a
/// credential pass on permissions it doesn't hold itself (Q262).
/// </summary>
public static class SecurityPermissions
{
    /// <summary>Needed by <c>Authorise.Permission</c>, to grant a permission to a credential.</summary>
    public const string AuthorisePermission = "AuthorisePermission";

    /// <summary>Needed by <c>Authorise.Certificate</c>, to trust a CA or root.</summary>
    public const string AuthoriseCertificate = "AuthoriseCertificate";

    /// <summary>Needed by <c>Authorise.Authority</c>, to trust an SSH certificate authority to issue credentials (Q303).</summary>
    public const string AuthoriseAuthority = "AuthoriseAuthority";

    /// <summary>Lets <c>Authorise.Permission</c> grant a permission the authorising credential doesn't hold.</summary>
    public const string AuthoriseAny = "AuthoriseAny";

    /// <summary>Needed by <c>Revoke</c>.</summary>
    public const string RevokePermission = "RevokePermission";

    /// <summary>
    /// The permissions that let a credential change what this machine trusts. Only a credential holding
    /// <see cref="AuthoriseAny"/> may grant one of them, so holding <c>AuthorisePermission</c> isn't enough to make
    /// more authorisers.
    /// </summary>
    public static IReadOnlyList<string> OverTrust { get; } = [AuthorisePermission, AuthoriseAny, AuthoriseCertificate, AuthoriseAuthority, RevokePermission];
}

/// <summary>
/// Authorise's permission inflection: grants a permission to a credential, emitting a <see cref="Grant"/> signed by this
/// machine's authority key and recording it in the trust store, so it's in force whenever the credential is verified,
/// after a restart too (Q254, Q256). In Meeplang: <c>Authorise permission="WriteNotes"</c>, under a <c>Parse</c> that
/// gives it the credential. <c>Authorize</c> is the same verb.
/// </summary>
/// <remarks>
/// <para>
/// It needs <c>AuthorisePermission</c>, and <c>@permission</c> adds to that. The credential it grants to is the message;
/// whoever authorises is whatever credential the message's family was verified with, as at any gate.
/// </para>
/// <para>
/// The authorising credential must have been proven, by a signed request, a connection or TLS, unless the line's
/// <c>@proof</c> says otherwise (<c>@proof=any</c> accepts a bearer credential). It passes on only what it holds itself,
/// with a scope no wider, unless it holds <c>AuthoriseAny</c> (Q262), and only a credential holding <c>AuthoriseAny</c>
/// may grant the permissions over the trust store itself (<see cref="SecurityPermissions.OverTrust"/>); otherwise the
/// message is refused with a <see cref="PermissionDenied"/>. The credential and permission must come from a part of a
/// signed request the signature covers, if they came from a request at all (<see cref="Verifying.SignedParts"/>). The credential
/// granted to must be one this machine trusts: issued by a trusted authority, in date and not revoked. The credential
/// isn't reissued, and the grant can be revoked alone.
/// </para>
/// <para>
/// The grant names the credentials that authorised it (<see cref="GrantTerms.AuthorisedBy"/>), and falls when one of
/// them is revoked (Q301). That goes one level only: it doesn't fall when an authoriser's credential expires, or loses
/// <c>AuthorisePermission</c>, and grants made by a grant's holder don't fall with the grant.
/// </para>
/// </remarks>
[Verb("Authorise", Inflection = "Permission", Alias = "Authorize")]
[Emits(typeof(Grant))]
[RequiresPermission(SecurityPermissions.AuthorisePermission)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class AuthorisePermission(AuthorisePermissionParameters parameters) : AInflection<Credential>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Credential message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (Trust.SourceOf(context) is not { Current: { } store } source)
        {
            context.EmitError(Trust.NoStore);
            return ValueTask.CompletedTask;
        }

        string written;
        try
        {
            written = parameters.Permission.Evaluate(message) ?? "";
        }
        catch (EvaluationException exception)
        {
            context.EmitError($"Authorise can't work out which permission to grant: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        if (!PermissionName.TryParse(written.Trim(), out var permission))
        {
            context.EmitError($"Authorise was asked to grant \"{written}\", which isn't a permission's name.");
            return ValueTask.CompletedTask;
        }

        // What it grants, and to whom, must come from parts of a signed request its signature covers.
        var moved = SignedParts.WhyNotCovered(message, Convert.ToBase64String(message.Blob.Span), whole: false, "the credential to grant to")
            ?? (parameters.Permission.IsConstant ? null : SignedParts.WhyNotCovered(message, written, whole: true, "the permission to grant"));
        if (moved is not null)
        {
            context.EmitError($"Authorise didn't grant {permission} to {message.KeyId}. {moved}");
            return ValueTask.CompletedTask;
        }

        // Only what the authorising credential holds can be passed on, unless it holds AuthoriseAny (Q262); and the
        // permissions over the trust store itself only with AuthoriseAny, so authorisers can't make more authorisers.
        var anyWhyNot = context.WhyNotHeld(SecurityPermissions.AuthoriseAny, message, (PermissionTarget?)null);
        if (anyWhyNot is not null && SecurityPermissions.OverTrust.Contains(permission.Name))
        {
            throw new PermissionDeniedException(new PermissionName(SecurityPermissions.AuthoriseAny),
                $"Only a credential holding {SecurityPermissions.AuthoriseAny} may grant {permission.Name}, which changes what this machine trusts: {anyWhyNot}");
        }
        if (anyWhyNot is not null && context.WhyNotHeld(permission.ToString(), message, (PermissionTarget?)null) is { } notHeld)
        {
            throw new PermissionDeniedException(permission,
                $"Authorise passes on only what the authorising credential holds, unless it holds {SecurityPermissions.AuthoriseAny}: {notHeld}");
        }

        var now = DateTime.UtcNow;
        var check = new Verification(message, VerificationKind.Credential, store, now);
        CredentialCheck.Check(check, message, message);
        if (check.Failed)
        {
            context.EmitError($"Authorise grants only to credentials this machine trusts, and {message.KeyId}'s doesn't verify: {string.Join(" ", check.Failures.Select(f => f.Reason))}");
            return ValueTask.CompletedTask;
        }

        // The grant names whoever authorised it, so it falls if one of their credentials is revoked (Q301).
        var terms = new GrantTerms(message, permission)
        {
            ValidBefore = parameters.Valid is { } valid ? now + valid : null,
            AuthorisedBy = [.. context.HeldBy(SecurityPermissions.AuthorisePermission, message).Select(CredentialName.Of).Distinct()],
        };
        Grant grant;
        try
        {
            grant = store.Folder.SignGrant(terms);
        }
        catch (TrustStoreException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }
        if (CredentialCheck.WhyNotGood(grant, store, now) is { } useless)
        {
            context.EmitError($"Authorise didn't record the grant, since it wouldn't count here: {useless} Add this machine's authority to the trust store's authorities for meep-grant, as meep trust init does.");
            return ValueTask.CompletedTask;
        }
        try
        {
            store.Folder.RecordGrant(grant);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.EmitError($"Authorise signed a grant of {permission} to {message.KeyId}, but couldn't record it: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        source.Reload();
        context.Emit(grant);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="AuthorisePermission"/>.</summary>
public sealed record AuthorisePermissionParameters
{
    /// <summary>
    /// The permission to grant, with a scope if it has one, such as <c>"WriteNotes"</c> or
    /// <c>"meep.files.write:~/notes/**"</c>, or read from the message, such as <c>fam.Listen.Json.permission</c>.
    /// </summary>
    public required Computed<string> Permission { get; init; }

    /// <summary>How long the grant lasts. Without it, as long as the credential does.</summary>
    public TimeSpan? Valid
    {
        get;
        init => field = value is null || value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Valid), value, "A grant has to last longer than zero.");
    }
}

/// <summary>
/// Authorise's certificate inflection: trusts an X.509 CA or root, adding it to the trust store's <c>roots/</c>, so
/// certificates that chain to it verify (Q255 A). In Meeplang: <c>Authorise</c>, under a <c>Parse</c> that gives it the
/// certificate. <c>Authorize</c> is the same verb.
/// </summary>
/// <remarks>
/// It needs <c>AuthoriseCertificate</c>, and <c>@permission</c> adds to that. The certificate must be a CA's; any CA
/// will do, an intermediate too, since every certificate in <c>roots/</c> is an anchor (Q275). One trusted already is
/// passed on without writing it again. It emits the certificate once it's trusted.
/// </remarks>
[Verb("Authorise", Inflection = "Certificate", Alias = "Authorize")]
[EmitsInput]
[RequiresPermission(SecurityPermissions.AuthoriseCertificate)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class AuthoriseCertificate(AuthoriseCertificateParameters parameters) : AInflection<Certificate>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Certificate message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (Trust.SourceOf(context) is not { Current: { } store } source)
        {
            context.EmitError(Trust.NoStore);
            return ValueTask.CompletedTask;
        }
        if (SignedParts.WhyNotCovered(message, Convert.ToBase64String(message.Der.Span), whole: false, "the certificate to trust") is { } moved)
        {
            context.EmitError($"Authorise didn't trust {message.Subject}. {moved}");
            return ValueTask.CompletedTask;
        }
        using var certificate = message.ToX509();
        if (TrustedRoot.WhyNot(certificate) is { } notRoot)
        {
            context.EmitError(notRoot);
            return ValueTask.CompletedTask;
        }
        if (store.Roots.Any(root => root.Certificate.Thumbprint == certificate.Thumbprint))
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }

        string name;
        try
        {
            name = parameters.Name?.Evaluate(message) is { Length: > 0 } given ? given : DefaultName(message, certificate);
        }
        catch (EvaluationException exception)
        {
            context.EmitError($"Authorise can't work out what to call the root: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        try
        {
            store.Folder.AddRoot(name, message.Der.Span);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            context.EmitError($"Authorise couldn't trust {message.Subject}: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        source.Reload();
        context.Emit(message);
        return ValueTask.CompletedTask;
    }

    // The certificate's common name, or its whole subject, with the start of its SHA-256 thumbprint (as the revoked
    // file names certificates), so two CAs with the same name don't clash.
    private static string DefaultName(Certificate message, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        var common = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
        return $"{TrustedRoot.NameFrom(string.IsNullOrWhiteSpace(common) ? certificate.Subject : common)}-{message.Thumbprint[..8].ToLowerInvariant()}";
    }
}

/// <summary>Parameters for <see cref="AuthoriseCertificate"/>.</summary>
public sealed record AuthoriseCertificateParameters
{
    /// <summary>
    /// What to call it in <c>roots/</c>: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>. Without it, its common name
    /// and the start of its thumbprint.
    /// </summary>
    public Computed<string>? Name { get; init; }
}

/// <summary>
/// Authorise's authority inflection: trusts an SSH certificate authority's key to issue credentials, adding it to the
/// trust store's <c>authorities</c>, as <c>meep trust add-authority</c> does (Q303). In Meeplang: <c>Authorise</c>, under
/// a <c>Parse kind=authority</c> that gives it the key, as a <c>.pub</c> file has it. <c>Authorize</c> is the same verb.
/// </summary>
/// <remarks>
/// It needs <c>AuthoriseAuthority</c>, which trusting X.509 roots (<c>AuthoriseCertificate</c>) doesn't give, so the two
/// can be given apart; <c>@permission</c> adds to it. The key is named <c>name=</c>, or its comment if that's one word,
/// or <c>authority-</c> and the start of its fingerprint. A key trusted already is passed on without writing it again,
/// and a name in use for another key is an error. With <c>grants=true</c> it's trusted to sign grants too. It emits the
/// key once it's trusted.
/// </remarks>
[Verb("Authorise", Inflection = "Authority", Alias = "Authorize")]
[EmitsInput]
[RequiresPermission(SecurityPermissions.AuthoriseAuthority)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class AuthoriseAuthority(AuthoriseAuthorityParameters parameters) : AInflection<AuthorityKey>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(AuthorityKey message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (Trust.SourceOf(context) is not { Current: { } store } source)
        {
            context.EmitError(Trust.NoStore);
            return ValueTask.CompletedTask;
        }
        if (SignedParts.WhyNotCovered(message, Convert.ToBase64String(message.Key.Span), whole: false, "the authority's key to trust") is { } moved)
        {
            context.EmitError($"Authorise didn't trust the key {message.Fingerprint}. {moved}");
            return ValueTask.CompletedTask;
        }

        string name;
        try
        {
            name = parameters.Name?.Evaluate(message) is { Length: > 0 } given ? given : DefaultName(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError($"Authorise can't work out what to call the authority: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        if (name == Permission.PipelineAuthority)
        {
            context.EmitError("pipeline is kept for pipelines' own credentials (Q316), so no authority can be called it. Give it another with name=.");
            return ValueTask.CompletedTask;
        }
        if (!Permission.IsAuthorityName(name))
        {
            context.EmitError($"{name} can't be an authority's name: it has no spaces, quotes, = or # in it.");
            return ValueTask.CompletedTask;
        }

        PublicKey key;
        try
        {
            key = PublicKey.Parse(name, $"{message.KeyType} {Convert.ToBase64String(message.Key.Span)}");
        }
        catch (FormatException exception)
        {
            context.EmitError($"Authorise can't trust the key {message.Fingerprint}: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        if (store.Authorities.FirstOrDefault(key.SameKeyAs) is not null)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }
        if (store.Authority(name) is not null)
        {
            context.EmitError($"There's an authority called {name} already, with another key. Give this one another name with name=.");
            return ValueTask.CompletedTask;
        }
        try
        {
            string[] namespaces = parameters.Grants ? [TrustStore.CredentialNamespace, TrustStore.GrantNamespace] : [TrustStore.CredentialNamespace];
            store.Folder.AddAuthority(name, key, namespaces);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException or TrustStoreException)
        {
            context.EmitError($"Authorise couldn't trust the key {message.Fingerprint}: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }
        source.Reload();
        context.Emit(message);
        return ValueTask.CompletedTask;
    }

    // The key's comment, if it's a name an authority can have, or "authority-" and the start of its fingerprint.
    private static string DefaultName(AuthorityKey key) =>
        key.Comment is { } comment && Permission.IsTrustedAuthorityName(comment)
            ? comment
            : $"authority-{new string([.. key.Fingerprint["SHA256:".Length..].Where(char.IsLetterOrDigit).Take(8)]).ToLowerInvariant()}";
}

/// <summary>Parameters for <see cref="AuthoriseAuthority"/>.</summary>
public sealed record AuthoriseAuthorityParameters
{
    /// <summary>
    /// What to call it in <c>authorities</c>, the name permission definitions use in <c>authority=</c>: one word, with no
    /// quotes, <c>=</c> or <c>#</c>. Without it, the key's comment if that's one word, or <c>authority-</c> and the start of
    /// its fingerprint.
    /// </summary>
    public Computed<string>? Name { get; init; }

    /// <summary>Whether it's trusted to sign grants as well as issue credentials. False unless set.</summary>
    public bool Grants { get; init; }
}
