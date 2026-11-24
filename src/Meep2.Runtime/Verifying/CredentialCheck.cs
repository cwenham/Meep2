using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// The checks of a credential against the trust store (Q256): it's a user's certificate with no critical option Meep
/// doesn't understand, issued and signed by a trusted authority, in date and not revoked; and which grants apply to it,
/// recorded in the store or carried among the message's ancestors, each checked in the same way, with a note for each
/// left out (Q283). Proof of possession
/// is checked separately (<see cref="RequestSignature"/>).
/// </summary>
internal static class CredentialCheck
{
    /// <summary>Checks <paramref name="credential"/>, adding what it finds to <paramref name="verification"/>.</summary>
    /// <param name="verification">The verification under way.</param>
    /// <param name="credential">The credential.</param>
    /// <param name="carrier">The message it came with, among whose ancestors carried grants are found.</param>
    public static void Check(Verification verification, Credential credential, Message carrier)
    {
        var store = verification.Store;
        var now = verification.Now;
        var who = $"{credential.KeyId}'s credential";

        if (credential.Kind != SshCertificateKind.User)
            verification.Fail(VerificationFailureCode.WrongUsage, $"{who} is a host's certificate, and a credential is a user's.");
        foreach (var option in credential.CriticalOptions.Keys)
            verification.Fail(VerificationFailureCode.UnknownCriticalOption, $"{who} has the critical option {option}, which Meep doesn't understand, so it can't be honoured.");

        var authority = store.AuthorityOf(credential);
        if (authority is null)
            verification.Fail(VerificationFailureCode.UnknownAuthority, $"{who} was issued by {credential.AuthorityFingerprint}, which this machine doesn't trust to issue credentials.");
        else if (!authority.Verify(credential.SignedData.ToArray(), credential.Signature.ToArray()))
            verification.Fail(VerificationFailureCode.BadSignature, $"{who} says {authority.Name} issued it, but its signature isn't {authority.Name}'s.");

        if (credential.ValidAfter is { } after && now < after)
            verification.Fail(VerificationFailureCode.NotYetValid, $"{who} isn't valid until {Time(after)}.");
        if (credential.ValidBefore is { } before && now >= before)
            verification.Fail(VerificationFailureCode.Expired, $"{who} expired at {Time(before)}.");
        verification.ReliesOnUntil(credential.ValidBefore);

        if (store.Revocations.WhyRevoked(credential) is { } revoked)
            verification.Fail(VerificationFailureCode.Revoked, revoked);

        foreach (var permission in credential.Permissions)
            verification.Establish(permission);
        foreach (var grant in GrantsFor(credential, carrier, store))
        {
            if (WhyNotGood(grant, store, now) is { } leftOut)
            {
                verification.Note($"{leftOut} So it doesn't give {grant.Terms.Permission}.");
            }
            else
            {
                verification.Establish(grant);
                verification.ReliesOnUntil(grant.Terms.ValidBefore);
            }
        }
    }

    /// <summary>
    /// Why a grant doesn't count, or null if it does: signed in the grant namespace by an authority trusted to sign
    /// grants, in date, and not revoked.
    /// </summary>
    public static string? WhyNotGood(Grant grant, TrustStore store, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(store);
        if (store.AuthorityOf(grant) is not { } signer)
            return $"The grant {grant.Terms.Id} was signed by {grant.AuthorityFingerprint}, which this machine doesn't trust to sign grants.";
        SshSignature signature;
        try
        {
            signature = SshSignature.FromBinary(grant.Signature.Span);
        }
        catch (FormatException exception)
        {
            return $"The grant {grant.Terms.Id}'s signature can't be read: {exception.Message}.";
        }
        if (signature.Namespace != Grant.Namespace || !signature.Key.SameKeyAs(signer) || !signature.Verifies(grant.Terms.ToSignedData()))
            return $"The grant {grant.Terms.Id} says {signer.Name} signed it, but its signature isn't {signer.Name}'s.";
        if (grant.Terms.ValidAfter is { } after && now < after)
            return $"The grant {grant.Terms.Id} isn't valid until {Time(after)}.";
        if (grant.Terms.ValidBefore is { } before && now >= before)
            return $"The grant {grant.Terms.Id} expired at {Time(before)}.";
        return store.Revocations.WhyRevoked(grant);
    }

    /// <summary>A time as people read it in reasons: <c>17:00 UTC on 3 March 2026</c>.</summary>
    public static string Time(DateTime time) =>
        time.ToUniversalTime().ToString("HH:mm 'UTC on' d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // The grants recorded for the credential, and those carried among the message's ancestors that name it, once each.
    private static IEnumerable<Grant> GrantsFor(Credential credential, Message carrier, TrustStore store)
    {
        var seen = new HashSet<Guid>();
        foreach (var grant in store.GrantsFor(credential))
        {
            if (seen.Add(grant.Terms.Id))
                yield return grant;
        }
        foreach (var grant in Ancestry.Of(carrier).OfType<Grant>())
        {
            if (grant.Terms.AppliesTo(credential) && seen.Add(grant.Terms.Id))
                yield return grant;
        }
    }
}

/// <summary>A message and its ancestors, each once, nearest first.</summary>
internal static class Ancestry
{
    public static IEnumerable<Message> Of(Message message)
    {
        var queue = new Queue<Message>();
        var seen = new HashSet<Message>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(message);
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next))
                continue;
            yield return next;
            foreach (var parent in next.Parents)
                queue.Enqueue(parent);
        }
    }
}
