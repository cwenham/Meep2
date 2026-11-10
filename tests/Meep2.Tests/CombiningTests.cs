using System.Collections.Concurrent;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;
using Numbers = Meep2.Tests.Support.Numbers;

namespace Meep2.Tests;

/// <summary>Batch, Zip and Distribute, and what the runtime does for them: inputs, keeping and flushing (step 9).</summary>
public class CombiningTests
{
    private static long[] Counts(Batched batch) => [.. batch.Items.Cast<Tick>().Select(t => t.Count)];

    [Test]
    public async Task Batch_gathers_so_many_and_sends_the_rest_when_nothing_else_is_left()
    {
        var run = await TestRun.Start("""
            Interval 1ms count=10
                Batch 4
                    Sink
            """);

        var batches = run.At("Sink").Cast<Batched>().ToList();
        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(batches.Select(b => b.Count)).IsEquivalentTo([4, 4, 2]);
        await Assert.That(batches.SelectMany(Counts)).IsEquivalentTo(Enumerable.Range(1, 10).Select(i => (long)i));
        await Assert.That(batches[0].Parents.Count).IsEqualTo(4); // its items are its parents
    }

    [Test]
    public async Task Batch_within_sends_what_has_come_once_the_time_is_up()
    {
        var run = await TestRun.Start("""
            Interval 150ms count=3
                Batch within=20ms
                    Sink
            """);

        await Assert.That(run.At("Sink").Cast<Batched>().Select(b => b.Count)).IsEquivalentTo([1, 1, 1]);
    }

