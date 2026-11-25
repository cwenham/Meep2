using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime;

/// <summary>
/// Revoke's credential inflection: revokes a credential in the trust store, by its authority and serial, or with
/// <c>whole-key=true</c>, its key and every credential for it. Gates notice at once, through the store's revocation version
/// (Q259). In Meeplang: <c>Revoke</c>, under a <c>Parse</c> that gives it the credential.
/// </summary>
/// <remarks>
/// Revoke needs <c>RevokePermission</c>, and <c>@permission</c> adds to that. The credential holding it must have
/// been proven, by a signed request, a connection or TLS, unless the line's <c>@proof</c> says otherwise
/// (<c>@proof=any</c> accepts a bearer credential). What it revokes must come from a part of a signed request the
/// signature covers, if it came from a request at all (<see cref="Verifying.SignedParts"/>). It emits what it revoked, whether it was
/// revoked just now or already.
/// </remarks>
[Verb("Revoke", Inflection = "Credential")]
[EmitsInput]
[RequiresPermission(SecurityPermissions.RevokePermission)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class RevokeCredential(RevokeCredentialParameters parameters) : AInflection<Credential>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Credential message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message.Serial == 0 && !parameters.WholeKey)
        {
            context.EmitError($"{message.KeyId}'s credential has no serial, so it can only be revoked by its key: Revoke whole-key=true.");
            return ValueTask.CompletedTask;
        }
        RevokeWork.Record(context, message, message.Blob.Span, parameters.WholeKey ? Revoking.KeyOf(message) : Revoking.Credential(message));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="RevokeCredential"/>.</summary>
public sealed record RevokeCredentialParameters
{
    /// <summary>
    /// Whether to revoke the credential's key, and so every credential for it and everything it signed as an
    /// authority, rather than this one credential by its authority and serial.
    /// </summary>
    public bool WholeKey { get; init; }
}

/// <summary>
/// Revoke's grant inflection: revokes a grant in the trust store, by its ID, leaving its credential and other grants
/// alone (Q254). In Meeplang: <c>Revoke</c>, under the <c>Authorise</c> that made the grant, or a <c>Parse</c>.
/// </summary>
/// <remarks>It needs <c>RevokePermission</c>, proven as the credential inflection's remarks say. It emits the grant.</remarks>
[Verb("Revoke", Inflection = "Grant")]
[EmitsInput]
[RequiresPermission(SecurityPermissions.RevokePermission)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class RevokeGrant : AInflection<Grant>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Grant message, ModuleContext context, CancellationToken cancellationToken)
    {
        RevokeWork.Record(context, message, message.ToBlob(), Revoking.Grant(message));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Revoke's certificate inflection: revokes an X.509 certificate in the trust store, by its SHA-256 hash. In Meeplang:
/// <c>Revoke</c>, under a <c>Parse</c> that gives it the certificate.
/// </summary>
/// <remarks>It needs <c>RevokePermission</c>, proven as the credential inflection's remarks say. It emits the certificate.</remarks>
[Verb("Revoke", Inflection = "Certificate")]
[EmitsInput]
[RequiresPermission(SecurityPermissions.RevokePermission)]
[DefaultProof(Proof.RequestSignature, Proof.Connection, Proof.Tls)]
public sealed class RevokeCertificate : AInflection<Certificate>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Certificate message, ModuleContext context, CancellationToken cancellationToken)
    {
        RevokeWork.Record(context, message, message.Der.Span, Revoking.Certificate(message));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Where Revoke's inflections write.</summary>
file static class RevokeWork
{
    // Checks what to revoke came from a covered part of any signed request, writes the revocation, unless it's there already, reads the store again so gates notice at once, and emits the
    // message; or emits an error saying why it couldn't.
    public static void Record(ModuleContext context, Message message, ReadOnlySpan<byte> blob, Revoking revoking)
    {
        // What to revoke must come from parts of a signed request its signature covers.
        if (SignedParts.WhyNotCovered(message, Convert.ToBase64String(blob), whole: false, "what to revoke") is { } moved)
        {
            context.EmitError($"Revoke didn't revoke {revoking.What}. {moved}");
            return;
        }
        if (Trust.SourceOf(context) is not { Current: { } store } source)
        {
            context.EmitError(Trust.NoStore);
            return;
        }
        if (!store.Revocations.Entries.Contains(revoking.Revocation))
        {
            try
            {
                store.Folder.Revoke(revoking);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.EmitError($"Revoke couldn't revoke {revoking.What}: {exception.Message}", exception);
                return;
            }
            source.Reload();
        }
        context.Emit(message);
    }
}
