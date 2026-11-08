using System.Collections.Concurrent;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class ErrorTests
{
    // Throws on the given value and passes every other number on.
    private static Map<Number> ThrowsOn(int value) => new((n, context) =>
    {
        if (n.Value == value)
            throw new InvalidOperationException($"boom {n.Value}");
        context.Emit(n);
    });

    // A module that only takes errors, so the numbers it isn't for are filtered rather than refused (Q54).
    private static readonly NodeOptions ErrorsOnly = new() { Errors = ErrorDelivery.Only };

    private static (PipelineBuilder Builder, ConcurrentQueue<ErrorMessage> Unhandled) NewBuilder(ErrorPolicy? policy = null)
    {
        var unhandled = new ConcurrentQueue<ErrorMessage>();
        var options = new PipelineOptions { UnhandledError = unhandled.Enqueue, ErrorPolicy = policy ?? ErrorPolicy.Default };
        return (new PipelineBuilder(options: options), unhandled);
    }

    [Test]
    public async Task A_thrown_exception_becomes_an_error_message_downstream()
    {
        var (builder, _) = NewBuilder();
        var numbers = new Collect<Number>();
        var errors = new Collect<ErrorMessage>();
        var thrower = builder.Add(() => ThrowsOn(3));
        builder.Connect(builder.AddSource(() => new Numbers(1, 2, 3, 4)), thrower);
        builder.Connect(thrower, builder.Add(() => numbers));
        builder.Connect(thrower, builder.Add(() => errors));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(numbers.Received.Select(n => n.Value))).IsEqualTo("1,2,4");
        await Assert.That(errors.Received.Count).IsEqualTo(1);
        var error = errors.Received[0];
        await Assert.That(error.Error).IsEqualTo("boom 3");
        await Assert.That(error.Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(((Number)error.Parent!).Value).IsEqualTo(3);
        await Assert.That(error.EmittedBy).IsSameReferenceAs(thrower);
    }

    [Test]
    public async Task A_module_that_throws_is_replaced_with_a_fresh_one()
    {
        var (builder, _) = NewBuilder();
        var created = 0;
        var collect = new Collect<Number>();
        var counter = builder.Add(() =>
        {
            Interlocked.Increment(ref created);
            var seen = 0;
            return new Map<Number>((n, context) =>
            {
                seen++;
                if (n.Value == 2)
                    throw new InvalidOperationException("boom");
                context.Emit(new Number(seen));
            });
        });
        builder.Chain(builder.AddSource(() => new Numbers(1, 2, 3)), counter, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        // The fresh module starts counting again, so it reports 1 for the third message.
        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo("1,1");
        await Assert.That(created).IsEqualTo(2);
        await Assert.That(pipeline.GetStats(counter).Restarts).IsEqualTo(1);
        await Assert.That(pipeline.GetStats(counter).Failed).IsEqualTo(1);
    }

    [Test]
    public async Task An_error_nothing_takes_is_reported_as_unhandled()
    {
        var (builder, unhandled) = NewBuilder();
        builder.Connect(builder.AddSource(() => new Numbers(1, 2)), builder.Add(() => ThrowsOn(2)));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(unhandled.Count).IsEqualTo(1);
        await Assert.That(unhandled.Single().Error).IsEqualTo("boom 2");
    }

    [Test]
    public async Task An_error_a_module_consumes_is_handled()
    {
        var (builder, unhandled) = NewBuilder();
        var errors = new Collect<ErrorMessage>();
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2)),
            builder.Add(() => ThrowsOn(2)),
            builder.Add(() => errors, options: ErrorsOnly));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(errors.Received.Count).IsEqualTo(1);
        await Assert.That(unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_error_a_module_passes_on_is_still_unhandled()
    {
        var (builder, unhandled) = NewBuilder();
        var passOn = builder.Add(() => new Map<Message>((m, context) => context.Emit(m)));
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2)),
            builder.Add(() => ThrowsOn(2)),
            passOn,
            builder.Add(() => new Collect<Number>())); // doesn't take errors

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(pipeline.GetStats(passOn).Handled).IsEqualTo(2);
        await Assert.That(unhandled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_last_module_in_a_branch_handles_errors_it_takes()
    {
        var (builder, unhandled) = NewBuilder();
        var passOn = builder.Add(() => new Map<Message>((m, context) => context.Emit(m)));
        builder.Chain(builder.AddSource(() => new Numbers(1, 2)), builder.Add(() => ThrowsOn(2)), passOn);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(pipeline.GetStats(passOn).Handled).IsEqualTo(2);
        await Assert.That(unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_error_turned_into_another_message_is_handled()
    {
        var (builder, unhandled) = NewBuilder();
        var words = new Collect<Word>();
        var describe = builder.Add(() => new Map<ErrorMessage>((e, context) => context.Emit(new Word($"failed: {e.Error}"))), options: ErrorsOnly);
        builder.Chain(builder.AddSource(() => new Numbers(1, 2)), builder.Add(() => ThrowsOn(2)), describe, builder.Add(() => words));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(words.Received.Single().Value).IsEqualTo("failed: boom 2");
        await Assert.That(words.Received.Single().Parent).IsTypeOf<ErrorMessage>();
        await Assert.That(unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Errors_skip_modules_that_do_not_take_them()
    {
        var (builder, unhandled) = NewBuilder();
        var errors = new Collect<ErrorMessage>();
        var numbersOnly = builder.Add(() => new Map<Number>((n, context) => context.Emit(n)));
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2, 3)),
            builder.Add(() => ThrowsOn(2)),
            numbersOnly,
            builder.Add(() => errors, options: ErrorsOnly));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(errors.Received.Count).IsEqualTo(1);
        await Assert.That(pipeline.GetStats(numbersOnly).Handled).IsEqualTo(2);
        await Assert.That(unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_errors_only_module_receives_only_errors()
    {
        var (builder, unhandled) = NewBuilder();
        var tap = builder.Add(
            () => new Map<Message>((m, context) => context.Emit(m)),
            options: new NodeOptions { Errors = ErrorDelivery.Only });
        builder.Chain(builder.AddSource(() => new Numbers(1, 2, 3)), builder.Add(() => ThrowsOn(2)), tap);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(pipeline.GetStats(tap).Handled).IsEqualTo(1);
        await Assert.That(pipeline.GetStats(tap).Filtered).IsEqualTo(2);
        await Assert.That(unhandled.Count).IsEqualTo(0); // it's the last module, so it handled the error
    }

    [Test]
    public async Task An_errors_only_module_that_passes_errors_on_does_not_handle_them()
    {
        var (builder, unhandled) = NewBuilder();
        var tap = builder.Add(
            () => new Map<Message>((m, context) => context.Emit(m)),
            options: new NodeOptions { Errors = ErrorDelivery.Only });
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2, 3)),
            builder.Add(() => ThrowsOn(2)),
            tap,
            builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(unhandled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_module_can_be_kept_away_from_errors()
    {
        var (builder, unhandled) = NewBuilder();
        var everything = new Collect<Message>();
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2, 3)),
            builder.Add(() => ThrowsOn(2)),
            builder.Add(() => everything, options: new NodeOptions { Errors = ErrorDelivery.Exclude }));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(everything.Received.All(m => m is Number)).IsTrue();
        await Assert.That(everything.Received.Count).IsEqualTo(2);
        await Assert.That(unhandled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_passthrough_module_at_the_end_of_a_branch_handles_errors_it_takes()
    {
        var (builder, unhandled) = NewBuilder();
        var recorder = new Recorder<Message>();
        builder.Chain(builder.AddSource(() => new Numbers(1, 2)), builder.Add(() => ThrowsOn(2)), builder.Add(() => recorder));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(recorder.Handled.OfType<ErrorMessage>().Count()).IsEqualTo(1);
        await Assert.That(unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_passthrough_module_passes_errors_on_to_the_modules_after_it()
    {
        var (builder, unhandled) = NewBuilder();
        var recorder = new Recorder<Message>();
        builder.Chain(
            builder.AddSource(() => new Numbers(1, 2)),
            builder.Add(() => ThrowsOn(2)),
            builder.Add(() => recorder),
            builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(recorder.Handled.OfType<ErrorMessage>().Count()).IsEqualTo(1);
        await Assert.That(unhandled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_source_that_throws_with_restarts_off_reports_the_error_and_finishes()
    {
        var (builder, unhandled) = NewBuilder(ErrorPolicy.Default with { RestartSources = null });
        builder.Connect(builder.AddSource(() => new FailingSource()), builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(unhandled.Single().Error).IsEqualTo("source broke");
    }

    private sealed class FailingSource : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("source broke");
    }
}
