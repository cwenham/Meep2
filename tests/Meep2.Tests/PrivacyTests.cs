using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Security;
using Meep2.Shared;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Meep's folder and files are kept to the user who runs it (S7).</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class PrivacyTests
{
    [Test]
    public async Task Private_folders_are_made_for_you_alone_and_an_open_one_is_narrowed()
    {
        using var folder = new TemporaryFolder();
        var home = Path.Combine(folder.Path, "home");
        var logs = Path.Combine(home, "logs");
        var open = Directory.CreateDirectory(Path.Combine(folder.Path, "open"), PrivateFolders.Folder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute).FullName;

        PrivateFolders.Create(logs);
        PrivateFolders.Create(open);

        await Assert.That(File.GetUnixFileMode(home)).IsEqualTo(PrivateFolders.Folder);
        await Assert.That(File.GetUnixFileMode(logs)).IsEqualTo(PrivateFolders.Folder);
        await Assert.That(File.GetUnixFileMode(open)).IsEqualTo(PrivateFolders.Folder);
    }

    [Test]
    public async Task Trusted_keys_that_others_can_change_arent_trusted()
    {
        using var folder = new TemporaryFolder();
        using var key = SshKeyPair.CreateEd25519("tester");
        var signers = folder.Write("allowed_signers", $"tester {key.Public.Text}\n");
        await Assert.That(TrustedKeys.Load(signers).Keys.Count).IsEqualTo(1);

        File.SetUnixFileMode(signers, PrivateFolders.File | UnixFileMode.OtherWrite);
        var refused = await Assert.That(() => TrustedKeys.Load(signers)).Throws<TrustStoreException>();

        await Assert.That(refused!.Message).StartsWith($"{signers} can be changed by other users, so Meep doesn't trust the keys in it.");
    }

    [Test]
    public async Task What_your_group_can_change_is_still_yours_alone_only_when_the_group_is_yours_alone()
    {
        using var folder = new TemporaryFolder();
        var file = folder.Write("authorities", "");
        File.SetUnixFileMode(file, PrivateFolders.File | UnixFileMode.GroupWrite);

        // Debian's rule (Q284), worked out here with the system's own tools: on Linux, the file's group is the user's
        // primary group, nobody else has it as theirs, and nobody else is listed in it.
        var alone = OperatingSystem.IsLinux() && await IsOwnGroupAloneAsync();

        await Assert.That(PrivateFolders.OthersCanWrite(file)).IsEqualTo(!alone);
        File.SetUnixFileMode(file, PrivateFolders.File | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        await Assert.That(PrivateFolders.OthersCanWrite(file)).IsTrue();
    }

    [Test]
    public async Task Narrowing_a_folder_says_whether_it_had_to()
    {
        using var folder = new TemporaryFolder();
        var open = Directory.CreateDirectory(Path.Combine(folder.Path, "open"), PrivateFolders.Folder | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute).FullName;

        await Assert.That(PrivateFolders.Narrow(open)).IsTrue();
        await Assert.That(File.GetUnixFileMode(open)).IsEqualTo(PrivateFolders.Folder);
        await Assert.That(PrivateFolders.Narrow(open)).IsFalse();
        await Assert.That(PrivateFolders.Narrow(Path.Combine(folder.Path, "missing"))).IsFalse();
    }

    private static async Task<bool> IsOwnGroupAloneAsync()
    {
        var user = (await RunAsync("id", "-un")).Trim();
        var gid = (await RunAsync("id", "-g")).Trim();
        var group = (await RunAsync("getent", "group", gid)).Trim().Split(':');
        var listed = group.Length > 3 ? group[3].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        var primary = (await RunAsync("getent", "passwd")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':')).Where(fields => fields.Length > 3 && fields[3] == gid).Select(fields => fields[0]).ToList();
        return primary.All(name => name == user) && (listed.Length == 0 || listed is [var only] && only == user) && primary.Count + listed.Length > 0;
    }

    private static async Task<string> RunAsync(string command, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(command) { RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    [Test]
    public async Task Saved_trees_and_token_files_are_yours_alone()
    {
        using var folder = new TemporaryFolder();
        var tree = BayesTree.Open(Path.Combine(folder.Path, "topics.json"));
        tree.Learn("Science", "gravity waves");
        await tree.SaveAsync();
        var tokens = Path.Combine(folder.Path, "daemon", "tokens");
        DaemonTokens.Create(tokens, "dashboard", Capabilities.Observe);

        await Assert.That(File.GetUnixFileMode(tree.Path)).IsEqualTo(PrivateFolders.File);
        await Assert.That(File.GetUnixFileMode(tokens)).IsEqualTo(PrivateFolders.File);
        await Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(tokens)!)).IsEqualTo(PrivateFolders.Folder);
    }
}
