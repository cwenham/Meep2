using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// A pipeline's own credential (Q252): who vouches for the pipeline, and so what it may do on the machine. Every
/// message a source emits descends from the runtime's verification of it, so Meep's own permissions, such as
/// <c>meep.files.write</c>, are held by the pipeline's messages unless the pipeline starts with less.
/// </summary>
/// <remarks>
/// <para>
/// The host that loads a pipeline says which it gets:
/// <list type="bullet">
/// <item>A signed pipeline, from a URL, Git or a file, gets its signer's.</item>
/// <item>An unsigned pipeline from this machine (a file of the owner's alone, standard input, text sent to the daemon) gets the owner's,
/// the user Meep runs as. So does one from a URL or Git whose address pins its exact content, since the owner chose
/// that content.</item>
/// <item>A pipeline nobody vouches for, such as an unsigned one from a URL in developer mode, gets none.</item>
/// </list>
/// An unsigned file gets the owner's only if it, and its folder, are the owner's alone, so nobody else can have
/// written it. Pipelines made in code, as embedding and tests make them, get none unless their options say otherwise.
/// </para>
/// <para>
/// A pipeline's credential holds only Meep's own permissions (<see cref="MeepPermissions.ForPipelines"/>), never
/// names people define. A message that came in from outside, such as a request to a Listen, descends from the
/// pipeline's verification too, so a gate on a name people define still needs a verified credential of its own.
/// </para>
/// <para>
/// This only describes the credential, and anyone can make one. It gives nothing until the runtime verifies it when
/// the pipeline starts, which only the host that builds the pipeline can arrange, through its options.
/// </para>
/// </remarks>
public sealed record PipelineCredential : Message
{
    private PipelineCredential(PipelineVouch vouch, string holder, string why)
    {
        Vouch = vouch;
        Holder = holder;
        Why = why;
    }

    /// <summary>Who vouches for the pipeline.</summary>
    public PipelineVouch Vouch { get; }

    /// <summary>Who holds it, for people: the owner's user name, or the signer's name as you trust it.</summary>
    public string Holder { get; }

    /// <summary>The SHA-256 fingerprint of the signer's key, as in <c>SHA256:…</c>, or null if it wasn't signed.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>Why the pipeline has this credential, as a clause: "it's a file on this machine".</summary>
    public string Why { get; }

    /// <summary>The permissions it holds before any <c>#holds</c> line narrows them: Meep's own, or none.</summary>
    public IReadOnlyList<PermissionName> Permissions => Vouch == PipelineVouch.Nobody ? [] : MeepPermissions.ForPipelines;

    /// <summary>The owner's credential, for a pipeline the user Meep runs as put there or chose.</summary>
    /// <param name="why">Why, as a clause, such as "it's a file on this machine".</param>
    public static PipelineCredential Owner(string why = "it was made on this machine")
    {
        ArgumentNullException.ThrowIfNull(why);
        return new PipelineCredential(PipelineVouch.Owner, Environment.UserName, why);
    }

    /// <summary>A signer's credential, for a pipeline signed by a key you trust.</summary>
    /// <param name="signer">The signer's name, as your trusted keys have it.</param>
    /// <param name="fingerprint">The SHA-256 fingerprint of the signer's key.</param>
    public static PipelineCredential SignedBy(string signer, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return new PipelineCredential(PipelineVouch.Signer, signer, $"it's signed by {signer}") { KeyFingerprint = fingerprint };
    }

    /// <summary>No credential, for a pipeline nobody vouches for: it holds none of Meep's permissions.</summary>
    /// <param name="why">Why, as a clause, such as "it came from a URL without a signature".</param>
    public static PipelineCredential None(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new PipelineCredential(PipelineVouch.Nobody, "nobody", why);
    }

    /// <summary>The credential in words: "the owner's (alice), as it's a file on this machine".</summary>
    public override string ToText() => Vouch switch
    {
        PipelineVouch.Owner => $"the owner's ({Holder}), as {Why}",
        PipelineVouch.Signer => $"{Holder}'s, as {Why}",
        _ => $"none, as {Why}",
    };
}

/// <summary>Who vouches for a pipeline, and so what its own credential holds.</summary>
public enum PipelineVouch
{
    /// <summary>Nobody: it holds none of Meep's permissions.</summary>
    Nobody,

    /// <summary>The user Meep runs as, who put the pipeline there or chose it.</summary>
    Owner,

    /// <summary>Someone whose key you trust, who signed it.</summary>
    Signer,
}
