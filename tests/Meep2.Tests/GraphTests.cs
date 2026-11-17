using System.Text.Json;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>A pipeline as a graph, over the daemon's API, and the roles verbs play (for Synesthesia).</summary>
public class GraphTests
{
    private static readonly string s_examples = Path.Combine(RepositoryRoot(), "examples");

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Meep2.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Couldn't find the repository from " + AppContext.BaseDirectory);
    }

    // The graph of a pipeline file, loaded but not run.
    private static async Task<PipelineGraph> GraphOf(string path)
    {
        var syntax = MeeplangParser.Parse(new SourceFile(path, await File.ReadAllTextAsync(path)));
        var (live, diagnostics) = LivePipeline.Load(syntax, new ModuleRegistry().AddAssembly(CoreModules.Assembly));
        if (live is null)
            throw new InvalidOperationException(string.Join("\n", diagnostics));
        await using var pipeline = live.Pipeline;
        return Graphs.Describe(Path.GetFileNameWithoutExtension(path), path, live, version: 1);
    }

    private static GraphSpan Span(string file, int startLine, int startColumn, int endLine, int endColumn) => new(file, startLine, startColumn, endLine, endColumn);

    private static string Links(PipelineGraph graph) =>
        string.Join("; ", graph.Links.Select(l => $"{l.Key} {l.Input} {l.Port} {string.Join(",", l.Types)}{(l.Undeclared ? " +undeclared" : "")}"));

    [Test]
    public async Task A_plus_chain_feeds_one_module_through_numbered_inputs()
    {
        var file = Path.Combine(s_examples, "merge.meep");

        var graph = await GraphOf(file);

        await Assert.That(graph.Modules.Select(m => $"{m.Key} {m.Module} {m.Role} {m.Feeder}")).IsEquivalentTo(
            ["m1 Interval #1 source True", "m2 Interval #2 source True", "m3 WriteLine #3 outlet False"]);
        await Assert.That(Links(graph)).IsEqualTo("m1>m3 1 main ErrorMessage,Tick; m2>m3 2 main ErrorMessage,Tick");
        var first = graph.Modules[0];
        await Assert.That(first.Span).IsEqualTo(Span(file, 2, 1, 2, 40));
        await Assert.That(first.Parameters).IsEquivalentTo(
        [
            new GraphParameter(null, "300ms", Span(file, 2, 12, 2, 17)),
            new GraphParameter("count", "4", Span(file, 2, 18, 2, 25)),
            new GraphParameter("payload", "\"fast\"", Span(file, 2, 26, 2, 40)),
        ]);
        await Assert.That(first.Inflections).IsEquivalentTo(["Interval"]);
        await Assert.That(graph.Modules[2].Span).IsEqualTo(Span(file, 4, 1, 4, 43));
        await Assert.That(graph.Modules[2].Parameters.Single().Text).IsEqualTo("\"{msg.Payload} tick {msg.Count}\"");
        await Assert.That(graph.Modules.All(m => m.Under is null && m.Templates is null && m.Include is null)).IsTrue();
    }

    [Test]
    public async Task An_errors_branch_is_a_link_on_the_errors_port()
    {
        var file = Path.Combine(s_examples, "watchdog.meep");

        var graph = await GraphOf(file);

        await Assert.That(graph.Modules.Select(m => $"{m.Key} {m.Module} {m.Role} under {m.Under}")).IsEquivalentTo(
            ["m1 Interval #1 source under ", "m2 Check step under m1", "m3 WriteLine #3 outlet under m2", "m4 Tap #4 tap under ", "m5 WriteLine #5 outlet under m4"]);
        var fetch = graph.Modules[1];
        await Assert.That(fetch.Name).IsEqualTo("Check");
        await Assert.That(fetch.Inflections).IsEquivalentTo(["HTTP"]); // the address is https, so it can't be File
        await Assert.That(fetch.Span).IsEqualTo(Span(file, 10, 5, 11, 22)); // its parameter line, not the WriteLine under it
        await Assert.That(fetch.Parameters).IsEquivalentTo(
        [
            new GraphParameter(null, "Site", Span(file, 10, 11, 10, 15)),
            new GraphParameter("timeout", "10s", Span(file, 11, 9, 11, 22)),
        ]);
        var tap = graph.Modules[3];
        await Assert.That(tap.Modifiers).IsEquivalentTo([new GraphModifier("errors", null, Span(file, 14, 11, 14, 18))]);
        await Assert.That(tap.Parameters.Single().Text).IsEqualTo("Check");
        var errors = graph.Links.Single(l => l.To == "m4");
        await Assert.That($"{errors.Key} {errors.Input} {errors.Port} {string.Join(",", errors.Types)} {errors.Undeclared}").IsEqualTo("m2>m4 1 errors ErrorMessage False");
        await Assert.That(graph.Links.Where(l => l.Port == "main").Select(l => l.Key)).IsEquivalentTo(["m1>m2", "m2>m3", "m4>m5"]);
        await Assert.That(graph.Links.Single(l => l.Key == "m2>m3").Types).IsEquivalentTo(["ErrorMessage", "HttpResponse"]);
    }

    [Test]
    public async Task Chains_of_plus_lines_end_at_the_modules_under_them()
    {
        var file = Path.Combine(s_examples, "classify.meep");

        var graph = await GraphOf(file);

        await Assert.That(graph.Modules.Select(m => $"{m.Key} {m.Verb} {m.Role}")).IsEquivalentTo(
            ["m1 Read source", "m2 Learn step", "m3 Read source", "m4 Learn step", "m5 Batch step", "m6 Classify step", "m7 WriteLine outlet"]);
        await Assert.That(Links(graph)).IsEqualTo(
            "m1>m2 1 main ErrorMessage,FileText; m3>m4 1 main ErrorMessage,FileText; m2>m5 1 main ErrorMessage,FileText; m4>m5 2 main ErrorMessage,FileText; " +
            "m5>m6 1 main Batched,ErrorMessage; m6>m7 1 main ClassifiedMessage,ErrorMessage");
        await Assert.That(graph.Modules[1].Span).IsEqualTo(Span(file, 8, 3, 8, 46));
        await Assert.That(graph.Modules[1].Under).IsEqualTo("m1");
    }

    [Test]
    public async Task A_module_from_a_template_in_an_included_file_says_where_it_came_from()
    {
        using var folder = new TemporaryFolder();
        var shared = folder.Write("shared.meep", """
            template Shout(Text, Times = 1)
                Where msg.Count <= Times
                    WriteLine Text
            """);
        var file = folder.Write("loud.meep", """
            #include "shared.meep"

            template Loudly(Words)
                Shout Words

            Interval 10ms count=3
                Loudly "{msg.Count}!"
            """);

        var graph = await GraphOf(file);

        await Assert.That(graph.Modules.Select(m => $"{m.Key} {m.Module} {m.Role} under {m.Under}")).IsEquivalentTo(
            ["m1 Interval #1 source under ", "m2 Where #2 filter under m1", "m3 WriteLine #3 outlet under m2"]);
        var where = graph.Modules[1];
        await Assert.That(where.Span).IsEqualTo(Span(shared, 2, 5, 2, 29));
        await Assert.That(where.Parameters.Single()).IsEqualTo(new GraphParameter(null, "msg.Count <= Times", Span(shared, 2, 11, 2, 29)) { Value = "msg.Count <= 1" });
        await Assert.That(where.Templates!).IsEquivalentTo(
        [
            new GraphTemplateUse("Shout", Span(file, 4, 5, 4, 16)),
            new GraphTemplateUse("Loudly", Span(file, 7, 5, 7, 26)),
        ]);
        await Assert.That(where.Include).IsEqualTo(new GraphInclude("shared.meep", Span(file, 1, 1, 1, 23)));
        var writeLine = graph.Modules[2];
        await Assert.That(writeLine.Span).IsEqualTo(Span(shared, 3, 9, 3, 23));
        await Assert.That(writeLine.Parameters.Single().Text).IsEqualTo("Text");
        await Assert.That(writeLine.Parameters.Single().Value).Contains("msg.Count");
        await Assert.That(graph.Modules[0].Templates).IsNull();
    }

    [Test]
    public async Task Keys_last_through_a_restart_and_into_the_modules_a_template_makes()
    {
        using var folder = new TemporaryFolder();
        var file = folder.Write("loud.meep", """
            template Shout(Text)
                WriteLine Text

            Interval 10ms count=3
                Shout "a"
            """);
        var syntax = MeeplangParser.Parse(new SourceFile(file, await File.ReadAllTextAsync(file)));
        var (live, _) = LivePipeline.Load(syntax, new ModuleRegistry().AddAssembly(CoreModules.Assembly));
        var before = live!.Shape();

        await using (live.Pipeline)
            await using (live.Rebuild()) // as a restart does, from the same text
            {
            }
        var after = live.Shape();

        await Assert.That(before.Modules.Select(m => m.Key)).IsEquivalentTo(["m1", "m2"]);
        await Assert.That(after.Modules.Select(m => m.Key)).IsEquivalentTo(["m1", "m2"]);
        await Assert.That(ReferenceEquals(before.Modules[1].Syntax, after.Modules[1].Syntax)).IsFalse(); // the template was copied in again
    }

    [Test]
    public async Task The_daemon_serves_the_graph_and_says_when_it_changes()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("chain.meep", """
            Interval 20ms
                WriteLine "{msg}"
            """);
        await lab.Client.StartAsync(file);
        await using var socket = await lab.Client.OpenSocketAsync();
        var frames = new List<JsonElement>();
        var reading = Task.Run(async () =>
        {
            while (await socket.ReceiveAsync() is { } frame)
            {
                lock (frames)
                    frames.Add(frame);
            }
        });
        async Task<JsonElement> Next(Func<JsonElement, bool> wanted)
        {
            JsonElement found = default;
            await Wait.Until(() =>
            {
                lock (frames)
                {
                    var match = frames.FindIndex(f => wanted(f));
                    if (match < 0)
                        return false;
                    found = frames[match];
                    frames.RemoveAt(match);
                    return true;
                }
            });
            return found;
        }
        static string? Id(JsonElement f) => f.TryGetProperty("id", out var id) ? id.GetString() : null;
        static string? Event(JsonElement f) => f.TryGetProperty("event", out var e) ? e.GetString() : null;

        var first = await lab.Client.GraphAsync("chain");
        await socket.SendAsync("stats", "GET", "/pipelines/chain/stats?every=1h");
        var stats = await Next(f => Id(f) == "stats" && Event(f) == "stats");

        // Reloading with a Where inserted: the modules that were there keep their keys, though WriteLine's number changes.
        await File.WriteAllTextAsync(file, """
            Interval 20ms
                Where msg.Count >= 0
                    WriteLine "{msg}"
            """);
        await lab.Client.ReloadAsync("chain");
        var reloaded = await Next(f => Id(f) == "stats" && Event(f) == "graph");
        await socket.SendAsync("graph", "GET", "/pipelines/chain/graph");
        var overSocket = await Next(f => Id(f) == "graph");
        var second = await lab.Client.GraphAsync("chain");

        // Reloading the same text again changes nothing, so there's no notice and no new version.
        await lab.Client.ReloadAsync("chain");
        var unchanged = await lab.Client.GraphAsync("chain");

        // An edit through the API.
        await lab.Client.EditAsync("chain", new EditRequest([new EditItem("Where", "set", null, "msg.Count >= 1")]));
        var edited = await Next(f => Id(f) == "stats" && Event(f) == "graph");
        var third = await lab.Client.GraphAsync("chain");
        var detail = await lab.Client.GetAsync("chain");
        await socket.CancelAsync("stats");

        await Assert.That(first.Version).IsEqualTo(1);
        await Assert.That(first.Path).IsEqualTo(file);
        await Assert.That(first.Modules.Select(m => $"{m.Key} {m.Module}")).IsEquivalentTo(["m1 Interval #1", "m2 WriteLine #2"]);
        await Assert.That(first.Links.Single().Key).IsEqualTo("m1>m2");
        await Assert.That(stats.GetProperty("data").GetProperty("graphVersion").GetInt32()).IsEqualTo(1);

        await Assert.That(reloaded.GetProperty("data").GetProperty("version").GetInt32()).IsEqualTo(2);
        await Assert.That(reloaded.GetProperty("data").GetProperty("reason").GetString()).IsEqualTo("reload");
        await Assert.That(second.Version).IsEqualTo(2);
        await Assert.That(second.Modules.Select(m => $"{m.Key} {m.Module} {m.Role}")).IsEquivalentTo(["m1 Interval #1 source", "m3 Where #2 filter", "m2 WriteLine #3 outlet"]);
        await Assert.That(second.Links.Select(l => l.Key)).IsEquivalentTo(["m1>m3", "m3>m2"]);
        await Assert.That(second.Modules[2].Span).IsEqualTo(new GraphSpan(file, 3, 9, 3, 26));
        await Assert.That(overSocket.GetProperty("status").GetInt32()).IsEqualTo(200);
        var body = overSocket.GetProperty("body");
        await Assert.That(body.GetProperty("version").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("modules")[1].GetProperty("key").GetString()).IsEqualTo("m3");
        await Assert.That(body.GetProperty("modules")[1].GetProperty("role").GetString()).IsEqualTo("filter");
        await Assert.That(body.GetProperty("modules")[1].GetProperty("span").GetProperty("startLine").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("links")[0].GetProperty("port").GetString()).IsEqualTo("main");
        await Assert.That(body.GetProperty("modules")[0].TryGetProperty("templates", out _)).IsFalse(); // nulls are left out

        await Assert.That(unchanged.Version).IsEqualTo(2);
        await Assert.That(edited.GetProperty("data").GetProperty("version").GetInt32()).IsEqualTo(3);
        await Assert.That(edited.GetProperty("data").GetProperty("reason").GetString()).IsEqualTo("edit");
        await Assert.That(third.Modules[1].Parameters.Single().Text).IsEqualTo("msg.Count >= 1");
        await Assert.That(third.Modules.Select(m => m.Key)).IsEquivalentTo(["m1", "m3", "m2"]);
        await Assert.That(detail.GraphVersion).IsEqualTo(3);
    }

    [Test]
    public async Task A_pipeline_that_isnt_there_has_no_graph()
    {
        await using var lab = await DaemonLab.StartAsync();

        var refused = await Assert.That(async () => { await lab.Client.GraphAsync("nothing"); }).Throws<DaemonException>();

        await Assert.That(refused!.Status).IsEqualTo(404);
    }

    [Test]
    public async Task Each_verb_that_comes_with_Meep_has_a_role()
    {
        var registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);

        var roles = registry.Verbs.Order(StringComparer.Ordinal).ToDictionary(v => v, v => { registry.TryGetVerb(v, out var i); return i![0].Role; });

        await Assert.That(roles).IsEquivalentTo(new Dictionary<string, VerbRole>
        {
            ["Batch"] = VerbRole.Step,
            ["Classify"] = VerbRole.Step,
            ["Distribute"] = VerbRole.Router,
            ["Enumerate"] = VerbRole.Step,
            ["Extract"] = VerbRole.Step,
            ["Fetch"] = VerbRole.Step,
            ["Forget"] = VerbRole.Step,
            ["Interval"] = VerbRole.Source,
            ["Learn"] = VerbRole.Step,
            ["Listen"] = VerbRole.Inlet,
            ["Read"] = VerbRole.Source,
            ["Save"] = VerbRole.Outlet,
            ["Send"] = VerbRole.Outlet,
            ["Tap"] = VerbRole.Tap,
            ["Where"] = VerbRole.Filter,
            ["WriteLine"] = VerbRole.Outlet,
            ["Zip"] = VerbRole.Step,
        });
        registry.TryGetVerb("Where", out var where);
        registry.TryGetVerb("Interval", out var interval);
        await Assert.That(where![0].RoleDeclared).IsTrue();
        await Assert.That(interval![0].RoleDeclared).IsFalse();
    }

    [Test]
    public async Task A_link_into_a_module_that_takes_no_errors_says_so()
    {
        using var folder = new TemporaryFolder();
        var file = folder.Write("quiet.meep", """
            Interval 10ms count=1
                WriteLine "{msg.Count}" @no-errors
                WriteLine "{msg.Count}"
            """);

        var graph = await GraphOf(file);

        await Assert.That(Links(graph)).IsEqualTo("m1>m2 1 no-errors Tick; m1>m3 1 main ErrorMessage,Tick");
    }

    [Test]
    public async Task A_plugin_declares_its_own_roles_and_they_have_to_fit()
    {
        var registry = new ModuleRegistry().Add(typeof(Gate)).Add(typeof(Beacon)).Add(typeof(Counter)).Add(typeof(Shout)).Add(typeof(MixedA));

        var notASource = Assert.Throws<ArgumentException>(() => new ModuleRegistry().Add(typeof(FalseInlet)));
        var notPassingOn = Assert.Throws<ArgumentException>(() => new ModuleRegistry().Add(typeof(Sieve)));
        var notRouting = Assert.Throws<ArgumentException>(() => new ModuleRegistry().Add(typeof(FalseRouter)));
        var disagreeing = Assert.Throws<ArgumentException>(() => registry.Add(typeof(MixedB)));

        registry.TryGetVerb("Gate", out var gate);
        registry.TryGetVerb("Beacon", out var beacon);
        await Assert.That(gate![0].Role).IsEqualTo(VerbRole.Filter);
        await Assert.That(beacon![0].Role).IsEqualTo(VerbRole.Inlet);
        registry.TryGetVerb("Counter", out var counter);
        registry.TryGetVerb("Shout", out var shout);
        await Assert.That(counter![0].Role).IsEqualTo(VerbRole.Step); // a passthrough isn't an outlet unless it says so
        await Assert.That(shout![0].Role).IsEqualTo(VerbRole.Outlet);
        await Assert.That(notRouting.Message).Contains("only a module that implements IRouteMessages");
        await Assert.That(notASource.Message).Contains("which is for sources, but it handles messages");
        await Assert.That(notPassingOn.Message).Contains("so it needs [EmitsInput] too");
        await Assert.That(disagreeing.Message).IsEqualTo("Mixed's inflections have to agree on their role, but A is a Filter and B is a Step. Give them the same [Role].");
    }

    [Verb("Gate")]
    [Role(VerbRole.Filter)]
    [EmitsInput]
    private sealed class Gate : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("Beacon")]
    [Role(VerbRole.Inlet)]
    private sealed class Beacon : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Verb("Counter")]
    private sealed class Counter : APassthrough<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("Shout")]
    [Role(VerbRole.Outlet)]
    private sealed class Shout : APassthrough<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("FalseRouter")]
    [Role(VerbRole.Router)]
    private sealed class FalseRouter : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("FalseInlet")]
    [Role(VerbRole.Inlet)]
    private sealed class FalseInlet : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("Sieve")]
    [Role(VerbRole.Filter)]
    private sealed class Sieve : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("Mixed", Inflection = "A")]
    [Role(VerbRole.Filter)]
    [EmitsInput]
    private sealed class MixedA : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Verb("Mixed", Inflection = "B")]
    [EmitsInput]
    private sealed class MixedB : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Test]
    public async Task The_daemons_edits_can_name_modules_by_key()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("chain.meep", "Interval 20ms\n    WriteLine \"{msg}\"\n");
        await lab.Client.StartAsync(file);

        // After a reload with a Where inserted, the daemon's keys aren't the order of the file: WriteLine is m2.
        await File.WriteAllTextAsync(file, "Interval 20ms\n    Where msg.Count >= 0\n        WriteLine \"{msg}\"\n");
        await lab.Client.ReloadAsync("chain");

        // m2 is WriteLine, and still is after the insert renumbers it; +0 is the Where the first edit inserted.
        var result = await lab.Client.EditAsync("chain", new EditRequest([
            new EditItem("m3", "insert", null, "Where msg.Count < 100"),
            new EditItem("m2", "set", null, "\"{msg.Count}\""),
            new EditItem("+0", "set", null, "msg.Count < 50"),
            new EditItem("m3", "set-modifier", "buffer", "5")], Save: false));
        var graph = await lab.Client.GraphAsync("chain");
        var refused = await Assert.That(async () => { await lab.Client.EditAsync("chain", new EditRequest([new EditItem("m9", "remove")], Save: false)); }).Throws<DaemonException>();

        await Assert.That(result.Text).IsEqualTo("""
            Interval 20ms
                Where msg.Count >= 0 @buffer=5
                    Where msg.Count < 50
                        WriteLine "{msg.Count}"

            """);
        await Assert.That(graph.Modules.Select(m => $"{m.Key} {m.Module}")).IsEquivalentTo(["m1 Interval #1", "m3 Where #2", "m4 Where #3", "m2 WriteLine #4"]);
        await Assert.That(refused!.Status).IsEqualTo(400);
        await Assert.That(refused.Message).StartsWith("The edit (remove m9) can't be made. There's no module with the key m9. The keys are m1 (Interval #1), m3 (Where #2), m4 (Where #3), m2 (WriteLine #4).");
    }
}
