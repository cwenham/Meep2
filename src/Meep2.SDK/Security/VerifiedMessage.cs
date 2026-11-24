using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// The result of verifying a credential, certificate or permission: whether it passed, every reason it didn't, and
/// the permissions it established. <c>Verify</c> emits one; a gate lets a message through if one that passed, with
/// the permission it needs, is among the message's ancestors.
/// </summary>
/// <remarks>
/// <para>
/// A failure is a result, not a fault, so a failed verification is a <see cref="VerifiedMessage"/> with
/// <see cref="Failures"/>, not an error. It expires when the earliest thing it relied on does.
/// </para>
/// <para>
/// Only the runtime can make one: the constructor is internal to the SDK, nothing reads one in from JSON, and a
/// pipeline can't be sent one from outside. A copy made with <c>with</c> is a new message that no <c>Verify</c>
/// emitted, so it isn't <see cref="IsGenuine">genuine</see>, and gates ignore it. Plugins run in the same process
/// and could get round this with reflection; plugins are trusted code, signed or pinned by SHA, so the trust
/// boundary is the process.
/// </para>
/// </remarks>
public sealed record VerifiedMessage : Message
{
    /// <summary>Made by the runtime's <c>Verify</c>, and by tests.</summary>
    internal VerifiedMessage(Message subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Subject = subject;
        IsGenuine = true;
    }

    // A copy is a new message no Verify emitted, so it isn't genuine.
    private VerifiedMessage(VerifiedMessage original)
        : base(original)
    {
        Subject = original.Subject;
        Kind = original.Kind;
        Failures = original.Failures;
        Permissions = original.Permissions;
        CheckedAt = original.CheckedAt;
        TrustStoreVersion = original.TrustStoreVersion;
        RevocationVersion = original.RevocationVersion;
        Proof = original.Proof;
    }

    /// <summary>What was verified: a <see cref="Credential"/>, <see cref="Certificate"/> or <see cref="Permission"/>.</summary>
    public Message Subject { get; }

    /// <summary>What kind of thing was verified.</summary>
    public VerificationKind Kind { get; internal init; }

    /// <summary>Whether it passed: true when there are no <see cref="Failures"/>.</summary>
    public bool Success => Failures.Count == 0;

    /// <summary>Every reason it didn't pass, or none if it did.</summary>
    public IReadOnlyList<VerificationFailure> Failures { get; internal init; } = [];

    /// <summary>
    /// The permissions it established: a credential's, with the grants that apply to it, or a permission met. Empty
    /// if it didn't pass.
    /// </summary>
    public IReadOnlyList<PermissionName> Permissions { get; internal init; } = [];

    /// <summary>When it was checked, in UTC.</summary>
    public DateTime CheckedAt { get; internal init; } = DateTime.UtcNow;

    /// <summary>The version of the trust store it was checked against.</summary>
    public long TrustStoreVersion { get; internal init; }

    /// <summary>
    /// The version of the revocation list it was checked against. A gate checks again only when the list has changed
    /// since (Q259).
    /// </summary>
    public long RevocationVersion { get; internal init; }

    /// <summary>How whoever presented it proved they hold its key, if they did (Q253).</summary>
    public Proof Proof { get; internal init; }

    /// <summary>
    /// Whether the runtime made this message, rather than it being a copy of one. Gates count only genuine ones,
    /// emitted by a <c>Verify</c>.
    /// </summary>
    public bool IsGenuine { get; }

    /// <inheritdoc/>
    public override string ToText() => Success
        ? $"{Kind} verified"
        : $"{Kind} not verified: {string.Join(" ", Failures.Select(failure => failure.Reason))}";
}

/// <summary>What kind of thing a <see cref="VerifiedMessage"/> verified.</summary>
public enum VerificationKind
{
    /// <summary>A <see cref="Security.Credential"/>, with the grants that apply to it.</summary>
    Credential,

    /// <summary>An X.509 <see cref="Security.Certificate"/>.</summary>
    Certificate,

    /// <summary>A <see cref="Security.Permission"/>: that it's well defined, and perhaps met.</summary>
    Permission,
}

/// <summary>How whoever presented a credential or certificate proved they hold its key (Q253).</summary>
public enum Proof
{
    /// <summary>No proof, such as for a permission, or a verification that failed before it got that far.</summary>
    None,

    /// <summary>None was needed: a bearer credential, allowed by its issuer and by the pipeline.</summary>
    Bearer,

    /// <summary>A signature over the request it came with (HTTP Message Signatures, RFC 9421).</summary>
    RequestSignature,

    /// <summary>A signature over a challenge when the connection was made, kept with the connection.</summary>
    Connection,

    /// <summary>The TLS handshake, for a client certificate.</summary>
    Tls,
}

/// <summary>Messages only the runtime may make, which must never come into a pipeline from outside.</summary>
internal static class RuntimeOnly
{
    /// <summary>Whether <paramref name="message"/> is one only the runtime may make.</summary>
    public static bool Is(Message message) => message is VerifiedMessage or Certificate { ProvenByTls: true };

    /// <summary>Whether <paramref name="message"/> or any of its ancestors is one only the runtime may make.</summary>
    public static bool IsIn(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var queue = new Queue<Message>();
        var seen = new HashSet<Message>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(message);
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next))
                continue;
            if (Is(next))
                return true;
            foreach (var parent in next.Parents)
                queue.Enqueue(parent);
        }
        return false;
    }
}
