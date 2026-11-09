using System.Diagnostics;

namespace Meep2.Tests.Support;

/// <summary>A Git repository in a folder, made and inspected with the git command, for checking write-back.</summary>
public static class GitRepo
{
    private static readonly Lazy<bool> s_available = new(() =>
    {
        try
        {
            return Run(Path.GetTempPath(), "--version").Code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Whether the git command is on this machine.</summary>
    public static bool Available => s_available.Value;

    /// <summary>
    /// Makes a repository in a folder and commits what's in it to main. It has a signing key of its own in .git, set
    /// as Git sets one for SSH signing, and trusts it, so write-back's signatures can be checked (Q126).
    /// </summary>
    public static void Init(string folder)
    {
        Check(Run(folder, "init", "-q", "-b", "main"));
        Check(Run(folder, "config", "user.name", "Tester"));
        Check(Run(folder, "config", "user.email", "tester@example.com"));
        var key = Path.Combine(folder, ".git", "signing-key");
        var keygen = Process.Start(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "tester", "-f", key]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        keygen.WaitForExit();
        File.WriteAllText(Path.Combine(folder, ".git", "allowed_signers"), $"tester@example.com namespaces=\"git\" {File.ReadAllText(key + ".pub").Trim()}\n");
        Check(Run(folder, "config", "gpg.format", "ssh"));
        Check(Run(folder, "config", "user.signingkey", key));
        Check(Run(folder, "config", "gpg.ssh.allowedSignersFile", Path.Combine(folder, ".git", "allowed_signers")));
        Check(Run(folder, "add", "-A"));
        Check(Run(folder, "commit", "-q", "-m", "First"));
    }

    /// <summary>Runs git in a folder, returning its exit code and what it printed.</summary>
    public static (int Code, string Output) Run(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }

    private static void Check((int Code, string Output) result)
    {
        if (result.Code != 0)
            throw new InvalidOperationException($"git failed: {result.Output}");
    }
}
