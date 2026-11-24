using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// A verification under way: what's being verified, against which store and at what moment, and what's been found so
/// far. The checks add to it, and <see cref="ToMessage"/> makes the <see cref="VerifiedMessage"/>.
/// </summary>
internal sealed class Verification(Message subject, VerificationKind kind, TrustStore store, DateTime now)
{
    private readonly List<VerificationFailure> _failures = [];
    private readonly List<PermissionName> _permissions = [];
    private readonly List<Grant> _grants = [];
    private readonly List<string> _notes = [];
    private DateTime? _until;

    /// <summary>What's being verified.</summary>
    public Message Subject => subject;

    /// <summary>The trust store it's checked against.</summary>
    public TrustStore Store => store;

    /// <summary>The moment it's checked at, in UTC.</summary>
    public DateTime Now => now;

    /// <summary>Whether anything has failed so far.</summary>
    public bool Failed => _failures.Count > 0;

    /// <summary>How whoever presented it proved they hold its key.</summary>
    public Proof Proof { get; set; }

    /// <summary>Adds a reason it doesn't pass.</summary>
    public void Fail(VerificationFailureCode code, string reason) => _failures.Add(new VerificationFailure(code, reason));

    /// <summary>Adds something found that isn't a failure, such as a grant left out, and why.</summary>
    public void Note(string note) => _notes.Add(note);

    /// <summary>Says the result relies on something that lapses at <paramref name="until"/>, if it ever does.</summary>
    public void ReliesOnUntil(DateTime? until)
    {
        if (until is { } time && (_until is null || time < _until))
            _until = time;
    }

    /// <summary>Adds a permission the result establishes, if it passes.</summary>
    public void Establish(PermissionName permission)
    {
        if (!_permissions.Contains(permission))
            _permissions.Add(permission);
    }

    /// <summary>Adds a grant that gave the credential a permission, and the permission.</summary>
    public void Establish(Grant grant)
    {
        _grants.Add(grant);
        Establish(grant.Terms.Permission);
    }

    /// <summary>
    /// The result. One that passed expires when the earliest thing it relied on does (Q259); one that failed keeps no
    /// permissions or grants, though it keeps its notes, and doesn't expire early, so a pipeline can still answer it.
    /// </summary>
    public VerifiedMessage ToMessage() => new(subject)
    {
        Kind = kind,
        Failures = [.. _failures],
        Permissions = Failed ? [] : [.. _permissions],
        Grants = Failed ? [] : [.. _grants],
        Notes = [.. _notes],
        CheckedAt = now,
        TrustStoreVersion = store.Version,
        RevocationVersion = store.RevocationVersion,
        Proof = Failed ? Proof.None : Proof,
        Expires = !Failed && _until is { } until ? Deadline.At(until) : null,
    };

    /// <summary>
    /// A result for a pipeline with no trust store, or a subject that can't be read: one failure, with the code that
    /// fits the kind of thing that was to be verified.
    /// </summary>
    public static VerifiedMessage Unchecked(Message subject, VerificationKind kind, VerificationFailureCode code, string reason) => new(subject)
    {
        Kind = kind,
        Failures = [new VerificationFailure(code, reason)],
        CheckedAt = DateTime.UtcNow,
    };
}
