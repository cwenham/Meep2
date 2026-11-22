namespace Meep2.Plugins;

/// <summary>
/// Signatures on pipeline files (goal 7, Q36): <c>ticker.meep.sig</c> beside <c>ticker.meep</c>, an SSH signature of the
/// file's bytes in the namespace <c>meep-pipeline</c>, as <c>meep sign ticker.meep</c> makes with ssh-keygen. A
/// pipeline loaded from a URL or Git needs one from a key you trust, unless its address pins what it should be.
/// </summary>
public static class PipelineSignature
{
    /// <summary>The SSH signature namespace for pipelines, so a signature on a pipeline can't pass for anything else.</summary>
    public const string Namespace = "meep-pipeline";

    /// <summary>The signature file beside a pipeline file.</summary>
    public static string FileFor(string pipeline) => pipeline + ".sig";

    /// <summary>Checks a pipeline's signature, returning the trusted key that made it.</summary>
    /// <param name="text">The pipeline's text, as it was signed.</param>
    /// <param name="signature">The armoured signature, or null if there's none.</param>
    /// <param name="trusted">The keys you trust.</param>
    /// <param name="what">The pipeline, as messages name it.</param>
    /// <exception cref="UnsignedPluginException">There's no signature.</exception>
    /// <exception cref="UntrustedSignatureException">The signature doesn't check out, or isn't from a key you trust.</exception>
    public static PublicKey Verify(byte[] text, string? signature, TrustedKeys trusted, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(trusted);
        if (signature is null)
            throw new UnsignedPluginException($"{what} isn't signed: there's no {Path.GetFileName(FileFor(what))} beside it.");
        SshSignature parsed;
        try
        {
            parsed = SshSignature.Parse(signature);
        }
        catch (FormatException exception)
        {
            throw new UntrustedSignatureException($"{what}'s signature isn't one Meep can read: {exception.Message}");
        }
        if (parsed.Namespace != Namespace)
            throw new UntrustedSignatureException($"{what}'s signature was made for {parsed.Namespace}, not for a pipeline.");
        if (trusted.Find(parsed.Key, Namespace) is not { } signer)
            throw new UntrustedSignatureException($"{what} is signed with {parsed.Key.Fingerprint}, which isn't a key you trust. Trust it with: meep keys trust");
        if (!parsed.Verifies(text))
            throw new UntrustedSignatureException($"{what}'s signature doesn't match it: it has changed since it was signed.");
        return signer;
    }

    /// <summary>
    /// Checks a Git commit's SSH signature (Q134), returning the key you trust for pipelines that made it, or null
    /// with why not. A commit's signature covers every file in it.
    /// </summary>
    /// <param name="signedData">The commit, as it was signed.</param>
    /// <param name="signature">The commit's armoured signature.</param>
    /// <param name="trusted">The keys you trust.</param>
    /// <param name="whyNot">Why the commit's signature can't be trusted, when it can't.</param>
    public static PublicKey? VerifyCommit(byte[] signedData, string signature, TrustedKeys trusted, out string? whyNot)
    {
        ArgumentNullException.ThrowIfNull(signedData);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(trusted);
        SshSignature parsed;
        try
        {
            parsed = SshSignature.Parse(signature);
        }
        catch (FormatException)
        {
            whyNot = "Its commit is signed, but not with SSH, so Meep can't check it.";
            return null;
        }
        if (parsed.Namespace != "git" || !parsed.Verifies(signedData))
        {
            whyNot = "Its commit's signature doesn't match the commit.";
            return null;
        }
        if (trusted.Find(parsed.Key, Namespace) is not { } signer)
        {
            whyNot = $"Its commit is signed with {parsed.Key.Fingerprint}, which isn't a key you trust.";
            return null;
        }
        whyNot = null;
        return signer;
    }
}
