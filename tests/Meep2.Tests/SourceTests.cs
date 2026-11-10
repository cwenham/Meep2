using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Meep2.Cli;
using Meep2.Daemon;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Loading pipelines from URLs, Git, standard input and the daemon's socket, signed or pinned (goal 7, step 9).</summary>
public class SourceTests
{
    private const string Ticker = """
        Interval 10ms count=2
            WriteLine "tick {msg.Count}"
        """;

    private static string Sign(SshKeyPair key, string text) =>
        key.Sign(Encoding.UTF8.GetBytes(text), PipelineSignature.Namespace).ToArmored();

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Serves files from a dictionary, by path; anything else is 404.
    private static TestServer Serve(Dictionary<string, string> files) => new(async context =>
    {
        if (files.TryGetValue(context.Request.Url!.AbsolutePath, out var body))
            await TestServer.Reply(context, 200, body, "text/plain");
        else
            await TestServer.Reply(context, 404, "", "text/plain");
    });

    private static async Task<(int Code, string Output, string Error)> Run(PluginOptions options, params string[] args)
    {
        var output = new SharedText();
        var error = new SharedText();
        var code = await Wait.For(MeepCommand.RunAsync(args, TextWriter.Synchronized(output), TextWriter.Synchronized(error), options, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }

    [Test]
    public async Task A_pipeline_from_a_url_runs_when_a_trusted_key_signed_it()
    {
        using var key = SshKeyPair.CreateEd25519("publisher");
        using var other = SshKeyPair.CreateEd25519("stranger");
        var main = "#include \"shared.meep\"\nInterval 10ms count=2\n    Say\n";
        var shared = "template Say\n    WriteLine \"tick {msg.Count}\"\n";
        await using var server = Serve(new()
        {
            ["/p/ticker.meep"] = main,
            ["/p/ticker.meep.sig"] = Sign(key, main),
            ["/p/shared.meep"] = shared,
            ["/p/shared.meep.sig"] = Sign(key, shared),
            ["/p/forged.meep"] = main,
            ["/p/forged.meep.sig"] = Sign(other, main),
            ["/p/unsigned.meep"] = Ticker,
            ["/p/loose.meep"] = "#include \"loose-shared.meep\"\nInterval 10ms count=1\n    Say\n",
            ["/p/loose-shared.meep"] = shared,
        });
        using var home = new TemporaryFolder();
        var options = new PluginOptions { Home = home.Path, Trusted = new TrustedKeys([key.Public]) };

        var signed = await Run(options, "run", $"{server.Url}/p/ticker.meep");
        var forged = await Run(options, "run", $"{server.Url}/p/forged.meep");
        var unsigned = await Run(options, "run", $"{server.Url}/p/unsigned.meep");
        var pinned = await Run(options, "run", $"{server.Url}/p/unsigned.meep#sha256={Sha(Ticker)}");
        var wrongPin = await Run(options, "run", $"{server.Url}/p/unsigned.meep#sha256={Sha("something else")}");

        await Assert.That(signed.Error).IsEqualTo("");
        await Assert.That(signed.Output).Contains("tick 2");
        await Assert.That(forged.Code).IsEqualTo(1);
        await Assert.That(forged.Error).Contains("which isn't a key you trust");
        await Assert.That(unsigned.Error).Contains("isn't signed: there's no unsigned.meep.sig beside it.");
        await Assert.That(pinned.Output).Contains("tick 2"); // a pin needs no signature
        await Assert.That(wrongPin.Error).Contains("not the");
        // An include needs a signature too, unless the line that includes it pins it.
        var loose = await Run(options, "run", $"{server.Url}/p/loose.meep#sha256={Sha("#include \"loose-shared.meep\"\nInterval 10ms count=1\n    Say\n")}");
        await Assert.That(loose.Error).Contains("Couldn't include loose-shared.meep");
    }

    [Test]
    public async Task A_pin_on_an_include_line_covers_the_file_it_includes()
    {
        var shared = "template Say\n    WriteLine \"tick {msg.Count}\"\n";
        var pinned = $"#include \"shared.meep\" sha256:{Sha(shared)}\nInterval 10ms count=1\n    Say\n";
        var wrong = $"#include \"shared.meep\" sha256:{Sha("something else")}\nInterval 10ms count=1\n    Say\n";
        await using var server = Serve(new() { ["/p/pinned.meep"] = pinned, ["/p/wrong.meep"] = wrong, ["/p/shared.meep"] = shared });
        using var home = new TemporaryFolder();
        var options = new PluginOptions { Home = home.Path, Trusted = TrustedKeys.None };

        var together = await Run(options, "run", $"{server.Url}/p/pinned.meep#sha256={Sha(pinned)}");
        var changed = await Run(options, "run", $"{server.Url}/p/wrong.meep#sha256={Sha(wrong)}");

        await Assert.That(together.Error).IsEqualTo(""); // one pin covers the pipeline and what it includes
        await Assert.That(together.Output).Contains("tick 1");
        await Assert.That(changed.Error).Contains($"Couldn't include shared.meep: it has the SHA-256 {Sha(shared)}, not the {Sha("something else")} the #include line pins, so it has changed.");
    }

    [Test]
    public async Task Plain_http_is_only_for_this_machine_where_developer_mode_needs_no_signature()
    {
        await using var server = Serve(new() { ["/p/ticker.meep"] = Ticker });
        using var home = new TemporaryFolder();
        var options = new PluginOptions { Home = home.Path, Trusted = TrustedKeys.None };

        var remote = await Run(options, "run", "http://example.com/ticker.meep");
        var local = await Run(options, "run", $"{server.Url}/p/ticker.meep");
        var developing = await Run(options with { DeveloperMode = true }, "run", $"{server.Url}/p/ticker.meep");

        await Assert.That(remote.Error).Contains("Meep fetches pipelines over HTTPS, so it won't fetch http://example.com/ticker.meep: http:// is only for this machine (localhost). Use https://.");
        await Assert.That(local.Error).Contains("As it's on this machine, --dev would run it unsigned.");
        await Assert.That(developing.Error).IsEqualTo("");
        await Assert.That(developing.Output).Contains("tick 2");
    }

    [Test]
    public async Task A_pipeline_from_git_runs_at_a_pinned_commit_or_signed_on_a_branch()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        using var key = SshKeyPair.CreateEd25519("publisher");
        using var repository = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(repository.Path, "pipes"));
        repository.Write(Path.Combine("pipes", "ticker.meep"), "#include \"../shared/say.meep\"\nInterval 10ms count=2\n    Say\n");
        Directory.CreateDirectory(Path.Combine(repository.Path, "shared"));
        repository.Write(Path.Combine("shared", "say.meep"), "template Say\n    WriteLine \"tick {msg.Count}\"\n");
        GitRepo.Init(repository.Path);
        var sha = GitRepo.Run(repository.Path, "rev-parse", "HEAD").Output;
        using var home = new TemporaryFolder();
        var options = new PluginOptions { Home = home.Path, Trusted = new TrustedKeys([key.Public]) };
        var address = $"git+file://{repository.Path}#pipes/ticker.meep";

        var pinned = await Run(options, "run", $"{address}@{sha}");
        var branch = await Run(options, "run", $"{address}@main");

        await Assert.That(pinned.Error).IsEqualTo("");
        await Assert.That(pinned.Output).Contains("tick 2");
        await Assert.That(branch.Code).IsEqualTo(1);
        await Assert.That(branch.Error).Contains("isn't signed");

        // A commit signed by a key you trust covers every file in it, with no .sig files (Q134).
        GitRepo.Run(repository.Path, "commit", "-q", "-S", "--allow-empty", "-m", "Signed");
        var committer = PublicKey.Parse("tester", File.ReadAllText(Path.Combine(repository.Path, ".git", "signing-key.pub")));
        var signedCommit = await Run(options with { Trusted = new TrustedKeys([key.Public, committer]) }, "run", $"{address}@main");
        var strangersCommit = await Run(options, "run", $"{address}@main");
        await Assert.That(signedCommit.Error).IsEqualTo("");
        await Assert.That(signedCommit.Output).Contains("tick 2");
        await Assert.That(strangersCommit.Error).Contains($"Its commit is signed with {committer.Fingerprint}, which isn't a key you trust.");

        // Signed files on a branch run, the include too.
        foreach (var file in new[] { Path.Combine("pipes", "ticker.meep"), Path.Combine("shared", "say.meep") })
            repository.Write(file + ".sig", Sign(key, File.ReadAllText(Path.Combine(repository.Path, file))));
        GitRepo.Run(repository.Path, "add", "-A");
        GitRepo.Run(repository.Path, "commit", "-q", "-m", "Sign");
        var signed = await Run(options, "run", $"{address}@main");
        await Assert.That(signed.Error).IsEqualTo("");
        await Assert.That(signed.Output).Contains("tick 2");
        await Assert.That(PipelineAddress.Parse("git@github.com:x/y.git") .Origin).IsEqualTo(PipelineOrigin.File);
        await Assert.That((await Run(options, "run", "git+ssh://github.com/x/y.git#a.meep")).Error).Contains("HTTPS only");
    }

    [Test]
    public async Task A_pipeline_can_come_from_standard_input()
    {
        using var home = new TemporaryFolder();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "meep"), ["run", "-"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["MEEP_HOME"] = home.Path;
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(Ticker);
        process.StandardInput.Close();
        var output = await Wait.For(process.StandardOutput.ReadToEndAsync());
        await Wait.For(process.WaitForExitAsync());

        await Assert.That(process.ExitCode).IsEqualTo(0);
        await Assert.That(output).Contains("tick 2");
    }

    [Test]
    public async Task The_daemon_starts_text_sent_over_its_socket_and_pipelines_from_urls()
    {
        await using var server = Serve(new() { ["/ticker.meep"] = "Interval 20ms\n    WriteLine \"remote {msg.Count}\"\n" });
        await using var lab = await DaemonLab.StartAsync();

        var text = await lab.Client.StartTextAsync("greeter", "Interval 20ms\n    WriteLine \"sent {msg.Count}\"\n");
        var remote = await lab.Client.StartAsync($"{server.Url}/ticker.meep#sha256={Sha("Interval 20ms\n    WriteLine \"remote {msg.Count}\"\n")}");
        await Wait.Until(() => lab.Output.ToString().Contains("sent 2", StringComparison.Ordinal) && lab.Output.ToString().Contains("remote 2", StringComparison.Ordinal));
        var reload = await Assert.That(async () => { await lab.Client.ReloadAsync("greeter"); }).Throws<DaemonException>();
        var urlReload = await lab.Client.ReloadAsync("ticker");
        var edit = await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("WriteLine", "set", null, "\"edited {msg.Count}\"")]));

        await Assert.That(text.Name).IsEqualTo("greeter");
        await Assert.That(remote.Name).IsEqualTo("ticker");
        await Assert.That(string.Join("\n", reload!.Details)).Contains("came from text sent to the daemon, so there's nothing to read it from again");
        await Assert.That(urlReload.Applied).IsTrue();
        await Assert.That(edit.NotSaved).Contains("not a file on this machine, so there's nowhere to save the edit");
    }

    [Test]
    public async Task Meep_sign_signs_a_pipeline_file_beside_it()
    {
        Skip.Unless(SshKeygen.Available, "ssh-keygen isn't on this machine.");
        using var folder = new TemporaryFolder();
        var key = await SshKeygen.NewKeyAsync(folder.Path, "ed25519");
        var file = folder.Write("ticker.meep", Ticker);
        var options = new PluginOptions { Home = folder.Path };

        var signed = await Run(options, "sign", file, "--key", key);
        var publicKey = PublicKey.Parse("me", File.ReadAllText(key + ".pub"));
        var signer = PipelineSignature.Verify(File.ReadAllBytes(file), File.ReadAllText(file + ".sig"), new TrustedKeys([publicKey]), file);

        await Assert.That(signed.Output).StartsWith($"Signed {file} as {file}.sig.");
        await Assert.That(signer.SameKeyAs(publicKey)).IsTrue();
    }

    [Test]
    public async Task Meep_pin_pins_includes_deepest_first_so_one_pin_covers_them_all()
    {
        using var folder = new TemporaryFolder();
        var deeper = folder.Write("deeper.meep", "const Word = \"tick\"\n");
        var shared = folder.Write("shared.meep", "#include \"deeper.meep\"\ntemplate Say\n    WriteLine \"{Word} {msg.Count}\"\n");
        var main = folder.Write("main.meep", "#include \"shared.meep\" // the template\nInterval 10ms count=1\n    Say\n");
        using var home = new TemporaryFolder();
        var options = new PluginOptions { Home = home.Path };
        var deeperSha = Sha(File.ReadAllText(deeper));

        var pin = await Run(options, "pin", main);
        var again = await Run(options, "pin", main);
        var run = await Run(options, "run", main);
        File.WriteAllText(deeper, "const Word = \"tock\"\n");
        var changed = await Run(options, "run", main);

        await Assert.That(File.ReadAllText(shared)).StartsWith($"#include \"deeper.meep\" sha256:{deeperSha}\n");
        await Assert.That(File.ReadAllText(main)).StartsWith($"#include \"shared.meep\" sha256:{Sha(File.ReadAllText(shared))} // the template\n");
        await Assert.That(pin.Output).IsEqualTo($"Pinned 2 includes of {main}. Its SHA-256 is {Sha(File.ReadAllText(main))}, so pin it with #sha256={Sha(File.ReadAllText(main))} on its URL.\n");
        await Assert.That(again.Output).StartsWith($"Pinned 0 includes of {main}.");
        await Assert.That(run.Output).Contains("tick 1");
        await Assert.That(changed.Error).Contains($"Couldn't include deeper.meep: it has the SHA-256 {Sha("const Word = \"tock\"\n")}, not the {deeperSha}");
    }
}
