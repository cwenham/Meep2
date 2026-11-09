using Meep2.Meeplang.Editing;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Changing a running pipeline's text, and the modules that carry on with their state (goal 4, step 8).</summary>
public class LiveTests
{
    private static string Labels(IEnumerable<Meep2.SDK.Modules.ModuleInfo> modules) => Wait.Join(modules.Select(m => m.ToString()));

    [Test]
    public async Task Inserting_a_module_mid_chain_keeps_the_modules_after_it()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Tally
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 3);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Where msg.Count > 0
                    Tally
                        Sink
            """);
        var before = run.Numbers.Count;
        await Wait.Until(() => run.Numbers.Count >= before + 3);

        await Assert.That(change.Applied).IsTrue();
        await Assert.That(Labels(change.Report!.Added)).IsEqualTo("Where #2");
        await Assert.That(Labels(change.Report.Unchanged)).IsEqualTo("Interval #1,Tally #3,Sink #4");
        await Assert.That(change.Report.Removed).IsEmpty();
        // Tally kept counting: no number came twice, so it wasn't made again.
        var numbers = run.Numbers; // once: the pipeline is still adding to it
        await Assert.That(numbers.Distinct().Count()).IsEqualTo(numbers.Count);
        await Assert.That(change.Matches.Single(m => m.Next?.Verb == "Tally").By).IsEqualTo(MatchedBy.Position);
        await Assert.That(change.Matches.Single(m => m.Next?.Verb == "Sink").By).IsEqualTo(MatchedBy.Structure);
    }

    [Test]
    public async Task A_module_that_cant_take_new_parameters_is_made_again()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Tally step=1
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 3);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Tally step=1000
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Any(n => n >= 1000));

        await Assert.That(Labels(change.Report!.Replaced)).IsEqualTo("Tally #2");
        await Assert.That(run.Numbers.First(n => n >= 1000)).IsEqualTo(1000); // it counts from 1 again
    }

    [Test]
    public async Task A_module_that_takes_new_parameters_keeps_its_state()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                KeepingTally step=1
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 3);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                KeepingTally step=1000
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Any(n => n >= 1000));

        var last = run.Numbers.Last(n => n < 1000);
        await Assert.That(Labels(change.Report!.Reconfigured)).IsEqualTo("KeepingTally #2");
        await Assert.That(run.Numbers.First(n => n >= 1000)).IsEqualTo((last + 1) * 1000); // it kept counting
    }

    [Test]
    public async Task Removing_a_module_mid_chain_joins_the_modules_either_side()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Tally
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 2);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Sink
            """);
        await Wait.Until(() => run.Run.At("Sink").OfType<Meep2.Modules.Tick>().Count() >= 2);

        await Assert.That(Labels(change.Report!.Removed)).IsEqualTo("Tally #2");
        await Assert.That(Labels(change.Report.Unchanged)).IsEqualTo("Interval #1,Sink #2");
    }

    [Test]
    public async Task Text_that_doesnt_load_leaves_the_pipeline_as_it_was()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Tally
                    Sink
            """);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Talley
                    Sink
            """);
        var before = run.Numbers.Count;
        await Wait.Until(() => run.Numbers.Count > before + 2);

        await Assert.That(change.Applied).IsFalse();
        await Assert.That(string.Join("\n", change.Diagnostics)).Contains("There's no module called Talley. Did you mean 'Tally'?");
        await Assert.That(run.Pipeline.Syntax.Modules[0].Children[0].Verb).IsEqualTo("Tally");
    }

    [Test]
    public async Task Interval_takes_a_new_period_and_carries_on_counting()
    {
        await using var run = TestRun.BeginLive("""
            Interval 50ms
                Sink
            """);
        await Wait.Until(() => run.Run.At("Sink").Count >= 2);

        var change = await run.Pipeline.ChangeAsync("""
            Interval 2ms payload="fast"
                Sink
            """);
        await Wait.Until(() => run.Run.At("Sink").OfType<Meep2.Modules.Tick>().Count(t => t.Payload == "fast") >= 10);
        var ticks = run.Run.At("Sink").OfType<Meep2.Modules.Tick>().ToList();

        await Assert.That(Labels(change.Report!.Reconfigured)).IsEqualTo("Interval #1"); // Q121
        await Assert.That(ticks.Select(t => t.Count)).IsEquivalentTo(Enumerable.Range(1, ticks.Count).Select(i => (long)i)); // no count started again
    }

    [Test]
    public async Task A_source_that_cant_take_new_parameters_is_started_again()
    {
        var first = TestServer.FreePort();
        var second = TestServer.FreePort();
        await using var run = TestRun.BeginLive($$"""
            Listen "http://127.0.0.1:{{first}}/"
                Respond "one"
            """);
        using var http = new HttpClient();
        await Wait.Until(async () => await Answers(http, first) == "one");

        var change = await run.Pipeline.ChangeAsync($$"""
            Listen "http://127.0.0.1:{{second}}/"
                Respond "one"
            """);
        await Wait.Until(async () => await Answers(http, second) == "one");

        await Assert.That(Labels(change.Report!.Replaced)).IsEqualTo("Listen #1"); // a new port needs a new Listen
        await Assert.That(await Answers(http, first)).IsNull();
    }

    [Test]
    public async Task Listen_takes_a_new_path_without_closing_its_port()
    {
        var port = TestServer.FreePort();
        await using var run = TestRun.BeginLive($$"""
            Listen "http://127.0.0.1:{{port}}/old/"
                Respond "here"
            """);
        using var http = new HttpClient();
        await Wait.Until(async () => await Answers(http, port, "old/") == "here");

        var change = await run.Pipeline.ChangeAsync($$"""
            Listen "http://127.0.0.1:{{port}}/new/"
                Respond "here"
            """);

        // The old path is let go once its open requests are answered; until then it says it's stopping.
        await Wait.Until(async () => await Answers(http, port, "old/") == "Nothing is listening at /old/.");

        await Assert.That(Labels(change.Report!.Reconfigured)).IsEqualTo("Listen #1"); // Q121
        await Assert.That(await Answers(http, port, "new/")).IsEqualTo("here");
    }

    // What a Listen answers, or null if nothing is listening on the port.
    private static async Task<string?> Answers(HttpClient http, int port, string path = "")
    {
        try
        {
            return await http.GetStringAsync($"http://127.0.0.1:{port}/{path}");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is not null)
        {
            return exception.StatusCode == System.Net.HttpStatusCode.NotFound ? "Nothing is listening at /" + path + "." : exception.Message;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [Test]
    public async Task Renaming_and_renumbering_keep_a_module()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Tally as Counter
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 2);

        var renamed = await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Tally as Counted
                    Sink
            """);
        await Wait.Until(() => run.Numbers.Count >= 5);

        await Assert.That(Labels(renamed.Report!.Unchanged)).IsEqualTo("Interval #1,Counted,Sink #3");
        var numbers = run.Numbers; // once: the pipeline is still adding to it
        await Assert.That(numbers.Distinct().Count()).IsEqualTo(numbers.Count);
        await Assert.That(run.Pipeline.Pipeline.Modules.Select(m => m.ToString())).Contains("Counted");
    }

    [Test]
    public async Task No_message_is_lost_while_other_branches_change()
    {
        await using var run = TestRun.BeginLive("""
            Interval 1ms
                Tally
                    Sink
                Echo.Loud
            """);
        for (var i = 0; i < 20; i++)
        {
            var other = i % 2 == 0 ? "Echo.Quiet" : "Echo.Loud";
            var change = await run.Pipeline.ChangeAsync($"""
                Interval 1ms
                    Tally
                        Sink
                    {other}
                """);
            await Assert.That(change.Applied).IsTrue();
            await Task.Delay(5);
        }
        await Wait.Until(() => run.Numbers.Count >= 50);

        // Tally numbers each message it gets, so a gap would mean Sink missed one.
        var numbers = run.Numbers;
        await Assert.That(numbers).IsEquivalentTo(Enumerable.Range(1, numbers.Count));
    }

    [Test]
    public async Task A_restart_builds_the_pipeline_from_its_edited_text()
    {
        await using var run = TestRun.BeginLive("""
            Interval 5ms
                Sink
            """);
        await run.Pipeline.ChangeAsync("""
            Interval 5ms
                Tally
                    Sink
            """);
        run.Pipeline.Pipeline.Stop();
        await Wait.For(run.Running);
        await run.Pipeline.Pipeline.DisposeAsync();

        var again = run.Pipeline.Rebuild();

        await Assert.That(again.Modules.Select(m => m.Verb)).IsEquivalentTo(["Interval", "Tally", "Sink"]);
    }

    [Test]
    public async Task Changes_wait_for_a_module_to_finish_the_message_it_is_handling()
    {
        var handling = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var builder = new PipelineBuilder("slow");
        var source = builder.AddSource(() => new Numbers(1, 2));
        var slow = builder.Add(() => new Map<Number>(async (number, context) =>
        {
            handling.TrySetResult();
            await release.Task;
            context.Emit(number);
        }), name: "Slow");
        var sink = new Collect<Number>();
        builder.Chain(source, slow, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        await Wait.For(handling.Task);

        var next = new PipelineBuilder("slow");
        var nextSource = next.AddSource(() => new Numbers(1, 2));
        var nextSlow = next.Add(() => new Map<Number>((number, context) => context.Emit(new Number(number.Value * 10))), name: "Slow");
        var nextSink = next.Add(() => sink);
        next.Chain(nextSource, nextSlow, nextSink);
        var change = new PipelineChange(next)
            .Keep(pipeline.Modules[0], nextSource)
            .Keep(pipeline.Modules[1], nextSlow, parametersChanged: true)
            .Keep(pipeline.Modules[2], nextSink);
        var applying = pipeline.ApplyAsync(change);
        await Task.Delay(50);
        var waited = !applying.IsCompleted;
        release.SetResult();
        var report = await Wait.For(applying);
        await Wait.For(running);

        await Assert.That(waited).IsTrue(); // it didn't change Slow mid-message
        await Assert.That(Labels(report.Replaced)).IsEqualTo("Slow");
        await Assert.That(sink.Received.Select(n => n.Value).ToList()).IsEquivalentTo([1, 20]); // the first by the old Slow, the second by the new
    }
}

/// <summary>Changing a running pipeline from C#, with <see cref="PipelineChange"/>.</summary>
public class PipelineChangeTests
{
    [Test]
    public async Task A_change_cant_keep_a_source_as_a_module_that_handles_messages()
    {
        var builder = new PipelineBuilder("p");
        builder.Chain(builder.AddSource(() => new Numbers(1)), builder.Add(() => new Collect<Number>()));
        await using var pipeline = builder.Build();
        var held = new TaskCompletionSource();
        var next = new PipelineBuilder("p");
        var source = next.AddSource(() => new Numbers(1));
        var sink = next.Add(() => new Collect<Number>());
        next.Chain(source, sink);

        await Assert.That(async () => { await pipeline.ApplyAsync(new PipelineChange(next)); }).Throws<InvalidOperationException>().WithMessageContaining("isn't running");
        var running = pipeline.RunAsync();
        await Wait.For(running);
        await Assert.That(() => new PipelineChange(next).Keep(pipeline.Modules[0], pipeline.Modules[0])).Throws<ArgumentException>().WithMessageContaining("isn't one of the new pipeline's modules");
        held.SetResult();
    }

    [Test]
    public async Task A_new_buffer_size_replaces_the_module_and_a_removed_modules_taps_end()
    {
        var builder = new PipelineBuilder("p");
        var source = builder.AddSource(() => new Ticks());
        var step = builder.Add(() => new Map<Number>((n, c) => c.Emit(n)), name: "Step");
        var sink = new Collect<Number>();
        var end = builder.Add(() => sink, name: "End");
        builder.Chain(source, step, end);
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        await Wait.For(sink.Reached(3));
        using var tap = pipeline.Tap(step);

        var next = new PipelineBuilder("p");
        var nextSource = next.AddSource(() => new Ticks());
        var nextEnd = next.Add(() => sink, name: "End", options: new NodeOptions { BufferSize = 7 });
        next.Chain(nextSource, nextEnd);
        var report = await pipeline.ApplyAsync(new PipelineChange(next).Keep(source, nextSource).Keep(end, nextEnd));
        var count = sink.Received.Count;
        await Wait.For(sink.Reached(count + 3));
        pipeline.Stop();
        await Wait.For(running);

        await Assert.That(Wait.Join(report.Removed.Select(m => m.ToString()))).IsEqualTo("Step");
        await Assert.That(Wait.Join(report.Replaced.Select(m => m.ToString()))).IsEqualTo("End");
        await Assert.That(Wait.Join(report.Unchanged.Select(m => m.ToString()))).IsEqualTo("Ticks #1");
        await Wait.For(Task.Run(async () =>
        {
            await foreach (var _ in tap.Messages.ReadAllAsync())
            {
                // Read what it saw before Step was removed; the tap ends after that.
            }
        }));
    }

    // Emits a Number every few milliseconds until stopped.
    private sealed class Ticks : Meep2.SDK.Modules.ASource
    {
        protected override async Task RunAsync(Meep2.SDK.Modules.ModuleContext context, CancellationToken cancellationToken)
        {
            for (var i = 1; !cancellationToken.IsCancellationRequested; i++)
            {
                context.Emit(new Number(i));
                await Task.Delay(3, cancellationToken);
            }
        }
    }
}
