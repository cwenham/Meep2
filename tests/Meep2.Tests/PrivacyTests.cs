using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.SDK.Modules;
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

        File.SetUnixFileMode(signers, PrivateFolders.File | UnixFileMode.GroupWrite);
        var refused = await Assert.That(() => TrustedKeys.Load(signers)).Throws<PluginException>();

        await Assert.That(refused!.Message).StartsWith($"{signers} can be changed by other users, so Meep doesn't trust the keys in it.");
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
