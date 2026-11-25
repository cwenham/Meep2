using System.Diagnostics;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// Whether the credentials verified among a message's ancestors meet a permission (Q257): every line of the message's
/// family, from it back to a message with no parents, passes through a genuine verification of a credential that
/// passed, hasn't expired, and holds the permission, conferred by an authority the permission allows, and holds every
/// permission it requires in the same way (Q261). <c>Verify.Permission</c> asks this ahead of time, and gates ask it
/// before delivering.
/// </summary>
/// <remarks>
/// <para>
/// A verification made before the trust store last took something away (its <see cref="TrustStore.RevocationVersion"/>
/// has moved on) is looked at again: its credential and grants are checked for revocation, and its authorities are
/// looked up in the store as it is now (Q259).
/// </para>
/// <para>
/// For a requirement that can be kept (<see cref="Requirement.Kept"/>), answers are kept on the messages the walk
/// starts from, joins at and verifies at (<see cref="GateAnswers"/>), so another gate needing the same requirement,
/// for the same message or one made from it, stops there rather than walking on.
/// </para>
/// </remarks>
internal sealed class PermissionLines(TrustStore store, DateTime? now = null)
{
    // The moment grants are judged at: the one given, or now, read only if a verification's grants are looked at.
    private DateTime? _now = now;

    // A family can't loop, since a message's parents are made before it; these only stop a walk running away.
    private const int MostSteps = 10_000_000;
    private const int MostJoins = 1_000;
    private int _steps;

    // Each joining message's answer, for a requirement that isn't kept on messages, so a family that joins again isn't
    // walked twice.
    private Dictionary<Message, LineAnswer>? _joins;

    // The credentials that met the requirement, one for each line, while Holders is asking.
    private List<Credential>? _holders;

    /// <summary>Whether a genuine verification of a credential is among <paramref name="message"/>'s ancestors, or is it.</summary>
    public static bool HasCredentials(Message message) =>
        Ancestry.Of(message).Any(m => m is VerifiedMessage { IsGenuine: true, Kind: VerificationKind.Credential });

    /// <summary>Why the credentials on <paramref name="message"/>'s lines don't meet <paramref name="permission"/>, or null if they do.</summary>
    public string? WhyNotMet(Message message, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return WhyNotMet(message, new Requirement(permission));
    }

