using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class FlowTests
{
    [Test]
    public async Task Messages_flow_down_a_chain_in_order()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        var source = builder.AddSource(() => new Numbers([.. Enumerable.Range(1, 100)]));
        var doubler = builder.Add(() => new Map<Number>((n, context) => context.Emit(n with { Value = n.Value * 2 })));
        builder.Chain(source, doubler, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var expected = Enumerable.Range(1, 100).Select(v => v * 2);
        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo(Wait.Join(expected));
    }

    [Test]
    public async Task The_pipeline_halts_only_after_slow_messages_finish()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        var slow = builder.Add(() => new Map<Number>(async (n, context) =>
        {
            await Task.Delay(20);
            context.Emit(n);
        }));
        builder.Chain(builder.AddSource(() => new Numbers(1, 2, 3, 4, 5)), slow, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(collect.Received.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Each_branch_receives_every_message()
    {
        var builder = new PipelineBuilder();
        var left = new Collect<Number>();
        var right = new Collect<Number>();
        var source = builder.AddSource(() => new Numbers(1, 2, 3));
        builder.Connect(source, builder.Add(() => left));
        builder.Connect(source, builder.Add(() => right));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(left.Received.Select(n => n.Value))).IsEqualTo("1,2,3");
        await Assert.That(Wait.Join(right.Received.Select(n => n.Value))).IsEqualTo("1,2,3");
    }

    [Test]
    public async Task Several_sources_can_feed_one_module()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        var sink = builder.Add(() => collect);
        builder.Connect(builder.AddSource(() => new Numbers(1, 2, 3)), sink);
        builder.Connect(builder.AddSource(() => new Numbers(10, 20, 30)), sink);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var values = collect.Received.Select(n => n.Value).Order();
        await Assert.That(Wait.Join(values)).IsEqualTo("1,2,3,10,20,30");
    }

    [Test]
    public async Task A_module_only_receives_types_it_handles_and_others_become_errors()
    {
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = _ => { } });
        var after = new Collect<Message>();
        var source = builder.AddSource(() => new Messages(new Number(1), new Word("a"), new Number(2), new Word("b")));
        var words = builder.Add(() => new Map<Word>((w, context) => context.Emit(w)));
        builder.Chain(source, words, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        // Q54: a message no inflection takes becomes an error from the module, made from the message.
        await Assert.That(Wait.Join(after.Received.OfType<Word>().Select(w => w.Value))).IsEqualTo("a,b");
        var refused = after.Received.OfType<ErrorMessage>().ToList();
        await Assert.That(refused.Count).IsEqualTo(2);
        await Assert.That(refused[0].Error).IsEqualTo("Map #2 doesn't take Number messages.");
        await Assert.That(refused[0].Parent).IsTypeOf<Number>();
        await Assert.That(refused[0].EmittedBy).IsSameReferenceAs(words);
        await Assert.That(pipeline.GetStats(words).Refused).IsEqualTo(2);
    }

    [Test]
    public async Task Bypass_lets_other_types_skip_a_module_and_carry_on()
    {
        var builder = new PipelineBuilder();
        var after = new Collect<Message>();
        var source = builder.AddSource(() => new Messages(new Number(1), new Word("a"), new Number(2)));
        var shout = builder.Add(
            () => new Map<Word>((w, context) => context.Emit(w with { Value = w.Value.ToUpperInvariant() })),
            options: new NodeOptions { Bypass = true });
        builder.Chain(source, shout, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var seen = after.Received.Select(m => m switch { Number n => n.Value.ToString(), Word w => w.Value, _ => "?" }).Order();
        await Assert.That(Wait.Join(seen)).IsEqualTo("1,2,A");
        await Assert.That(pipeline.GetStats(shout).Handled).IsEqualTo(1);
    }

    [Test]
    public async Task A_module_handles_one_message_at_a_time_even_when_fed_by_several_sources()
    {
        var builder = new PipelineBuilder();
        var probe = new ConcurrencyProbe();
        var sink = builder.Add(() => probe);
        for (var i = 0; i < 4; i++)
            builder.Connect(builder.AddSource(() => new Numbers([.. Enumerable.Range(1, 50)])), sink);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(probe.Handled).IsEqualTo(200);
        await Assert.That(probe.Highest).IsEqualTo(1);
    }

    [Test]
    public async Task A_slow_branch_does_not_hold_up_its_sibling()
    {
        var builder = new PipelineBuilder();
        var gate = new Gate<Number>();
        var fast = new Collect<Number>();
        var source = builder.AddSource(() => new Numbers([.. Enumerable.Range(1, 10)]));
        builder.Connect(source, builder.Add(() => gate));
        builder.Connect(source, builder.Add(() => fast));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        await Wait.For(fast.Reached(10));

        await Assert.That(gate.Handled).IsEqualTo(0);
        gate.Open();
        await Wait.For(run);
        await Assert.That(gate.Handled).IsEqualTo(10);
    }

    [Test]
    public async Task A_full_buffer_drops_its_oldest_messages()
    {
        var builder = new PipelineBuilder();
        var feed = new Feed();
        var gate = new Gate<Number>();
        var collect = new Collect<Number>();
        var held = builder.Add(() => gate, options: new NodeOptions { BufferSize = 2 });
        builder.Chain(builder.AddSource(() => feed), held, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        _ = feed.Send(new Number(1));
        await Wait.For(gate.Started);
        for (var i = 2; i <= 10; i++)
            _ = feed.Send(new Number(i));
        await Wait.Until(() => pipeline.GetStats(held).Received == 10);
        gate.Open();
        feed.Finish();
        await Wait.For(run);

        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo("1,9,10");
        await Assert.That(pipeline.GetStats(held).Dropped).IsEqualTo(7);
    }

    [Test]
    public async Task A_passthrough_module_does_not_delay_the_modules_after_it()
    {
        var builder = new PipelineBuilder();
        var recorder = new Recorder<Number>(startOpen: false);
        var after = new Collect<Number>();
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2, 3, 4, 5)),
            builder.Add(() => recorder),
            builder.Add(() => after));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        await Wait.For(after.Reached(5));

        await Assert.That(recorder.Handled.Count).IsEqualTo(0);
        recorder.Open();
        await Wait.For(run);
        await Assert.That(Wait.Join(recorder.Handled.Select(n => n.Value))).IsEqualTo("1,2,3,4,5");
    }

    [Test]
    public async Task A_passthrough_module_turns_types_it_cannot_handle_into_errors()
    {
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = _ => { } });
        var after = new Collect<Message>();
        builder.Chain(
            builder.AddSource(() => new Messages(new Number(1), new Word("a"))),
            builder.Add(() => new Recorder<Word>()),
            builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(after.Received.Count).IsEqualTo(2);
        await Assert.That(after.Received.OfType<Word>().Single().Value).IsEqualTo("a");
        await Assert.That(after.Received.OfType<ErrorMessage>().Single().Error).IsEqualTo("Recorder #2 doesn't take Number messages.");
    }
}
