using System.Collections.Concurrent;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Leases on live resources, counted by the messages in flight that carry them (Q9, Q48).</summary>
public class LeaseTests
{
    private sealed class TestLease : Lease
    {
        private int _closes;

        public int Closes => Volatile.Read(ref _closes);

        public Exception? FailOnClose { get; init; }

        protected override ValueTask OnClosedAsync()
        {
            Interlocked.Increment(ref _closes);
            return FailOnClose is { } failure ? throw failure : ValueTask.CompletedTask;
        }
    }

    // Emits each message with the same lease. A lease's maker only holds it until its first emit, so with several it
    // holds the lease itself until it has emitted them all (step 5); otherwise the first could finish and close the
    // lease before the next went out.
    private sealed class Leasing(Lease lease, params Message[] messages) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            using var hold = messages.Length > 1 ? lease.Hold() : null;
            foreach (var message in messages)
                context.Emit(message, lease);
            return Task.CompletedTask;
        }
    }

    // Records whether the lease was still open when each message arrived.
    private sealed class Observer(Lease lease) : AInflection<Message>
    {
        public ConcurrentQueue<(Message Message, bool Open)> Seen { get; } = new();

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
        {
            Seen.Enqueue((message, !lease.IsClosed));
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task A_lease_closes_once_the_last_message_carrying_it_has_left()
    {
        var lease = new TestLease();
        var observer = new Observer(lease);
        var builder = new PipelineBuilder();
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Number(1))),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                context.Emit(new Word("a"));
                context.Emit(new Word("b"));
            })),
            builder.Add(() => observer));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed);

        await Assert.That(observer.Seen.Count).IsEqualTo(2);
        await Assert.That(observer.Seen.All(seen => seen.Open)).IsTrue();
        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task Messages_made_from_a_leased_message_carry_its_lease()
    {
        var lease = new TestLease();
        var observer = new Observer(lease);
        var builder = new PipelineBuilder();
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Number(1))),
            builder.Add(() => new Map<Number>((n, context) => context.Emit(new Word($"{n.Value}")))),
            builder.Add(() => observer));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed); // it closes in the background, once its last message has finished

        var word = observer.Seen.Single().Message;
        await Assert.That(word.Leases.Single()).IsSameReferenceAs(lease);
        await Assert.That(lease.ToString()).IsEqualTo($"lease {lease.Number} (closed)");
    }

    [Test]
    public async Task A_lease_closes_after_the_last_of_several_messages_it_went_out_with()
    {
        var lease = new TestLease();
        var observer = new Observer(lease);
        var builder = new PipelineBuilder();
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Number(1), new Number(2), new Number(3))),
            builder.Add(() => observer));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed);

        await Assert.That(observer.Seen.Count).IsEqualTo(3);
        await Assert.That(observer.Seen.All(seen => seen.Open)).IsTrue();
        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task A_module_holds_a_lease_it_made_until_it_has_finished_its_message()
    {
        // Q123: a module that emits several messages with one lease needs no Hold() of its own, however quickly the
        // first is handled.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var lease = new TestLease();
            var observer = new Observer(lease);
            var builder = new PipelineBuilder();
            builder.Chain(
                builder.AddSource(() => new Numbers(1)),
                builder.Add(() => new Map<Number>(async (n, context) =>
                {
                    context.Emit(new Word("a"), lease);
                    await Task.Delay(1); // the first can be handled meanwhile
                    context.Emit(new Word("b"), lease);
                })),
                builder.Add(() => observer));

            await using var pipeline = builder.Build();
            await Wait.For(pipeline.RunAsync());
            await Wait.For(lease.Closed);

            await Assert.That(observer.Seen.Count).IsEqualTo(2);
            await Assert.That(observer.Seen.All(seen => seen.Open)).IsTrue();
            await Assert.That(lease.Closes).IsEqualTo(1);
        }
    }

    [Test]
    public async Task A_lease_still_closes_when_nothing_takes_its_message()
    {
        var lease = new TestLease();
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = _ => { } });
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Word("not a number"))),
            builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Wait.For(lease.Closed);

        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task A_hold_keeps_a_lease_open_after_its_messages_have_gone()
    {
        var lease = new TestLease();
        var hold = lease.Hold();
        var builder = new PipelineBuilder();
        builder.Chain(builder.AddSource(() => new Leasing(lease, new Number(1))), builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Task.Delay(50);

        await Assert.That(lease.IsClosed).IsFalse();
        hold.Dispose();
        await Wait.For(lease.Closed);
        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task A_lease_closed_on_purpose_is_closed_for_the_modules_after()
    {
        var lease = new TestLease();
        var observer = new Observer(lease);
        var builder = new PipelineBuilder();
        builder.Chain(
            builder.AddSource(() => new Leasing(lease, new Number(1))),
            builder.Add(() => new Map<Number>(async (n, context) =>
            {
                await lease.CloseAsync();
                context.Emit(n);
            })),
            builder.Add(() => observer));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Task.Delay(50);

        await Assert.That(observer.Seen.Single().Open).IsFalse();
        await Assert.That(lease.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task A_lease_that_fails_to_close_is_reported_as_coming_from_the_module_that_emitted_it()
    {
        var unhandled = new ConcurrentQueue<ErrorMessage>();
        var lease = new TestLease { FailOnClose = new IOException("socket broke") };
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = unhandled.Enqueue });
        var source = builder.AddSource(() => new Leasing(lease, new Number(1)));
        builder.Connect(source, builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        await Wait.Until(() => !unhandled.IsEmpty);

        var error = unhandled.Single();
        await Assert.That(error.Error).IsEqualTo($"Couldn't close lease {lease.Number}: socket broke");
        await Assert.That(error.EmittedBy).IsSameReferenceAs(source);
    }

    [Test]
    public async Task Only_a_new_message_can_carry_a_lease()
    {
        var lease = new TestLease();
        var failure = new TaskCompletionSource<Exception>();
        var builder = new PipelineBuilder();
        builder.Chain(
            builder.AddSource(() => new Numbers(1)),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                try
                {
                    context.Emit(n, lease);
                }
                catch (ArgumentException exception)
                {
                    failure.TrySetResult(exception);
                }
            })));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That((await Wait.For(failure.Task)).Message).StartsWith("Only a new message can carry a lease");
        await lease.CloseAsync();
    }
}