    /// <summary>Why the credentials on <paramref name="message"/>'s lines don't meet <paramref name="requirement"/>, or null if they do.</summary>
    public string? WhyNotMet(Message message, Requirement requirement)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.Problem is { } problem)
            return problem;
        _joins = null;
        _steps = 0;
        var answer = Line(message, requirement, 0);
        Finish(message, requirement, answer);
        return answer.WhyNot;
    }

    /// <summary>
    /// The credentials that meet <paramref name="requirement"/> on <paramref name="message"/>'s lines: for each line,
    /// the nearest verified credential that holds it, as a gate finds them, without a repeat. Empty if any line doesn't
    /// meet it. <c>Authorise</c> names them in a grant as who authorised it (Q301). Answers kept on messages are
    /// passed by, since they don't say who.
    /// </summary>
    public IReadOnlyList<Credential> Holders(Message message, Requirement requirement)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.Problem is not null || requirement.Kept)
            return [];
        _joins = null;
        _steps = 0;
        _holders = [];
        try
        {
            return Line(message, requirement, 0).WhyNot is null ? [.. _holders.Distinct<Credential>(ReferenceEqualityComparer.Instance)] : [];
        }
        finally
        {
            _holders = null;
        }
    }

    // One line, from a message up its single parents until an answer: one kept on a message, a verification that holds
    // the permission, a message with no parents, or a join, whose parents' lines are each walked in turn (the only
    // recursion, so a long family costs no stack). If the line fails, a verification on the way that doesn't hold the
    // permission gives the reason, the nearest to the message first, as it says best what's wrong.
    private LineAnswer Line(Message message, Requirement requirement, int joins)
    {
        string? nearest = null;
        var current = message;
        LineAnswer answer;
        while (true)
        {
            if (++_steps > MostSteps)
                return new LineAnswer("This message's family is too large to check.", 0);
            if (Known(current, requirement) is { } known)
            {
                answer = known;
                break;
            }
            string? here = null;
            if (current is VerifiedMessage { IsGenuine: true, Kind: VerificationKind.Credential } verified && verified.IsStamped)
            {
                here = WhyNotHeld(verified, requirement.Permission.Name, requirement.Target, requirement.Permission.Authorities, requirement.Permission.Requires, [])
                    ?? WhyNotProven(verified, requirement.Proofs);
                if (here is null)
                {
                    if (verified.Subject is Credential holder)
                        _holders?.Add(holder);
                    answer = Finish(current, requirement, new LineAnswer(null, verified.ExpiresTimestamp));
                    break;
                }
                nearest ??= here;
            }
            var parents = current.Parents;
            if (parents.Count == 1)
            {
                current = parents[0];
                continue;
            }
            if (parents.Count == 0)
            {
                answer = new LineAnswer(here ?? "Nothing on one of the lines of this message's family is a verified credential.", 0);
                break;
            }
            if (joins >= MostJoins)
                return new LineAnswer("This message's family joins too many times over to check.", 0);
            string? why = null;
            long until = 0;
            foreach (var parent in parents)
            {
                var line = Line(parent, requirement, joins + 1);
                if (line.WhyNot is { } not)
                {
                    why = here ?? not;
                    break;
                }
                until = Earliest(until, line.Until);
            }
            answer = Finish(current, requirement, new LineAnswer(why, why is null ? until : 0));
            break;
        }
        return answer.WhyNot is not null && nearest is not null ? new LineAnswer(nearest, 0) : answer;
    }

    // A message's answer, kept on it, or for a requirement that isn't kept, from this walk.
    private LineAnswer? Known(Message message, Requirement requirement)
    {
        if (requirement.Kept)
            return GateAnswers.Find(message, requirement);
        return _joins is not null && _joins.TryGetValue(message, out var answer) ? answer : null;
    }

    // Keeps an answer on a message: the one the walk started from, a join, or a verification. For a requirement that
    // isn't kept on messages, only joins are remembered, for this walk.
    private LineAnswer Finish(Message message, Requirement requirement, LineAnswer answer)
    {
        if (requirement.Kept)
            GateAnswers.Add(message, requirement, answer);
        else if (message.Parents.Count > 1)
            (_joins ??= new(ReferenceEqualityComparer.Instance))[message] = answer;
        return answer;
    }

    // The earlier of two Stopwatch deadlines, 0 meaning none.
    private static long Earliest(long a, long b) => a == 0 ? b : b == 0 ? a : Math.Min(a, b);

    // Why a verification doesn't hold a permission (on target, if there is one), conferred by one of these authorities
    // (any, if none), with these requirements, or null if it does.
    private string? WhyNotHeld(VerifiedMessage verified, PermissionName name, string? target, IReadOnlyList<string> authorities, IReadOnlyList<PermissionName> requires, HashSet<string> visiting)
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
        var matching = target is null
            ? held.Where(h => h.Permission.Covers(name)).ToList()
            : held.Where(h => h.Permission.Allows(name.Name, target)).ToList();
        if (matching.Count == 0)
            return target is null ? $"{who} doesn't hold {name}." : $"{who} doesn't hold {name.Name} for {target}.";
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
            var why = WhyNotHeld(verified, required, null, definition?.Authorities ?? [], definition?.Requires ?? [], visiting);
            if (why is not null)
                return $"{name} requires {required}: {why}";
        }
        visiting.Remove(name.Name);
        return null;
    }

    // Why a verification wasn't proven as a gate needs (@proof, Q299), or null if it was.
    private static string? WhyNotProven(VerifiedMessage verified, ProofSet proofs)
    {
        if (proofs.Allows(verified.Proof))
            return null;
        var who = verified.Subject is Credential credential ? $"{credential.KeyId}'s credential" : "The credential";
        return $"{who} was verified with {ProofSet.Describe(verified.Proof)}, and this needs {proofs}.";
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
            if (grant.Terms.ValidBefore is { } before && (_now ??= DateTime.UtcNow) >= before)
                continue;
            held.Add(new Conferred(grant.Terms.Permission, store.AuthorityOf(grant)?.Name));
        }
        return held;
    }

    private sealed record Conferred(PermissionName Permission, string? Authority);
}

