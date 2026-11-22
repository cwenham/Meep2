using LibGit2Sharp;
using Meep2.SDK.Modules;

namespace Meep2.Git;

/// <summary>
/// Signs write-back's commits with your SSH key (Q126), as Git does with <c>gpg.format = ssh</c>: an SSH signature in
/// the namespace <c>git</c>, made by <c>ssh-keygen -Y sign</c>, so <c>git log --show-signature</c> can check it.
/// </summary>
/// <remarks>
/// The key is the repository's <c>user.signingkey</c> when its <c>gpg.format</c> is <c>ssh</c>, and otherwise the
/// key <c>meep sign</c> uses: the first of <c>~/.ssh/id_ed25519</c>, <c>id_ecdsa</c> and <c>id_rsa</c>. A key with a
/// passphrase needs to be in ssh-agent, since nobody is there to type it.
/// </remarks>
internal static class CommitSigning
{
    private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(15);

    /// <summary>The key to sign with, or null, with why not, if there isn't one.</summary>
    public static (string? Key, string? WhyNot) KeyFor(Repository repository)
    {
        var format = repository.Config.Get<string>("gpg.format")?.Value;
        var configured = repository.Config.Get<string>("user.signingkey")?.Value;
        if (format == "ssh" && configured is { Length: > 0 })
        {
            if (configured.StartsWith("key::", StringComparison.Ordinal))
            {
                // A public key written in the config: ssh-keygen signs with it through ssh-agent.
                var file = Path.Combine(Path.GetTempPath(), $"{TemporaryPrefix}{Guid.NewGuid():N}.pub");
                File.WriteAllText(file, configured["key::".Length..] + "\n");
                return (file, null);
            }
            var path = Expand(configured);
            return File.Exists(path) ? (path, null) : (null, $"user.signingkey names {path}, which isn't there");
        }
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var key = new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(name => Path.Combine(ssh, name)).FirstOrDefault(File.Exists);
        return key is null
            ? (null, "there's no SSH key to sign with: set user.signingkey with gpg.format = ssh, or make one with ssh-keygen -t ed25519")
            : (key, null);
    }

    // The start of the name of a file KeyFor writes for a key in the config.
    private const string TemporaryPrefix = "meep-signing-";

    /// <summary>Deletes the key's file once it has been signed with, if <see cref="KeyFor"/> wrote it (M5).</summary>
    public static void LetGo(string? key)
    {
        if (key is null || !Path.GetFileName(key).StartsWith(TemporaryPrefix, StringComparison.Ordinal)
            || Path.GetDirectoryName(Path.GetFullPath(key)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            return;
        try
        {
            File.Delete(key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the system to clear; it holds only a public key.
        }
    }

    /// <summary>Signs a commit's content, returning the armoured signature, or null, with why not, if ssh-keygen couldn't.</summary>
    public static async Task<(string? Signature, string? WhyNot)> SignAsync(string content, string key)
    {
        ProgramResult signed;
        try
        {
            using var limit = new CancellationTokenSource(s_limit);
            signed = await ProgramRunner.RunAsync(
                "ssh-keygen",
                ["-Y", "sign", "-n", "git", "-f", key],
                input: content,
                environment: new Dictionary<string, string> { ["SSH_ASKPASS_REQUIRE"] = "never" },
                cancellationToken: limit.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "ssh-keygen isn't on this machine");
        }
        catch (OperationCanceledException)
        {
            return (null, $"ssh-keygen didn't finish signing with {key}; a key with a passphrase needs to be in ssh-agent");
        }
        return signed.Succeeded && signed.Output.Contains("BEGIN SSH SIGNATURE", StringComparison.Ordinal)
            ? (signed.Output.Trim(), null)
            : (null, $"ssh-keygen couldn't sign with {key}: {signed.Error.Trim()}");
    }

    private static string Expand(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}
