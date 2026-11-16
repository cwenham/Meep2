using System.Text.Json;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;
using Numbers = Meep2.Tests.Support.Numbers;

namespace Meep2.Tests;

/// <summary>
/// How many messages, and how many errors, each link of a pipeline carries, keyed as the graph's links are (for
/// Synesthesia's wires), and how many each module passed on.
/// </summary>
public class LinkCountTests
{
    private const string NeverStop = """
        defaults
            on-error = log, stop-pipeline never

        """;

    private static LivePipeline Load(string text)
    {
        var (live, diagnostics) = LivePipeline.Load(Script.Parse(text), new ModuleRegistry().AddAssembly(CoreModules.Assembly));
        return live ?? throw new InvalidOperationException(string.Join("\n", diagnostics));
    }

    // Runs a pipeline until its sources finish and its messages have gone, then gives its links' counts by key.
    private static async Task<(Dictionary<string, LinkDetail> Links, PipelineGraph Graph, Pipeline Pipeline)> Run(string text)
    {
        var live = Load(text);
        await using var pipeline = live.Pipeline;
        await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var links = Graphs.LinkCounts(live, pipeline).ToDictionary(l => l.Key);
        return (links, Graphs.Describe("test", "test.meep", live, version: 1), pipeline);
    }

    // "m1>m2 6" for a link that carried 6 messages, "m1>m2 6 (3 errors)" if some were errors.
    private static string Counts(Dictionary<string, LinkDetail> links) =>
        string.Join("; ", links.Values.Select(l => l.Errors == 0 ? $"{l.Key} {l.Carried}" : $"{l.Key} {l.Carried} ({l.Errors} errors)"));

    [Test]
    public async Task Each_input_of_a_plus_chain_has_its_own_count()
    {
        var (links, graph, _) = await Run("""
            + Interval 1ms count=4
            + Interval 1ms count=2
            WriteLine "{msg.Count}"
            """);

        await Assert.That(Counts(links)).IsEqualTo("m1>m3 4; m2>m3 2");
        await Assert.That(links.Keys).IsEquivalentTo(graph.Links.Select(l => l.Key));
    }

    [Test]
    public async Task Zips_numbered_inputs_are_counted_apart()
    {
        // The third message on input 1 has no partner, so it becomes an error when within runs out.
        var (links, graph, _) = await Run(NeverStop + """
            + Interval 1ms count=3
            + Interval 1ms count=2
            Zip within=50ms
                WriteLine "{msg}"
            """);

        await Assert.That(graph.Links.Select(l => $"{l.Key} {l.Input}")).IsEquivalentTo(["m1>m3 1", "m2>m3 2", "m3>m4 1"]);
        await Assert.That(Counts(links)).IsEqualTo("m1>m3 3; m2>m3 2; m3>m4 3 (1 errors)"); // two pairs and the error
    }

    [Test]
    public async Task Each_wire_out_of_a_filter_counts_what_it_passed_on()
    {
        var (links, _, pipeline) = await Run("""
            Interval 1ms count=10
                Where msg.Count <= 3
                    WriteLine "a {msg.Count}"
                    WriteLine "b {msg.Count}"
            """);

        await Assert.That(Counts(links)).IsEqualTo("m1>m2 10; m2>m3 3; m2>m4 3");

        // Where passes on the messages it keeps rather than emitting them, and counts them as passed.
        var where = pipeline.GetStats(pipeline.Modules[1]);
        await Assert.That(where.Received).IsEqualTo(10);
        await Assert.That(where.Passed).IsEqualTo(3);
        await Assert.That(where.Emitted).IsEqualTo(0);
    }

    [Test]
    public async Task A_link_on_the_errors_port_counts_only_errors_and_one_on_no_errors_counts_errors_passing_by()
    {
        // Odd ticks pass; even ones divide by zero, and Where emits an error for each.
        var (links, graph, pipeline) = await Run(NeverStop + """
            Interval 1ms count=6
                Where 10 / (msg.Count % 2) > 0
                    WriteLine "error {msg.Error}" @errors
                    WriteLine "all {msg}"
                    WriteLine "quiet {msg.Count}" @no-errors
            """);

        await Assert.That(graph.Links.Select(l => $"{l.Key} {l.Port}")).IsEquivalentTo(["m1>m2 main", "m2>m3 errors", "m2>m4 main", "m2>m5 no-errors"]);
        // The @no-errors WriteLine doesn't take the errors, but they go past it, so they're counted on the link in (Q179).
        await Assert.That(Counts(links)).IsEqualTo("m1>m2 6; m2>m3 3 (3 errors); m2>m4 6 (3 errors); m2>m5 6 (3 errors)");

        // The filter's counts add up on their own: what it received is what it passed on and the errors it emitted.
        var where = pipeline.GetStats(pipeline.Modules[1]);
        await Assert.That((where.Received, where.Passed, where.Emitted)).IsEqualTo((6L, 3L, 3L));
    }

