using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using Meep2.Cli;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Installing, checking, loading and unloading plugins (step 6).</summary>
public class PluginTests
{
    private const string ShoutTwice = """
        Interval 10ms count=2
            Shout "tick {msg.Count}"
                Where msg is Shouted
                    WriteLine "{msg.Text}"
        """;

    [Test]
    public async Task A_signed_plugin_on_this_machine_loads_and_its_modules_run()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(run.Warnings).IsEmpty();
    }

    [Test]
    public async Task A_plugin_can_be_named_by_its_folder()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("Meep2.TestPlugin", lab.Key);

        var run = await lab.RunAsync("#plugin 'Meep2.TestPlugin'\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
    }

    [Test]
    public async Task An_unsigned_plugin_on_this_machine_needs_developer_mode()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", key: null);
        var pipeline = "#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice;

        var refused = await lab.RunAsync(pipeline, new PluginCatalog());
        lab.Options = lab.Options with { DeveloperMode = true };
        var allowed = await lab.RunAsync(pipeline, new PluginCatalog());

        await Assert.That(refused.AllErrors).Contains("(1,1): error: shout/Meep2.TestPlugin.dll isn't signed: it has no meep-files.txt.sig. A plugin on this machine needs signing with meep sign, unless meep runs in developer mode (meep run --dev).");
        await Assert.That(refused.Output).IsEmpty();
        await Assert.That(allowed.AllErrors).IsEmpty();
        await Assert.That(allowed.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(allowed.Warnings).Contains("(1,1): warning: shout/Meep2.TestPlugin.dll isn't signed. It's loaded because meep is in developer mode.");
    }

    [Test]
    public async Task A_plugin_signed_with_a_key_nobody_trusts_is_refused()
    {
        using var lab = new PluginLab();
        using var stranger = SshKeyPair.CreateEd25519("stranger");
        lab.CopyPlugin("shout", stranger);

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains($"is signed with the key {stranger.Public.Fingerprint}, which isn't one you trust. If you trust its author, add their key with meep keys trust.");
    }

    [Test]
    public async Task A_key_trusted_only_for_other_namespaces_doesnt_sign_plugins()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);
        var allowedSigners = Path.Combine(lab.Path, "allowed_signers");
        await File.WriteAllTextAsync(allowedSigners, $"tester namespaces=\"git,file\" {lab.Key.Public.Text}\n");
        lab.Options = lab.Options with { Trusted = TrustedKeys.Load(allowedSigners) };

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("which isn't one you trust");
    }

    [Test]
    public async Task A_file_changed_after_signing_is_found()
    {
        using var lab = new PluginLab();
        var folder = lab.CopyPlugin("shout", lab.Key);
        await File.AppendAllTextAsync(Path.Combine(folder, "Meep2.TestPlugin.deps.json"), " ");

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("shout/Meep2.TestPlugin.dll's Meep2.TestPlugin.deps.json has changed since it was signed.");
    }

    [Test]
    public async Task A_file_added_after_signing_is_found()
    {
        using var lab = new PluginLab();
        var folder = lab.CopyPlugin("shout", lab.Key);
        await File.WriteAllTextAsync(Path.Combine(folder, "Extra.dll"), "not really");

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("shout/Meep2.TestPlugin.dll has Extra.dll, which its signature doesn't cover.");
    }

    [Test]
    public async Task A_changed_list_of_files_breaks_the_signature()
    {
        using var lab = new PluginLab();
        var folder = lab.CopyPlugin("shout", lab.Key);
        var list = Path.Combine(folder, PluginSignature.ListName);
        await File.WriteAllTextAsync(list, (await File.ReadAllTextAsync(list)).Replace("Meep2.TestPlugin.deps.json\n", "", StringComparison.Ordinal));

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("shout/Meep2.TestPlugin.dll's signature doesn't match its meep-files.txt, so it may have been tampered with.");
    }

    [Test]
    [Arguments("ed25519", null)]
    [Arguments("ecdsa", 256)]
    [Arguments("ecdsa", 384)]
    [Arguments("ecdsa", 521)]
    [Arguments("rsa", 3072)]
    public async Task A_plugin_signed_by_ssh_keygen_is_accepted(string type, int? bits)
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = new PluginLab();
        var folder = lab.CopyPlugin("shout", key: null);
        var key = await SshKeygen.NewKeyAsync(Path.Combine(lab.Path, "keys"), type, bits);
        var list = PluginSignature.WriteList(folder);
        var (code, said) = await SshKeygen.Run(["-Y", "sign", "-n", "meep-plugin", "-f", key, list]);
        lab.Options = lab.Options with { Trusted = new TrustedKeys([PublicKey.Parse("me", await File.ReadAllTextAsync(key + ".pub"))]) };

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(code).IsEqualTo(0).Because(said);
        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
    }

    [Test]
    [Arguments("ed25519")]
    [Arguments("ecdsa")]
    public async Task Ssh_keygen_accepts_meeps_signatures(string type)
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = new PluginLab();
        using var key = type == "ed25519" ? SshKeyPair.CreateEd25519("me") : SshKeyPair.CreateEcdsa("me");
        var folder = lab.CopyPlugin("shout", key);
        var allowedSigners = Path.Combine(lab.Path, "allowed_signers");
        await File.WriteAllTextAsync(allowedSigners, $"me namespaces=\"meep-plugin\" {key.Public.Text}\n");

        var (code, said) = await SshKeygen.Run(
            ["-Y", "verify", "-f", allowedSigners, "-I", "me", "-n", "meep-plugin", "-s", Path.Combine(folder, PluginSignature.FileName)],
            input: await File.ReadAllTextAsync(Path.Combine(folder, PluginSignature.ListName)));

        await Assert.That(code).IsEqualTo(0).Because(said);
        await Assert.That(said).Contains("Good \"meep-plugin\" signature for me");
    }

    [Test]
    public async Task A_github_plugin_is_downloaded_checked_and_cached()
    {
        using var lab = new PluginLab();
        var (zip, sha) = lab.ReleaseAsset();
        var requests = new ConcurrentQueue<string>();
        await using var github = new TestServer(context => Serve(context, requests, "/cwenham/Meep2.TestPlugin/releases/download/v1.0.0/Meep2.TestPlugin-1.0.0.zip", zip));
        lab.Options = lab.Options with { GitHub = new Uri(github.Url + "/") };
        var pipeline = $"#plugin github.com/cwenham/Meep2.TestPlugin 1.0.0 sha256:{sha}\n" + ShoutTwice;

        var first = await lab.RunAsync(pipeline, new PluginCatalog());
        var second = await lab.RunAsync(pipeline, new PluginCatalog());

        await Assert.That(first.AllErrors).IsEmpty();
        await Assert.That(first.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(second.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(requests.Count).IsEqualTo(1); // the second run used the cache
        await Assert.That(File.Exists(Path.Combine(lab.Options.Cache, "github", "cwenham", "meep2.testplugin", "1.0.0", "Meep2.TestPlugin.dll"))).IsTrue();
    }

    [Test]
    public async Task An_unsigned_github_plugin_is_trusted_by_the_sha_its_line_pins()
    {
        using var lab = new PluginLab();
        var (zip, sha) = lab.ReleaseAsset(signed: false);
        await using var github = new TestServer(context => Serve(context, new(), "/cwenham/Meep2.TestPlugin/releases/download/v1.0.0/Meep2.TestPlugin-1.0.0.zip", zip));
        lab.Options = lab.Options with { GitHub = new Uri(github.Url + "/"), Trusted = TrustedKeys.None };

        var run = await lab.RunAsync($"#plugin github.com/cwenham/Meep2.TestPlugin 1.0.0 sha256:{sha}\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Warnings).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
    }

    [Test]
    public async Task A_github_plugin_whose_cached_files_changed_is_downloaded_again()
    {
        using var lab = new PluginLab();
        var (zip, sha) = lab.ReleaseAsset(signed: false);
        var requests = new ConcurrentQueue<string>();
        await using var github = new TestServer(context => Serve(context, requests, "/cwenham/Meep2.TestPlugin/releases/download/v1.0.0/Meep2.TestPlugin-1.0.0.zip", zip));
        lab.Options = lab.Options with { GitHub = new Uri(github.Url + "/") };
        var pipeline = $"#plugin github.com/cwenham/Meep2.TestPlugin 1.0.0 sha256:{sha}\n" + ShoutTwice;

        await lab.RunAsync(pipeline, new PluginCatalog());
        var cached = Path.Combine(lab.Options.Cache, "github", "cwenham", "meep2.testplugin", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(cached, "Sneaky.dll"), "not really");
        var again = await lab.RunAsync(pipeline, new PluginCatalog());

        await Assert.That(again.AllErrors).IsEmpty();
        await Assert.That(again.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(File.Exists(Path.Combine(cached, "Sneaky.dll"))).IsFalse();
    }

    [Test]
    public async Task A_github_plugin_whose_sha_differs_is_refused()
    {
        using var lab = new PluginLab();
        var (zip, sha) = lab.ReleaseAsset();
        await using var github = new TestServer(context => Serve(context, new(), "/cwenham/Meep2.TestPlugin/releases/download/v1.0.0/Meep2.TestPlugin-1.0.0.zip", zip));
        lab.Options = lab.Options with { GitHub = new Uri(github.Url + "/") };
        var wrong = new string('0', 64);

        var run = await lab.RunAsync($"#plugin github.com/cwenham/Meep2.TestPlugin 1.0.0 sha256:{wrong}\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains($"has the SHA-256 {sha}, but the #plugin line says {wrong}. Either the release has changed, or the line has the wrong SHA.");
        await Assert.That(Directory.Exists(Path.Combine(lab.Options.Cache, "github", "cwenham", "meep2.testplugin", "1.0.0"))).IsFalse();
    }

    [Test]
    public async Task A_zip_entry_that_would_land_outside_its_folder_is_refused()
    {
        using var lab = new PluginLab();
        var path = Path.Combine(lab.Path, "evil.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escaped.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("out");
        }
        var zip = await File.ReadAllBytesAsync(path);
        await using var github = new TestServer(context => Serve(context, new(), "/cwenham/Evil/releases/download/v1.0.0/Evil-1.0.0.zip", zip));
        lab.Options = lab.Options with { GitHub = new Uri(github.Url + "/") };

        var run = await lab.RunAsync($"#plugin github.com/cwenham/Evil 1.0.0 sha256:{PluginSignature.Hash(path)}\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("Evil-1.0.0.zip has an entry that would land outside its folder: ../../escaped.txt");
    }

    [Test]
    public async Task A_nuget_plugin_comes_with_its_dependencies()
    {
        using var lab = new PluginLab();
        lab.Options = lab.Options with { NuGetSources = [lab.SignedFeed()] };

        var run = await lab.RunAsync("#plugin nuget:Meep2.TestPlugin 1.0.0\n" + ShoutTwice, new PluginCatalog());

        var installed = Path.Combine(lab.Options.Cache, "nuget", "meep2.testplugin", "1.0.0");
        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(File.Exists(Path.Combine(installed, "Meep2.TestPlugin.Words.dll"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(installed, "Meep2.SDK.dll"))).IsFalse(); // the host's own is used
    }

    [Test]
    public async Task A_nuget_plugin_without_a_signature_is_refused()
    {
        using var lab = new PluginLab();
        lab.Options = lab.Options with { NuGetSources = [PluginLab.Feed], DeveloperMode = true };

        var run = await lab.RunAsync("#plugin nuget:Meep2.TestPlugin 1.0.0\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("nuget:Meep2.TestPlugin 1.0.0 isn't signed: it has no meep-files.txt.sig. Plugins from NuGet must be signed by a key you trust.");
    }

    [Test]
    public async Task A_nuget_plugins_packages_go_in_the_lock_file_and_changes_are_refused()
    {
        using var lab = new PluginLab();
        lab.Options = lab.Options with { NuGetSources = [lab.SignedFeed()] };
        var pipeline = "#plugin nuget:Meep2.TestPlugin 1.0.0\n" + ShoutTwice;

        var first = await lab.RunAsync(pipeline, new PluginCatalog(), file: "shout.meep");
        var lockFile = Path.Combine(lab.Path, "shout.meep.lock");
        var written = await File.ReadAllTextAsync(lockFile);
        var words = PluginSignature.Hash(Path.Combine(lab.Options.Cache, "nuget", "meep2.testplugin", "1.0.0", "Meep2.TestPlugin.Words.dll"));
        var tampered = string.Join('\n', written.Split('\n').Select(line =>
            line.StartsWith("  Meep2.TestPlugin.Words ", StringComparison.Ordinal) ? "  Meep2.TestPlugin.Words 1.0.0 sha256:" + new string('0', 64) : line));
        await File.WriteAllTextAsync(lockFile, tampered);
        var second = await lab.RunAsync(pipeline, new PluginCatalog(), file: "shout.meep");

        await Assert.That(first.AllErrors).IsEmpty();
        await Assert.That(written).Contains("\nnuget:Meep2.TestPlugin 1.0.0\n  Meep2.TestPlugin 1.0.0 sha256:");
        await Assert.That(written).Contains("\n  Meep2.TestPlugin.Words 1.0.0 sha256:");
        await Assert.That(words).IsNotEmpty();
        await Assert.That(second.AllErrors).Contains(
            "nuget:Meep2.TestPlugin 1.0.0 installed packages that differ from shout.meep.lock: Meep2.TestPlugin.Words 1.0.0 sha256:000000000000… is now Meep2.TestPlugin.Words 1.0.0 sha256:");
        await Assert.That(second.AllErrors).Contains("If you expect that, delete nuget:Meep2.TestPlugin 1.0.0's lines from the lock file and run the pipeline again.");
        await Assert.That(second.Output).IsEmpty();
    }

    [Test]
    public async Task A_plugins_verbs_and_message_types_can_be_reached_by_its_name()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);

        var run = await lab.RunAsync("""
            #plugin "shout/Meep2.TestPlugin.dll"
            Interval 10ms count=1
                TestPlugin.Shout "short"
                    Where msg is TestPlugin.Shouted
                        WriteLine "{msg.Text}"
                TestPlugin.Shout.Shout "long"
                    WriteLine "{msg.Text}"
            """, new PluginCatalog());

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order()).IsEquivalentTo(["LONG!", "SHORT!"]);
    }

    [Test]
    public async Task A_plugin_line_can_name_its_plugin()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);

        var run = await lab.RunAsync("""
            #plugin "shout/Meep2.TestPlugin.dll" as Loud
            Interval 10ms count=1
                Loud.Shout "named"
                    Where msg is Loud.Shouted
                        WriteLine "{msg.Text}"
            """, new PluginCatalog());

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(run.Output).IsEqualTo("NAMED!\n");
    }

    [Test]
    public async Task Names_two_plugins_share_need_the_plugins_name()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("one", lab.Key);
        lab.CopyPlugin("two", lab.Key);
        const string Plugins = """
            #plugin "one/Meep2.TestPlugin.dll" as One
            #plugin "two/Meep2.TestPlugin.dll" as Two

            """;

        var bare = await lab.RunAsync(Plugins + ShoutTwice, new PluginCatalog());
        var bareType = await lab.RunAsync(Plugins + """
            Interval 10ms count=1
                One.Shout "one"
                    Where msg is Shouted
            """, new PluginCatalog());
        var named = await lab.RunAsync(Plugins + """
            Interval 10ms count=1
                One.Shout "one"
                    Where msg is One.Shouted
                        WriteLine "{msg.Text}"
                Two.Shout "two"
                    Where msg is Two.Shouted
                        WriteLine "{msg.Text}"
            """, new PluginCatalog());

        await Assert.That(bare.AllErrors).Contains("Shout is in both One and Two, so write One.Shout or Two.Shout.");
        await Assert.That(bareType.AllErrors).Contains("Shouted is in both One and Two, so write One.Shouted or Two.Shouted.");
        await Assert.That(named.AllErrors).IsEmpty();
        await Assert.That(named.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order()).IsEquivalentTo(["ONE!", "TWO!"]);
    }

    [Test]
    public async Task A_plugin_cant_take_a_verbs_name()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);

        var run = await lab.RunAsync("#plugin \"shout/Meep2.TestPlugin.dll\" as Fetch\n" + ShoutTwice, new PluginCatalog());

        await Assert.That(run.AllErrors).Contains("(1,1): error: shout/Meep2.TestPlugin.dll can't be called Fetch: Fetch is a verb's name, so it can't be a plugin's too.");
    }

    [Test]
    public async Task A_plugin_unloads_once_its_pipeline_is_finished()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);
        var catalog = new PluginCatalog();
        WeakReference? unloaded = null;

        var running = lab.RunAsync("""
            #plugin "shout/Meep2.TestPlugin.dll"
            Interval 50ms count=3
                Shout "tick {msg.Count}"
                    WriteLine "{msg.Text}"
            """, catalog);
        await Wait.Until(() => (unloaded = PluginLab.Loaded(catalog)) is not null || running.IsCompleted);
        var run = await running;

        await Assert.That(run.AllErrors).IsEmpty();
        await Assert.That(unloaded).IsNotNull();
        await Assert.That(catalog.Loaded.Count).IsEqualTo(0);
        for (var i = 0; i < 20 && unloaded!.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10);
        }
        await Assert.That(unloaded!.IsAlive).IsFalse();
    }

    [Test]
    public async Task Pipelines_using_one_plugin_share_it()
    {
        using var lab = new PluginLab();
        lab.CopyPlugin("shout", lab.Key);
        var catalog = new PluginCatalog();
        const string slow = """
            #plugin "shout/Meep2.TestPlugin.dll"
            Interval 100ms count=3
                Shout "tick {msg.Count}"
                    WriteLine "{msg.Text}"
            """;

        var first = lab.RunAsync(slow, catalog);
        var second = lab.RunAsync(slow, catalog);
        await Wait.Until(() => catalog.Loaded.Count > 0);
        var loadedWhileRunning = catalog.Loaded.Count;
        await Task.WhenAll(first, second);

        await Assert.That(loadedWhileRunning).IsEqualTo(1);
        await Assert.That(catalog.Loaded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_cli_signs_with_ssh_keygen_trusts_the_key_and_runs_the_plugin()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = new PluginLab();
        var home = Path.Combine(lab.Path, "cli-home");
        var folder = lab.CopyPlugin("shout", key: null);
        var key = await SshKeygen.NewKeyAsync(Path.Combine(lab.Path, "keys"), "ed25519");
        var file = Path.Combine(lab.Path, "shout.meep");
        await File.WriteAllTextAsync(file, "#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice);

        var unsigned = await Cli(home, "run", file);
        var signed = await Cli(home, "sign", folder, "--key", key);
        var untrusted = await Cli(home, "run", file);
        var trusted = await Cli(home, "keys", "trust", "me", key + ".pub");
        var listed = await Cli(home, "keys", "list");
        var ran = await Cli(home, "run", file);

        await Assert.That(unsigned.Code).IsEqualTo(1);
        await Assert.That(unsigned.Error).Contains("isn't signed");
        await Assert.That(signed.Code).IsEqualTo(0).Because(signed.Error);
        await Assert.That(signed.Output).StartsWith("Signed 5 files with the ssh-ed25519 key SHA256:");
        await Assert.That(signed.Output).Contains("You don't trust this key yet");
        await Assert.That(untrusted.Error).Contains("which isn't one you trust");
        await Assert.That(trusted.Output).StartsWith("Plugins signed by me's ssh-ed25519 key SHA256:");
        await Assert.That(File.ReadAllText(Path.Combine(home, "allowed_signers"))).StartsWith("me ssh-ed25519 AAAA");
        await Assert.That(listed.Output).StartsWith("me  SHA256:");
        await Assert.That(ran.Error).IsEmpty();
        await Assert.That(ran.Output.ReplaceLineEndings("\n")).IsEqualTo("TICK 1!\nTICK 2!\n");
    }

    [Test]
    public async Task The_cli_trusts_someone_elses_key()
    {
        using var lab = new PluginLab();
        var home = Path.Combine(lab.Path, "cli-home");
        using var friend = SshKeyPair.CreateEd25519("friend");

        var trusted = await Cli(home, ["keys", "trust", .. friend.Public.ToString().Split(' ')]);
        var again = await Cli(home, ["keys", "trust", "pal", .. friend.Public.Text.Split(' ')]);
        var nonsense = await Cli(home, "keys", "trust", "friend", "ssh-ed25519", "abc");
        var privateKey = Path.Combine(lab.Path, "id_ed25519");
        await File.WriteAllTextAsync(privateKey, "-----BEGIN OPENSSH PRIVATE KEY-----\n…\n");
        var wrongHalf = await Cli(home, "keys", "trust", "friend", privateKey);

        await Assert.That(trusted.Output).IsEqualTo($"Plugins signed by friend's ssh-ed25519 key {friend.Public.Fingerprint} can load now.{Environment.NewLine}");
        await Assert.That(again.Output).StartsWith($"You trust {friend.Public.Fingerprint} already, as friend.");
        await Assert.That(nonsense.Code).IsEqualTo(2);
        await Assert.That(wrongHalf.Error).Contains("is a private key. Trust the public one beside it");
        await Assert.That(TrustedKeys.Load(Path.Combine(home, "allowed_signers")).Keys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_cli_trusts_the_keys_on_a_github_account()
    {
        using var lab = new PluginLab();
        using var friend = SshKeyPair.CreateEd25519("friend");
        var securityKey = new SshWriter();
        securityKey.WriteText("sk-ssh-ed25519@openssh.com");
        securityKey.WriteBytes(new byte[32]);
        securityKey.WriteText("ssh:");
        var keys = $"{friend.Public.Text}\nsk-ssh-ed25519@openssh.com {Convert.ToBase64String(securityKey.ToArray())}\n";
        await using var github = new TestServer(context => context.Request.Url!.AbsolutePath == "/octocat.keys"
            ? TestServer.Reply(context, 200, keys, "text/plain")
            : TestServer.Reply(context, 404, "Not Found", "text/plain"));
        var options = new PluginOptions { Home = Path.Combine(lab.Path, "cli-home"), GitHub = new Uri(github.Url + "/") };

        var trusted = await Cli(options, "keys", "trust", "github:octocat");
        var nobody = await Cli(options, "keys", "trust", "github:nobody");

        await Assert.That(trusted.Code).IsEqualTo(0);
        await Assert.That(trusted.Output).Contains("Skipped one of octocat's keys: github:octocat's key can't be used: it's a security key (sk-ssh-ed25519@openssh.com), which can't sign plugins yet.");
        await Assert.That(trusted.Output).Contains($"Plugins signed by github:octocat's ssh-ed25519 key {friend.Public.Fingerprint} can load now.");
        await Assert.That(nobody.Error).Contains("GitHub has no user called nobody.");
        await Assert.That(TrustedKeys.Load(options.TrustedKeysFile).Keys.Single().Name).IsEqualTo("github:octocat");
    }

    [Test]
    public async Task The_cli_runs_unsigned_plugins_in_developer_mode()
    {
        using var lab = new PluginLab();
        var home = Path.Combine(lab.Path, "cli-home");
        lab.CopyPlugin("shout", key: null);
        var file = Path.Combine(lab.Path, "shout.meep");
        await File.WriteAllTextAsync(file, "#plugin \"shout/Meep2.TestPlugin.dll\"\n" + ShoutTwice);

        var ran = await Cli(home, "run", "--dev", file);

        await Assert.That(ran.Code).IsEqualTo(0);
        await Assert.That(ran.Output.ReplaceLineEndings("\n")).IsEqualTo("TICK 1!\nTICK 2!\n");
        await Assert.That(ran.Error).Contains("warning: shout/Meep2.TestPlugin.dll isn't signed. It's loaded because meep is in developer mode.");
    }

    private static Task<(int Code, string Output, string Error)> Cli(string home, params string[] args) =>
        Cli(new PluginOptions { Home = home }, args);

    private static async Task<(int Code, string Output, string Error)> Cli(PluginOptions options, params string[] args)
    {
        var output = new SharedText();
        var error = new SharedText();
        var code = await Wait.For(MeepCommand.RunAsync(args, output, error, options, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }

    private static async Task Serve(HttpListenerContext context, ConcurrentQueue<string> requests, string path, byte[] body)
    {
        requests.Enqueue(context.Request.Url!.AbsolutePath);
        if (context.Request.Url.AbsolutePath != path)
        {
            await TestServer.Reply(context, 404, "Not Found", "text/plain");
            return;
        }
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }
}
