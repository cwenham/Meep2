using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class LifecycleTests
{
    [Test]
    public async Task A_pipeline_without_sources_halts_straight_away()
    {
        var builder = new PipelineBuilder();
        builder.Add(() => new Collect<Number>());

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
    }

    [Test]
    public async Task A_pipeline_can_only_run_once()
    {
        await using var pipeline = new PipelineBuilder().Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(() => pipeline.RunAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stopping_lets_messages_already_in_the_pipeline_finish()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        var slow = builder.Add(() => new Map<Number>(async (n, context) =>
        {
            await Task.Delay(30);
            context.Emit(n);
        }));
        builder.Chain(builder.AddSource(() => new Ticker(TimeSpan.FromMilliseconds(5))), slow, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(stop.Token);
        await Wait.For(collect.Reached(2));
        stop.Cancel();
        await Wait.For(run);

        var stats = pipeline.GetStats(slow);
        await Assert.That(stats.Received).IsGreaterThan(2);
        await Assert.That(stats.Handled + stats.Dropped).IsEqualTo(stats.Received);
        await Assert.That((long)collect.Received.Count).IsEqualTo(stats.Handled);
    }

    [Test]
    public async Task Messages_still_waiting_after_the_drain_timeout_are_abandoned()
    {
        var builder = new PipelineBuilder(options: new PipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(100) });
        var feed = new Feed();
        var stuck = new Gate<Number>(); // never opened
        builder.Connect(builder.AddSource(() => feed), builder.Add(() => stuck));

        await using var pipeline = builder.Build();
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(stop.Token);
        var handling = feed.Send(new Number(1));
        var waiting = feed.Send(new Number(2));
        await Wait.For(stuck.Started);
        stop.Cancel();
        await Wait.For(run);

        await Assert.That(await Wait.For(handling)).IsEqualTo(MessageOutcome.Abandoned);
        await Assert.That(await Wait.For(waiting)).IsEqualTo(MessageOutcome.Abandoned);
    }

    [Test]
    public async Task Disposing_a_running_pipeline_stops_it()
    {
        var builder = new PipelineBuilder();
        builder.Connect(builder.AddSource(() => new Ticker(TimeSpan.FromMilliseconds(5))), builder.Add(() => new Collect<Number>()));
        var pipeline = builder.Build();
        var run = pipeline.RunAsync();

        await Wait.For(pipeline.DisposeAsync().AsTask());
        await Wait.For(run);
    }

    [Test]
    public async Task Modules_are_disposed_when_the_pipeline_finishes()
    {
        var builder = new PipelineBuilder();
        var module = new DisposableModule();
        builder.Connect(builder.AddSource(() => new Numbers(1)), builder.Add(() => module));

        await using (var pipeline = builder.Build())
            await Wait.For(pipeline.RunAsync());

        await Assert.That(module.Disposed).IsTrue();
    }

    [Test]
    public async Task A_module_cannot_emit_when_it_is_not_handling_a_message()
    {
        var builder = new PipelineBuilder();
        ModuleContext? kept = null;
        builder.Connect(
            builder.AddSource(() => new Numbers(1)),
            builder.Add(() => new Map<Number>((_, context) => kept = context)));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(() => kept!.Emit(new Number(2))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_message_that_was_already_emitted_cannot_be_tracked_again()
    {
        var builder = new PipelineBuilder();
        Exception? caught = null;
        builder.Connect(
            builder.AddSource(() => new Numbers(1)),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                try
                {
                    context.EmitTracked(n);
                }
                catch (ArgumentException exception)
                {
                    caught = exception;
                }
            })));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(caught).IsNotNull();
    }

    [Test]
    public async Task The_builder_refuses_to_feed_a_source()
    {
        var builder = new PipelineBuilder();
        var first = builder.AddSource(() => new Numbers(1));
        var second = builder.AddSource(() => new Numbers(2));

        await Assert.That(() => builder.Connect(first, second)).Throws<ArgumentException>();
    }

    [Test]
    public async Task The_builder_refuses_two_modules_with_the_same_name()
    {
        var builder = new PipelineBuilder();
        builder.Add(() => new Collect<Number>(), name: "Sink");

        await Assert.That(() => builder.Add(() => new Collect<Word>(), name: "Sink")).Throws<ArgumentException>();
    }

    [Test]
    public async Task A_module_that_cannot_be_created_stops_the_pipeline_starting()
    {
        var builder = new PipelineBuilder();
        builder.Add<Collect<Number>>(() => throw new InvalidOperationException("no"));

        await using var pipeline = builder.Build();

        await Assert.That(() => pipeline.RunAsync()).Throws<InvalidOperationException>();
    }

    private sealed class DisposableModule : AInflection<Message>, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
