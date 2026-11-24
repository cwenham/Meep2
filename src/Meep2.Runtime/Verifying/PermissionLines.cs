using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// Whether the credentials verified among a message's ancestors meet a permission (Q257): every line of the message's
/// family, from it back to a message with no parents, passes through a genuine verification of a credential that
/// passed, hasn't expired, and holds the permission, conferred by an authority the permission allows, and holds every
/// permission it requires in the same way (Q261). <c>Verify.Permission</c> asks this ahead of time; phase 4's gates
/// will ask it before delivering.
/// </summary>
/// <remarks>
/// A verification made before the trust store last took something away (its <see cref="TrustStore.RevocationVersion"/>
/// has moved on) is looked at again: its credential and grants are checked for revocation, and its authorities are
/// looked up in the store as it is now (Q259).
/// </remarks>
internal sealed class PermissionLines(TrustStore store, DateTime now)
{
    // Each message's answer, so a family that joins again isn't walked twice.
    private readonly Dictionary<Message, string?> _lines = new(ReferenceEqualityComparer.Instance);
    private Permission _permission = null!;

    /// <summary>Whether a genuine verification of a credential is among <paramref name="message"/>'s ancestors, or is it.</summary>
    public static bool HasCredentials(Message message) =>
        Ancestry.Of(message).Any(m => m is VerifiedMessage { IsGenuine: true, Kind: VerificationKind.Credential });

    /// <summary>Why the credentials on <paramref name="message"/>'s lines don't meet <paramref name="permission"/>, or null if they do.</summary>
    public string? WhyNotMet(Message message, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(permission);
        _lines.Clear();
        _permission = permission;
        return Line(message);
    }

    // Why this line doesn't meet the permission, or null if every line through this message does.
    private string? Line(Message message)
    {
        if (_lines.TryGetValue(message, out var known))
            return known;
        _lines[message] = "its family loops back on itself"; // never true of a real family, but nothing can then loop
        string? here = null;
        if (message is VerifiedMessage { IsGenuine: true, Kind: VerificationKind.Credential } verified)
        {
            here = WhyNotHeld(verified, _permission.Name, _permission.Authorities, _permission.Requires, []);
            if (here is null)
                return _lines[message] = null;
        }
        string? answer;
        if (message.Parents.Count == 0)
        {
            answer = here ?? "Nothing on one of the lines of this message's family is a verified credential.";
        }
        else
        {
            answer = null;
            foreach (var parent in message.Parents)
            {
                if (Line(parent) is { } why)
                {
                    answer = here ?? why;
                    break;
                }
            }
        }
        return _lines[message] = answer;
    }

    // Why a verification doesn't hold a permission, conferred by one of these authorities (any, if none), with these
    // requirements, or null if it does.
    private string? WhyNotHeld(VerifiedMessage verified, PermissionName name, IReadOnlyList<string> authorities, IReadOnlyList<PermissionName> requires, HashSet<string> visiting)
    {
        if (verified.Subject is not Credential credential)
            return "A verification that says it's of a credential isn't of one.";
        var who = $"{credential.KeyId}'s credential";
        if (!verified.Success)
            return $"{who} didn't verify: {string.Join(" ", verified.Failures.Select(f => f.Reason))}";
        if (verified.Expires is { HasPassed: true })
            return $"The verification of {who} has expired.";
        var recheck = verified.RevocationVersion != store.RevocationVersion;
        if (recheck && store.Revocations.WhyRevoked(credential) is { } revoked)
            return revoked;

        var held = Held(verified, credential, recheck);
        var matching = held.Where(h => h.Permission.Covers(name)).ToList();
        if (matching.Count == 0)
            return $"{who} doesn't hold {name}.";
        if (authorities.Count > 0 && !matching.Any(h => h.Authority is { } by && authorities.Contains(by, StringComparer.Ordinal)))
        {
            var by = string.Join(", ", matching.Select(h => h.Authority ?? "an authority no longer trusted").Distinct());
            return $"{who} holds {name}, conferred by {by}, but only {string.Join(", ", authorities)} may confer it.";
        }
        if (authorities.Count == 0 && matching.All(h => h.Authority is null))
            return $"{who} holds {name}, but the authority that conferred it is no longer trusted.";

        if (!visiting.Add(name.Name))
            return $"{name.Name} requires itself.";
        foreach (var required in requires)
        {
            var definition = store.Permissions.Find(required.Name);
            var why = WhyNotHeld(verified, required, definition?.Authorities ?? [], definition?.Requires ?? [], visiting);
            if (why is not null)
                return $"{name} requires {required}: {why}";
        }
        visiting.Remove(name.Name);
        return null;
    }

    // The permissions a verification holds and the names of the authorities that conferred them, as the store is now:
    // the credential's own, from its issuer, and its grants', from their signers. A grant revoked since is left out.
    private List<Conferred> Held(VerifiedMessage verified, Credential credential, bool recheck)
    {
        var issuer = store.AuthorityOf(credential)?.Name;
        var held = credential.Permissions.Select(permission => new Conferred(permission, issuer)).ToList();
        foreach (var grant in verified.Grants)
        {
            if (recheck && store.Revocations.WhyRevoked(grant) is not null)
                continue;
            if (grant.Terms.ValidBefore is { } before && now >= before)
                continue;
            held.Add(new Conferred(grant.Terms.Permission, store.AuthorityOf(grant)?.Name));
        }
        return held;
    }

    private sealed record Conferred(PermissionName Permission, string? Authority);
}