    [Test]
    public async Task A_tap_on_errors_counts_the_errors_it_takes()
    {
        var (links, graph, _) = await Run(NeverStop + """
            Interval 1ms count=4
                Where 10 / (msg.Count % 2) > 0 as Odd
                    WriteLine "odd {msg.Count}" @no-errors

            Tap Odd @errors
                WriteLine "error {msg.Error}"
            """);

        await Assert.That(graph.Links.Select(l => $"{l.Key} {l.Port}")).IsEquivalentTo(["m1>m2 main", "m2>m3 no-errors", "m2>m4 errors", "m4>m5 main"]);
        await Assert.That(Counts(links)).IsEqualTo("m1>m2 4; m2>m3 4 (2 errors); m2>m4 2 (2 errors); m4>m5 2 (2 errors)");
    }

    [Test]
    public async Task Distribute_counts_each_message_on_the_copy_it_chose()
    {
        var (links, graph, pipeline) = await Run("""
            Interval 1ms count=8
                Distribute 2 by msg.Count % 2
                    WriteLine "{msg.Count}"
            """);

        await Assert.That(links.Keys).IsEquivalentTo(graph.Links.Select(l => l.Key));
        await Assert.That(links["m1>m2"].Carried).IsEqualTo(8);
        await Assert.That(pipeline.GetStats(pipeline.Modules[1]).Passed).IsEqualTo(8);
        var copies = graph.Links.Where(l => l.From == "m2").Select(l => links[l.Key].Carried).ToList();
        await Assert.That(copies.Count).IsEqualTo(2);
        await Assert.That(copies.Sum()).IsEqualTo(8); // each tick down one copy only, whichever its key chose
    }

    [Test]
    public async Task A_restart_counts_from_zero_with_the_same_keys()
    {
        var live = Load("""
            + Interval 1ms count=4
            + Interval 1ms count=2
            WriteLine "{msg.Count}"
            """);
        await using (var first = live.Pipeline)
            await first.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await using var again = live.Rebuild();

        await Assert.That(Counts(Graphs.LinkCounts(live, again).ToDictionary(l => l.Key))).IsEqualTo("m1>m3 0; m2>m3 0");
    }

    [Test]
    public async Task A_passthrough_counts_each_message_as_passed_once()
    {
        var builder = new PipelineBuilder("passing");
        var source = builder.AddSource(() => new Numbers(1, 2, 3, 4));
        var passthrough = builder.Add(() => new Recorder<Number>());
        var sink = new Collect<Number>();
        builder.Chain(source, passthrough, builder.Add(() => sink));
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());

        var stats = pipeline.GetStats(passthrough);
        await Assert.That((stats.Received, stats.Passed, stats.Emitted)).IsEqualTo((4L, 4L, 0L));
        await Assert.That(pipeline.GetLinkStats().Select(l => l.Carried)).IsEquivalentTo([4L, 4L]);
    }

    [Test]
    public async Task A_reload_keeps_the_count_of_a_link_it_keeps_and_starts_a_new_one_at_zero()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("fan.meep", """
            Interval 20ms
                WriteLine "a {msg.Count}"
            """);
        await lab.Client.StartAsync(file);
        await Wait.Until(async () => (await lab.Client.GetAsync("fan")).Carried("m1>m2") >= 3);
        var before = (await lab.Client.GetAsync("fan")).Carried("m1>m2")!.Value;

        // A second WriteLine beside the first: the link to the first is kept, and the one to the second is new.
        await File.WriteAllTextAsync(file, """
            Interval 20ms
                WriteLine "a {msg.Count}"
                WriteLine "b {msg.Count}"
            """);
        await lab.Client.ReloadAsync("fan");
        await Wait.Until(async () => (await lab.Client.GetAsync("fan")).Carried("m1>m3") >= 2);
        var after = await lab.Client.GetAsync("fan");
        var graph = await lab.Client.GraphAsync("fan");

        // The same counts on the stats stream, over /ws.
        await using var socket = await lab.Client.OpenSocketAsync();
        await socket.SendAsync("stats", "GET", "/pipelines/fan/stats?every=1h");
        JsonElement stats;
        while (true)
        {
            var frame = await Wait.For(socket.ReceiveAsync()) ?? throw new InvalidOperationException("The socket closed.");
            if (frame.TryGetProperty("event", out var e) && e.GetString() == "stats")
            {
                stats = frame.GetProperty("data");
                break;
            }
        }
        await socket.CancelAsync("stats");

        await Assert.That(after.Links!.Select(l => l.Key)).IsEquivalentTo(["m1>m2", "m1>m3"]);
        await Assert.That(after.Links!.Select(l => l.Key)).IsEquivalentTo(graph.Links.Select(l => l.Key));
        // Every tick since the reload went along both links, and the kept one also has the ticks from before it.
        await Assert.That(after.Carried("m1>m2")!.Value - after.Carried("m1>m3")!.Value).IsGreaterThanOrEqualTo(before);
        var overSocket = stats.GetProperty("links").EnumerateArray().ToDictionary(l => l.GetProperty("key").GetString()!, l => l.GetProperty("carried").GetInt64());
        await Assert.That(overSocket.Keys).IsEquivalentTo(["m1>m2", "m1>m3"]);
        await Assert.That(overSocket["m1>m2"]).IsGreaterThanOrEqualTo(after.Carried("m1>m2")!.Value);
    }
}
