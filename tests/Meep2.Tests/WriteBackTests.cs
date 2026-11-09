using Meep2.Cli;
using Meep2.Daemon;
using Meep2.Git;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Write-back: edits to running pipelines, committed to pending, and reloading from the file (goal 4, step 8).</summary>
public class WriteBackTests
{
    private const string Ticker = """
        // Ticks, written out.
        Interval 20ms
            WriteLine "tick {msg.Count}"
        """;

    [Test]
    public async Task A_commit_goes_to_pending_without_touching_the_checkout()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        using var folder = new TemporaryFolder();
        var file = folder.Write("ticker.meep", Ticker);
        GitRepo.Init(folder.Path);

        var first = PendingBranch.Commit(file, Ticker.Replace("20ms", "50ms", StringComparison.Ordinal), "Slower");
        var second = PendingBranch.Commit(file, Ticker.Replace("20ms", "1s", StringComparison.Ordinal), "Slower still");

        await Assert.That(first.Branch).IsEqualTo("pending");
        await Assert.That(first.Path).IsEqualTo("ticker.meep");
        await Assert.That(File.ReadAllText(file)).IsEqualTo(Ticker); // the working tree is as it was
        await Assert.That(GitRepo.Run(folder.Path, "rev-parse", "--abbrev-ref", "HEAD").Output).IsEqualTo("main");
        await Assert.That(GitRepo.Run(folder.Path, "status", "--porcelain").Output).IsEqualTo("");
        await Assert.That(GitRepo.Run(folder.Path, "rev-parse", "pending~1").Output).IsEqualTo(first.Sha);
        await Assert.That(GitRepo.Run(folder.Path, "rev-parse", "pending~2").Output).IsEqualTo(GitRepo.Run(folder.Path, "rev-parse", "main").Output);
        await Assert.That(GitRepo.Run(folder.Path, "log", "-1", "--format=%an %s", "pending").Output).IsEqualTo("Tester Slower still");
        await Assert.That(PendingBranch.Read(file)).Contains("Interval 1s");
        await Assert.That(second.Sha).IsNotEqualTo(first.Sha);
    }

    [Test]
    public async Task A_commit_says_why_it_isnt_signed()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        using var folder = new TemporaryFolder();
        var file = folder.Write("ticker.meep", Ticker);
        GitRepo.Init(folder.Path);
        GitRepo.Run(folder.Path, "config", "user.signingkey", Path.Combine(folder.Path, "no-such-key"));

        var commit = PendingBranch.Commit(file, Ticker + "\n", "Edit");

        await Assert.That(commit.Unsigned).IsEqualTo($"user.signingkey names {Path.Combine(folder.Path, "no-such-key")}, which isn't there");
        await Assert.That(GitRepo.Run(folder.Path, "log", "-1", "--format=%G?", "pending").Output).IsEqualTo("N");
    }

    [Test]
    public async Task A_commit_says_why_it_cant_be_made()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        using var outside = new TemporaryFolder();
        var loose = outside.Write("loose.meep", Ticker);
        using var folder = new TemporaryFolder();
        var file = folder.Write("ticker.meep", Ticker);
        GitRepo.Init(folder.Path);

        await Assert.That(() => PendingBranch.Commit(loose, "x", "Edit")).Throws<PendingBranchException>().WithMessageContaining("isn't in a Git repository");
        await Assert.That(() => PendingBranch.Commit(file, "x", "Edit", "main")).Throws<PendingBranchException>().WithMessageContaining("main is checked out");
    }

    [Test]
    public async Task An_edit_changes_the_running_pipeline_and_commits_its_text_to_pending()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        GitRepo.Init(lab.Home);
        await lab.Client.StartAsync(file);

        var result = await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("Interval", "set", "count", "3")]));
        var source = await lab.Client.SourceAsync("ticker");
        var finished = await lab.WaitFor("ticker", PipelineStates.Finished); // count=3 makes it finish

        await Assert.That(result.Applied).IsTrue();
        await Assert.That(result.Reconfigured).IsEquivalentTo(["Interval #1"]);
        await Assert.That(result.Saved!.Branch).IsEqualTo("pending");
        await Assert.That(result.Saved.Unsigned).IsNull(); // signed with the repository's SSH key (Q126)
        await Assert.That(GitRepo.Run(lab.Home, "log", "-1", "--format=%G?", "pending").Output).IsEqualTo("G");
        await Assert.That(result.Text).IsEqualTo(Ticker.Replace("Interval 20ms", "Interval 20ms count=3", StringComparison.Ordinal));
        await Assert.That(GitRepo.Run(lab.Home, "show", "pending:ticker.meep").Output).IsEqualTo(result.Text!.Trim());
        await Assert.That(GitRepo.Run(lab.Home, "log", "-1", "--format=%B", "pending").Output).Contains("- Set Interval's count to 3");
        await Assert.That(File.ReadAllText(file)).IsEqualTo(Ticker);
        await Assert.That(source.Edited).IsTrue();
        await Assert.That(source.Text).Contains("count=3");
        await Assert.That(finished.Reason).IsEqualTo("its sources finished");
    }

    [Test]
    public async Task An_edit_outside_a_repository_is_written_to_the_file()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        await lab.Client.StartAsync(file, developerMode: false, watch: true);

        var result = await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("WriteLine", "set", null, "\"tock {msg.Count}\"")]));
        await Wait.Until(() => lab.Output.ToString().Contains("tock ", StringComparison.Ordinal));
        var reload = await lab.Client.ReloadAsync("ticker");

        await Assert.That(result.Applied).IsTrue();
        await Assert.That(result.Saved).IsNull();
        await Assert.That(result.WrittenTo).IsEqualTo(file); // Q124
        await Assert.That(File.ReadAllText(file)).IsEqualTo(result.Text);
        await Assert.That(reload.Replaced.Count + reload.Added.Count + reload.Removed.Count).IsEqualTo(0); // the file has the edit
    }

    [Test]
    public async Task An_edit_that_wouldnt_load_changes_nothing()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));

        var refused = await Assert.That(async () => { await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("WriteLine", "insert", null, "Talley")])); }).Throws<DaemonException>();
        var missing = await Assert.That(async () => { await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("Fetch", "remove")])); }).Throws<DaemonException>();
        var source = await lab.Client.SourceAsync("ticker");

        await Assert.That(refused!.Message).IsEqualTo("After those edits ticker wouldn't load, so nothing changed.");
        await Assert.That(string.Join("\n", refused.Details)).Contains("There's no module called Talley.");
        await Assert.That(missing!.Message).StartsWith("There's no module called Fetch.");
        await Assert.That(source.Text).IsEqualTo(Ticker);
        await Assert.That(source.Edited).IsFalse();
    }

    [Test]
    public async Task Reloading_changes_the_running_pipeline_to_match_its_file()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        await lab.Client.StartAsync(file);
        await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("Interval", "rename", null, "Clock")], Save: false));

        File.WriteAllText(file, Ticker + "\n        WriteLine \"again {msg.Count}\"");
        var warned = await Assert.That(async () => { await lab.Client.ReloadAsync("ticker"); }).Throws<DaemonException>();
        var result = await lab.Client.ReloadAsync("ticker", force: true);
        var unchanged = await lab.Client.ReloadAsync("ticker");
        await Wait.Until(() => lab.Output.ToString().Contains("again ", StringComparison.Ordinal));
        var source = await lab.Client.SourceAsync("ticker");

        // Q120: it warns before undoing the edit, and goes ahead when told to.
        await Assert.That(warned!.Status).IsEqualTo(409);
        await Assert.That(warned.Message).IsEqualTo("Reloading ticker from its file would undo edits made through meep edit that aren't in the file: name Interval Clock. They weren't saved anywhere. To reload anyway: meep reload ticker --force");
        await Assert.That(result.Added).IsEquivalentTo(["WriteLine #3"]);
        await Assert.That(result.Removed).IsEmpty();
        await Assert.That(unchanged.Added.Count + unchanged.Removed.Count + unchanged.Replaced.Count + unchanged.Reconfigured.Count).IsEqualTo(0);
        await Assert.That(source.Edited).IsFalse(); // the file wins over the edit (Q32)
        await Assert.That(source.Text).DoesNotContain("Clock");
    }

    [Test]
    public async Task A_reload_goes_ahead_once_pending_is_merged()
    {
        Skip.Unless(GitRepo.Available, "git isn't on this machine.");
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        GitRepo.Init(lab.Home);
        await lab.Client.StartAsync(file);
        var edit = await lab.Client.EditAsync("ticker", new EditRequest([new EditItem("Interval", "set", null, "30ms")]));

        var before = await Assert.That(async () => { await lab.Client.ReloadAsync("ticker"); }).Throws<DaemonException>();
        GitRepo.Run(lab.Home, "merge", "-q", "--ff-only", "pending");
        var after = await lab.Client.ReloadAsync("ticker");

        await Assert.That(before!.Message).Contains($"They're committed as {edit.Saved!.Sha[..7]}; merge them into your branch to keep them.");
        await Assert.That(after.Applied).IsTrue();
        await Assert.That(after.Reconfigured.Count + after.Replaced.Count).IsEqualTo(0); // the file now has the edit
    }

    [Test]
    public async Task A_watched_pipeline_reloads_when_its_file_changes()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        await lab.Client.StartAsync(file, developerMode: false, watch: true);

        await WriteUntil(file, Ticker.Replace("tick {msg.Count}", "tock {msg.Count}", StringComparison.Ordinal), () => lab.Output.ToString().Contains("tock ", StringComparison.Ordinal));
        await WriteUntil(file, "Intervall 20ms\n", () => lab.Log.ToString().Contains("doesn't load, so it runs as it was", StringComparison.Ordinal));

        await Assert.That(lab.Log.ToString()).Contains("[ticker] reloaded: replaced WriteLine #2");
        await Assert.That((await lab.Client.ListAsync())[0].State).IsEqualTo(PipelineStates.Running);
    }

    [Test]
    public async Task Reloading_loads_a_rebuilt_plugin_afresh_and_replaces_its_modules()
    {
        await using var lab = await DaemonLab.StartAsync();
        using var plugins = new PluginLab();
        var folder = plugins.CopyPlugin("shout", key: null);
        var dll = Path.Combine(folder, "Meep2.TestPlugin.dll");
        var file = lab.Write("shout.meep", $"""
            #plugin "{dll}"
            Interval 20ms
                Shout "tick {"{"}msg.Count{"}"}"
                    WriteLine "{"{"}msg{"}"}"
            """);
        await lab.Client.StartAsync(file, developerMode: true);

        var same = await lab.Client.ReloadAsync("shout");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1)); // rebuilt, but still 1.0.0
        var rebuilt = await lab.Client.ReloadAsync("shout");
        foreach (var built in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "plugins", "build-1.0.1"), "Meep2.TestPlugin*"))
            File.Copy(built, Path.Combine(folder, Path.GetFileName(built)), overwrite: true); // rebuilt as 1.0.1
        var bumped = await lab.Client.ReloadAsync("shout");

        await Assert.That(same.Replaced).IsEmpty();
        await Assert.That(same.Unchanged).Contains("Shout #2");
        await Assert.That(rebuilt.Replaced).IsEmpty(); // Q127: a new build needs a new version
        await Assert.That(string.Join("\n", rebuilt.Diagnostics)).Contains("has been rebuilt since it was loaded, but its version is still 1.0.0");
        await Assert.That(bumped.Replaced).IsEquivalentTo(["Shout #2"]);
        await Assert.That(bumped.Unchanged).Contains("Interval #1");
    }

    [Test]
    public async Task An_edit_can_add_a_plugin_which_is_installed_before_the_change()
    {
        await using var lab = await DaemonLab.StartAsync();
        using var plugins = new PluginLab();
        var dll = Path.Combine(plugins.CopyPlugin("shout", key: null), "Meep2.TestPlugin.dll");
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker), developerMode: true);

        var result = await lab.Client.EditAsync("ticker", new EditRequest(
            [new EditItem(null, "add-plugin", null, $"\"{dll}\""), new EditItem("WriteLine", "add", null, "Shout \"x\"")],
            Save: false));
        await Wait.Until(() => lab.Output.ToString().Contains("tick ", StringComparison.Ordinal));

        await Assert.That(result.Applied).IsTrue();
        await Assert.That(result.Added).IsEquivalentTo(["Shout #3"]);
        await Assert.That(result.Text).StartsWith($"#plugin \"{dll}\"\n\n// Ticks, written out.");
    }

    [Test]
    public async Task Meep_run_watch_follows_the_file()
    {
        using var folder = new TemporaryFolder();
        var file = folder.Write("ticker.meep", Ticker);
        var output = new StringWriter();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();

        var run = MeepCommand.RunAsync(["run", "--watch", file], TextWriter.Synchronized(output), TextWriter.Synchronized(error), folder.Path, stop.Token);
        await Wait.Until(() => output.ToString().Contains("tick ", StringComparison.Ordinal));
        await WriteUntil(file, Ticker.Replace("tick {msg.Count}", "tock {msg.Count}", StringComparison.Ordinal), () => output.ToString().Contains("tock ", StringComparison.Ordinal));
        await stop.CancelAsync();
        var code = await Wait.For(run);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(error.ToString()).Contains("Watching ");
        await Assert.That(error.ToString()).Contains("[ticker] reloaded: replaced WriteLine #2");
    }

    [Test]
    public async Task The_meep_command_edits_reloads_and_shows_a_running_pipeline()
    {
        using var home = DaemonLab.ShortFolder();
        var options = new PluginOptions { Home = home.Path };
        var file = home.Write("ticker.meep", Ticker);
        using var stopDaemon = new CancellationTokenSource();
        var daemon = MeepCommand.RunAsync(["daemon", file], TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(new StringWriter()), options, stopDaemon.Token);
        await Wait.Until(async () => (await Cli(options, "list")).Output.Contains("running", StringComparison.Ordinal));

        var edit = await Cli(options, "edit", "ticker", "Interval", "set", "@expires=5s", "--no-save");
        var named = await Cli(options, "edit", "ticker", "WriteLine", "rename", "Printer", "--no-save");
        var source = await Cli(options, "source", "ticker");
        var refused = await Cli(options, "reload", "ticker");
        var reload = await Cli(options, "reload", "ticker", "--force");
        var nonsense = await Cli(options, "edit", "ticker", "Interval", "frobnicate");
        await Cli(options, "daemon", "stop");
        await Wait.For(daemon);

        await Assert.That(edit.Output).StartsWith("Edited ticker:");
        await Assert.That(named.Output).IsEqualTo($"Edited ticker: nothing changed.{Environment.NewLine}");
        await Assert.That(source.Output).Contains("Interval 20ms @expires=5s");
        await Assert.That(source.Output).Contains("WriteLine \"tick {msg.Count}\" as Printer");
        await Assert.That(refused.Code).IsEqualTo(1);
        await Assert.That(refused.Error).Contains("To reload anyway: meep reload ticker --force");
        await Assert.That(reload.Output).StartsWith("Reloaded ticker");
        await Assert.That(nonsense.Code).IsEqualTo(2);
        await Assert.That(nonsense.Error).Contains("meep edit <pipeline> <module> takes one edit");
    }

    // Writes a file, and again every second until the condition holds: on macOS, a watcher that has only just
    // started can miss a change.
    private static async Task WriteUntil(string file, string text, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (true)
        {
            File.WriteAllText(file, text);
            var wrote = DateTime.UtcNow;
            while (!condition() && DateTime.UtcNow - wrote < TimeSpan.FromSeconds(1))
                await Task.Delay(20);
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The change to the file was never picked up.");
        }
    }

    private static async Task<(int Code, string Output, string Error)> Cli(PluginOptions options, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Wait.For(MeepCommand.RunAsync(args, output, error, options, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }
}
