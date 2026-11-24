namespace Meep2.SDK.Security;

/// <summary>One reason a verification didn't pass: a code to act on, and a sentence for people.</summary>
/// <param name="Code">What went wrong, for pipelines and gates to act on.</param>
/// <param name="Reason">What went wrong, in a sentence for people, such as "The credential expired at 17:00 UTC on 3 March."</param>
public sealed record VerificationFailure(VerificationFailureCode Code, string Reason)
{
    /// <summary>The sentence.</summary>
    public override string ToString() => Reason;
}

/// <summary>Why a verification didn't pass.</summary>
public enum VerificationFailureCode
{
    /// <summary>It couldn't be read, or isn't the kind of thing that can be verified.</summary>
    Malformed,

    /// <summary>It was valid until a time that has passed.</summary>
    Expired,

    /// <summary>It's valid from a time that hasn't come yet.</summary>
    NotYetValid,

    /// <summary>The authority that issued it isn't in the trust store.</summary>
    UnknownAuthority,

    /// <summary>Its signature isn't good.</summary>
    BadSignature,

    /// <summary>It, or a grant it relies on, has been revoked.</summary>
    Revoked,

    /// <summary>
    /// Whether it's revoked can't be known: the revocation list from its issuer is out of date (Q274), so it fails
    /// until the list is replaced.
    /// </summary>
    RevocationUnknown,

    /// <summary>A certificate's chain couldn't be built to a root: an intermediate is missing.</summary>
    ChainIncomplete,

    /// <summary>A certificate's chain ends at a root the trust store doesn't trust.</summary>
    UntrustedRoot,

    /// <summary>It isn't for this use, such as a host's certificate presented as a user's, or the wrong key usage.</summary>
    WrongUsage,

    /// <summary>It has a critical option Meep doesn't understand, so it can't be honoured.</summary>
    UnknownCriticalOption,

    /// <summary>The permission isn't defined in the trust store, nor narrowed from one that is.</summary>
    UnknownPermission,

    /// <summary>The permission claims looser requirements than its definition (Q257).</summary>
    WiderThanDefinition,

    /// <summary>The permission refers to an authority or permission that isn't defined, or requires itself.</summary>
    UndefinedRequirement,

    /// <summary>The credentials among the message's ancestors don't meet the permission's requirements.</summary>
    MissingRequirement,

    /// <summary>Whoever presented it didn't prove they hold its key, and it can't be used as a bearer credential.</summary>
    NoProofOfPossession,

    /// <summary>The request's signature was made too long ago.</summary>
    StaleSignature,

    /// <summary>The request's signature has been seen before.</summary>
    Replayed,

    /// <summary>The request's signature is for another server.</summary>
    WrongAudience,

    /// <summary>The request's body doesn't match the digest that was signed.</summary>
    BodyMismatch,
}
