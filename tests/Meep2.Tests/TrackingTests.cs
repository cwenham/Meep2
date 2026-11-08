using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class TrackingTests
{
    private static PipelineBuilder Quiet() =>
        new(options: new PipelineOptions { UnhandledError = _ => { } });

    [Test]
    public async Task A_tracked_message_finishes_only_after_all_its_descendants()
    {
        var builder = Quiet();
        var feed = new Feed();
        var gate = new Gate<Word>();
        var source = builder.AddSource(() => feed);
        var split = builder.Add(() => new Map<Number>((n, context) =>
        {
            context.Emit(new Word("a"));
            context.Emit(new Word("b"));
        }));
        builder.Chain(source, split, builder.Add(() => gate), builder.Add(() => new Collect<Word>()));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        var outcome = feed.Send(new Number(1));
        await Wait.For(gate.Started);
        await Task.Delay(50);

        await Assert.That(outcome.IsCompleted).IsFalse();
        gate.Open();
        await Assert.That(await Wait.For(outcome)).IsEqualTo(MessageOutcome.Completed);
        feed.Finish();
        await Wait.For(run);
    }

    [Test]
    public async Task A_tracked_message_reports_being_dropped()
    {
        var builder = Quiet();
        var feed = new Feed();
        var gate = new Gate<Number>();
        builder.Connect(builder.AddSource(() => feed), builder.Add(() => gate, options: new NodeOptions { BufferSize = 1 }));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        var first = feed.Send(new Number(1));
        await Wait.For(gate.Started);
        var second = feed.Send(new Number(2));
        var third = feed.Send(new Number(3)); // pushes the second out of the one-message buffer

        await Assert.That(await Wait.For(second)).IsEqualTo(MessageOutcome.Dropped);
        gate.Open();
        await Assert.That(await Wait.For(first)).IsEqualTo(MessageOutcome.Completed);
        await Assert.That(await Wait.For(third)).IsEqualTo(MessageOutcome.Completed);
        feed.Finish();
        await Wait.For(run);
    }

    [Test]
    public async Task A_tracked_message_reports_an_unhandled_error_among_its_descendants()
    {
        var builder = Quiet();
        var feed = new Feed();
        builder.Connect(
            builder.AddSource(() => feed),
            builder.Add(() => new Map<Number>((_, _) => throw new InvalidOperationException("boom"))));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        var outcome = await Wait.For(feed.Send(new Number(1)));
        feed.Finish();
        await Wait.For(run);

        await Assert.That(outcome).IsEqualTo(MessageOutcome.UnhandledError);
    }

    [Test]
    public async Task A_new_message_becomes_a_child_of_the_message_being_handled()
    {
        var builder = new PipelineBuilder();
        var words = new Collect<Word>();
        var source = builder.AddSource(() => new Numbers(7), name: "Source");
        var describe = builder.Add(() => new Map<Number>((n, context) => context.Emit(new Word($"number {n.Value}"))));
        builder.Chain(source, describe, builder.Add(() => words));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var word = words.Received.Single();
        await Assert.That(word.EmittedBy).IsSameReferenceAs(describe);
        await Assert.That(((Number)word.Parent!).Value).IsEqualTo(7);
        await Assert.That(((Number)word.Fam["Source"]!).Value).IsEqualTo(7);
        await Assert.That(word.Fam["Map"]).IsNull(); // fam only searches ancestors
    }

    [Test]
    public async Task A_derived_message_inherits_its_parents_expiry()
    {
        var builder = Quiet();
        var feed = new Feed();
        var words = new Collect<Word>();
        var deadline = Deadline.After(TimeSpan.FromMinutes(10));
        builder.Chain(
            builder.AddSource(() => feed),
            builder.Add(() => new Map<Number>((n, context) => context.Emit(new Word("child")))),
            builder.Add(() => words));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        await Wait.For(feed.Send(new Number(1) { Expires = deadline }));
        feed.Finish();
        await Wait.For(run);

        await Assert.That(words.Received.Single().Expires).IsEqualTo(deadline);
    }

    [Test]
    public async Task A_module_can_bring_expiry_forward_but_not_put_it_back()
    {
        var builder = Quiet();
        var feed = new Feed();
        var words = new Collect<Word>();
        var parentDeadline = Deadline.After(TimeSpan.FromMinutes(10));
        var sooner = Deadline.After(TimeSpan.FromMinutes(1));
        var later = Deadline.After(TimeSpan.FromHours(1));
        builder.Chain(
            builder.AddSource(() => feed),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                context.Emit(new Word("sooner") { Expires = sooner });
                context.Emit(new Word("later") { Expires = later });
                context.Emit(new Word("cleared") { Expires = null });
            })),
            builder.Add(() => words));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        await Wait.For(feed.Send(new Number(1) { Expires = parentDeadline }));
        feed.Finish();
        await Wait.For(run);

        var byValue = words.Received.ToDictionary(w => w.Value, w => w.Expires);
        await Assert.That(byValue["sooner"]).IsEqualTo(sooner);
        await Assert.That(byValue["later"]).IsEqualTo(parentDeadline);
        await Assert.That(byValue["cleared"]).IsEqualTo(parentDeadline);
    }

    [Test]
    public async Task A_message_with_several_parents_expires_with_the_earliest()
    {
        var builder = Quiet();
        var feed = new Feed();
        var words = new Collect<Word>();
        var parentDeadline = Deadline.After(TimeSpan.FromMinutes(10));
        var sooner = Deadline.After(TimeSpan.FromMinutes(1));
        builder.Chain(
            builder.AddSource(() => feed),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                var other = new Number(99) { Expires = sooner };
                context.Emit(new Word("pair") { Parents = [n, other] });
            })),
            builder.Add(() => words));

        await using var pipeline = builder.Build();
        var run = pipeline.RunAsync();
        await Wait.For(feed.Send(new Number(1) { Expires = parentDeadline }));
        feed.Finish();
        await Wait.For(run);

        var pair = words.Received.Single();
        await Assert.That(pair.Parents.Count).IsEqualTo(2);
        await Assert.That(pair.Expires).IsEqualTo(sooner);

        // The other parent was made by the module but never emitted, so it's stamped as a child of the input.
        await Assert.That(pair.Parents[1].Parent).IsSameReferenceAs(pair.Parents[0]);
    }
}