/// <summary>
/// What a message must hold: a permission as its definition, narrowed by the pipeline, says, and the thing a module is
/// about to touch, if it named one (<see cref="SDK.Modules.ModuleContext.Demand"/>), or why it can't be held at all.
/// </summary>
internal sealed class Requirement
{
    /// <summary>A permission to check as it stands, as <c>Verify.Permission</c> does; answers aren't kept on messages.</summary>
    public Requirement(Permission permission, string? target = null)
    {
        Permission = permission;
        Target = target;
        Name = permission.Name;
    }

    /// <summary>A requirement that can't be met, such as a permission that isn't defined, saying why.</summary>
    public Requirement(PermissionName name, string problem)
    {
        Permission = new Permission(name);
        Name = name;
        Problem = problem;
    }

    /// <summary>The permission as written at the gate, for saying what was needed.</summary>
    public PermissionName Name { get; init; }

    /// <summary>The permission, as defined and narrowed, that the message's credentials must hold.</summary>
    public Permission Permission { get; }

    /// <summary>What a scoped permission must match, such as a file's full path, or null.</summary>
    public string? Target { get; }

    /// <summary>Why no message can hold it, or null.</summary>
    public string? Problem { get; }

    /// <summary>The kinds of proof the credentials must have been verified with (<c>@proof</c>), or any.</summary>
    public ProofSet Proofs { get; init; }

    /// <summary>
    /// Whether answers for it are kept on messages: true for a gate's, which is made once for each trust store and
    /// asked for many messages; false for one made for a single check.
    /// </summary>
    public bool Kept { get; init; }
}

/// <summary>A line's answer: why it doesn't carry a permission, or null if it does, and until when a yes holds.</summary>
/// <param name="WhyNot">Why not, or null.</param>
/// <param name="Until">A <see cref="Stopwatch"/> timestamp after which a yes must be worked out again, or 0 for never.</param>
internal readonly record struct LineAnswer(string? WhyNot, long Until)
{
    /// <summary>Whether it still holds.</summary>
    public bool Current => WhyNot is not null || Until == 0 || Stopwatch.GetTimestamp() < Until;
}

/// <summary>
/// The answers kept on a message (<see cref="Message.GateAnswers"/>), by requirement. A few at most, replaced whole, so
/// gates on several threads can read and add at once; one lost in a race is only worked out again.
/// </summary>
internal static class GateAnswers
{
    private const int Most = 8;

    /// <summary>The answer kept on <paramref name="message"/> for <paramref name="requirement"/>, if it still holds.</summary>
    public static LineAnswer? Find(Message message, Requirement requirement)
    {
        switch (Volatile.Read(ref message.GateAnswers))
        {
            case Entry one when ReferenceEquals(one.Requirement, requirement):
                return one.Answer.Current ? one.Answer : null;
            case Entry[] many:
                foreach (var entry in many)
                {
                    if (ReferenceEquals(entry.Requirement, requirement))
                        return entry.Answer.Current ? entry.Answer : null;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Keeps an answer on a message, in place of any it had for the same requirement.</summary>
    public static void Add(Message message, Requirement requirement, LineAnswer answer)
    {
        var entry = new Entry(requirement, answer);
        var before = Volatile.Read(ref message.GateAnswers);
        object after = before switch
        {
            null => entry,
            Entry one when ReferenceEquals(one.Requirement, requirement) => entry,
            Entry one => new[] { one, entry },
            Entry[] many => (Entry[])[.. many.Where(e => !ReferenceEquals(e.Requirement, requirement)).TakeLast(Most - 1), entry],
            _ => entry,
        };
        Interlocked.CompareExchange(ref message.GateAnswers, after, before);
    }

    private sealed record Entry(Requirement Requirement, LineAnswer Answer);
}