    [Test]
    public async Task A_batch_keeps_its_items_leases_open_until_it_has_finished()
    {
        var lease = new TestLease();
        var seen = new ConcurrentQueue<bool>();
        var builder = new PipelineBuilder("batched");
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Number(1), new Number(2), new Number(3))),
            builder.Add(() => new Batch(new BatchParameters { Size = 3 })),
            builder.Add(() => new Map<Batched>((_, _) => seen.Enqueue(!lease.IsClosed))));
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed);

        await Assert.That(seen.ToArray()).IsEquivalentTo([true]);
    }

    [Test]
    public async Task Zip_pairs_its_two_inputs_in_order()
    {
        var run = await TestRun.Start("""
            + Interval 5ms count=3 payload="a"
            + Interval 9ms count=3 payload="b"
            Zip
                Sink
            """);

        var pairs = run.At("Sink").Cast<Zipped>().ToList();
        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(pairs.Select(p => $"{((Tick)p.Left).Payload}{((Tick)p.Left).Count}+{((Tick)p.Right).Payload}{((Tick)p.Right).Count}"))
            .IsEquivalentTo(["a1+b1", "a2+b2", "a3+b3"]);
        await Assert.That(pairs[0].Parents.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Zip_by_a_key_pairs_what_matches_and_reports_what_nothing_matched()
    {
        var run = await TestRun.Start("""
            + Interval 2ms count=4 payload="left"
            + Interval 5ms count=2 payload="right"
            Zip by msg.Count
                Sink
            """);

        var pairs = run.At("Sink").OfType<Zipped>().Select(p => ((Tick)p.Left).Count).Order().ToList();
        await Assert.That(pairs).IsEquivalentTo([1L, 2L]);
        var unmatched = run.At("Sink").OfType<ErrorMessage>().ToList(); // errors go on down the branch, as any do
        await Assert.That(unmatched.Count).IsEqualTo(2);
        await Assert.That(unmatched[0].Error).StartsWith("Nothing came on Zip's input 2 to pair with this message with the key ");
    }

    [Test]
    public async Task Zip_gives_up_on_a_message_past_its_limit_its_time_or_its_deadline()
    {
        // Interval sends its first message at once, so each first pair is made straight away, and the right's second
        // message comes 300ms later.
        var limited = await TestRun.Start("""
            + Interval 2ms count=6 payload="left"
            + Interval 300ms count=2 payload="right"
            Zip limit=2
                Sink
            """);
        var timed = await TestRun.Start("""
            + Interval 2ms count=2 payload="left"
            + Interval 300ms count=2 payload="right"
            Zip within=50ms
                Sink
            """);
        var expiring = await TestRun.Start("""
            + Interval 2ms count=2 payload="left" @expires=40ms
            + Interval 300ms count=2 payload="right"
            Zip
                Sink
            """);
        static string[] Errors(TestRun run) => [.. run.At("Sink").OfType<ErrorMessage>().Select(e => $"{e.Error} ({((Tick)e.Parents[0]).Count})")];
        static long[] Paired(TestRun run) => [.. run.At("Sink").OfType<Zipped>().Select(p => ((Tick)p.Left).Count)];

        await Assert.That(limited.Diagnostics + timed.Diagnostics + expiring.Diagnostics).IsEqualTo("");
        await Assert.That(Errors(limited)).IsEquivalentTo([
            "Nothing came on Zip's input 2 to pair with this message before 2 messages were waiting, Zip's limit. (2)",
            "Nothing came on Zip's input 2 to pair with this message before 2 messages were waiting, Zip's limit. (3)",
            "Nothing came on Zip's input 2 to pair with this message before 2 messages were waiting, Zip's limit. (4)",
            "Nothing came on Zip's input 2 to pair with this message. (6)", // when nothing else was left to do
        ]);
        await Assert.That(Paired(limited)).IsEquivalentTo([1L, 5L]); // the right's second takes the oldest still waiting
        await Assert.That(Errors(timed)).IsEquivalentTo([
            "Nothing came on Zip's input 2 to pair with this message within 50ms. (2)",
            "Nothing came on Zip's input 1 to pair with this message. (2)",
        ]);
        await Assert.That(Errors(expiring)).IsEquivalentTo([
            "Nothing came on Zip's input 2 to pair with this message before it expired. (2)",
            "Nothing came on Zip's input 1 to pair with this message. (2)",
        ]);
    }

    [Test]
    public async Task Distribute_spreads_messages_across_copies_and_keeps_order_per_key()
    {
        var run = await TestRun.Start("""
            Interval 1ms count=30
                Distribute 3 by msg.Count % 3
                    Echo.Loud
                        Sink
            """);

        var pipeline = run.Result.Pipeline!;
        var echoes = pipeline.Modules.Where(m => m.Verb == "Echo").ToList();
        var ticks = run.At("Sink").Cast<Tick>().ToList();
        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(echoes.Count).IsEqualTo(3);
        await Assert.That(ticks.Count).IsEqualTo(30);
        foreach (var key in new[] { 0, 1, 2 })
        {
            var sameKey = ticks.Where(t => t.Count % 3 == key).Select(t => t.Count).ToList();
            await Assert.That(sameKey).IsEquivalentTo(sameKey.Order().ToList()); // in order within a key
        }
    }

    [Test]
    [Arguments("const N = 3\nInterval 1ms count=1\n    Distribute msg.Count\n        Sink", "Distribute needs a whole number of copies, at least 2, written in the file")]
    [Arguments("Interval 1ms count=1\n    Distribute 2\n        Sink as Out\nTap Out\n    Sink", "Out is in a branch Distribute copies, so there's more than one of it")]
    [Arguments("Interval 1ms count=1\n    Batch\n        Sink", "needs a size, as in Batch 100, or a time, as in Batch within=1s")]
    public async Task Combining_mistakes_say_what_they_are(string text, string expected)
    {
        var run = await TestRun.Start(text, run: false);

        await Assert.That(run.Diagnostics).Contains(expected);
    }

    [Test]
    public async Task A_module_knows_which_input_a_message_came_in_on()
    {
        var inputs = new ConcurrentQueue<(int Value, int Input)>();
        var builder = new PipelineBuilder("inputs");
        var first = builder.AddSource(() => new Numbers(1, 2));
        var second = builder.AddSource(() => new Numbers(10, 20));
        var both = builder.Add(() => new Map<Number>((n, context) => inputs.Enqueue((n.Value, context.Input))));
        builder.Connect(first, both).Connect(second, both);
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());

        await Assert.That(inputs.OrderBy(i => i.Value).Select(i => i.Input)).IsEquivalentTo([1, 1, 2, 2]);
    }

    [Test]
    public async Task What_a_module_still_keeps_when_the_pipeline_halts_is_let_go()
    {
        var lease = new TestLease();
        var builder = new PipelineBuilder("hoarding");
        builder.Chain(builder.AddSource(() => new Leasing(lease, new Number(1))), builder.Add(() => new Hoard()));
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed);

        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    // Keeps everything, and doesn't flush.
    private sealed class Hoard : AInflection<Number>
    {
        protected override ValueTask HandleAsync(Number message, ModuleContext context, CancellationToken cancellationToken)
        {
            context.Keep(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLease : Lease
    {
        private int _closes;

        public int Closes => Volatile.Read(ref _closes);

        protected override ValueTask OnClosedAsync()
        {
            Interlocked.Increment(ref _closes);
            return ValueTask.CompletedTask;
        }
    }

    // Emits each message with the same lease, holding it while it does.
    private sealed class Leasing(Lease lease, params Message[] messages) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            using var hold = lease.Hold();
            foreach (var message in messages)
                context.Emit(message, lease);
            return Task.CompletedTask;
        }
    }
}
