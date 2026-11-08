using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Expired messages stop at the next module, and a module's token fires at the deadline (Q12, Q78).</summary>
public class ExpiryTests
{
    private static Deadline Past => Deadline.After(TimeSpan.FromSeconds(-1));

    [Test]
    public async Task An_expired_message_stops_at_the_next_module()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        var sink = builder.Add(() => collect);
        builder.Connect(builder.AddSource(() => new Messages(new Number(1) { Expires = Past }, new Number(2))), sink);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo("2");
        await Assert.That(pipeline.GetStats(sink).Expired).IsEqualTo(1);
        await Assert.That(pipeline.GetStats(sink).Received).IsEqualTo(1);
    }

    [Test]
    public async Task A_module_that_accepts_expired_messages_still_receives_them()
    {
        var builder = new PipelineBuilder();
        var collect = new Collect<Number>();
        builder.Connect(
            builder.AddSource(() => new Messages(new Number(1) { Expires = Past }, new Number(2))),
            builder.Add(() => collect, options: new NodeOptions { AcceptExpired = true }));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo("1,2");
    }

    [Test]
    public async Task Errors_never_expire_so_they_are_always_reported()
    {
        var builder = new PipelineBuilder();
        var errors = new Collect<ErrorMessage>();
        var failing = builder.Add(() => new Map<Number>((n, context) => context.EmitError("bad")), options: new NodeOptions { AcceptExpired = true });
        builder.Chain(builder.AddSource(() => new Messages(new Number(1) { Expires = Past })), failing, builder.Add(() => errors));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(errors.Received.Single().Error).IsEqualTo("bad");
    }

    [Test]
    public async Task A_tracked_message_that_expires_is_reported_as_expired()
    {
        var builder = new PipelineBuilder();
        var feed = new Feed();
        builder.Connect(builder.AddSource(() => feed), builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        var outcome = await Wait.For(feed.Send(new Number(1) { Expires = Past }));
        feed.Finish();
        await Wait.For(running);

        await Assert.That(outcome).IsEqualTo(MessageOutcome.Expired);
    }

    [Test]
    public async Task A_modules_token_fires_when_its_message_expires()
    {
        var builder = new PipelineBuilder();
        var feed = new Feed();
        var patient = builder.Add(() => new Waiter());
        builder.Connect(builder.AddSource(() => feed), patient);

        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        var outcome = await Wait.For(feed.Send(new Number(1) { Expires = Deadline.After(TimeSpan.FromMilliseconds(100)) }));
        feed.Finish();
        await Wait.For(running);

        await Assert.That(outcome).IsEqualTo(MessageOutcome.Expired);
        await Assert.That(pipeline.GetStats(patient).Expired).IsEqualTo(1);
        await Assert.That(pipeline.GetStats(patient).Failed).IsEqualTo(0);
    }

    [Test]
    public async Task Expires_sets_the_expiry_of_what_a_module_makes()
    {
        var builder = new PipelineBuilder();
        var words = new Collect<Word>();
        var toWords = builder.Add(
            () => new Map<Number>((n, context) => context.Emit(new Word($"{n.Value}"))),
            options: new NodeOptions { Expires = TimeSpan.FromHours(1) });
        builder.Chain(builder.AddSource(() => new Numbers(1)), toWords, builder.Add(() => words));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var remaining = words.Received.Single().Expires!.Value.Remaining;
        await Assert.That(remaining > TimeSpan.FromMinutes(59) && remaining <= TimeSpan.FromHours(1)).IsTrue();
    }

    [Test]
    public async Task Expires_none_clears_expiry_a_message_would_inherit()
    {
        var builder = new PipelineBuilder();
        var words = new Collect<Word>();
        var toWords = builder.Add(
            () => new Map<Number>((n, context) => context.Emit(new Word($"{n.Value}"))),
            options: new NodeOptions { Expires = Timeout.InfiniteTimeSpan });
        builder.Chain(
            builder.AddSource(() => new Messages(new Number(1) { Expires = Deadline.After(TimeSpan.FromHours(1)) })),
            toWords,
            builder.Add(() => words));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(words.Received.Single().Expires).IsNull();
    }

    [Test]
    public async Task The_pipelines_default_expiry_applies_to_messages_from_sources()
    {
        var builder = new PipelineBuilder(options: new PipelineOptions { Expires = TimeSpan.FromHours(1) });
        var collect = new Collect<Number>();
        builder.Connect(
            builder.AddSource(() => new Messages(new Number(1), new Number(2) { Expires = Deadline.After(TimeSpan.FromMinutes(5)) })),
            builder.Add(() => collect));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var first = collect.Received.Single(n => n.Value == 1).Expires!.Value.Remaining;
        var second = collect.Received.Single(n => n.Value == 2).Expires!.Value.Remaining;
        await Assert.That(first > TimeSpan.FromMinutes(59)).IsTrue();
        await Assert.That(second <= TimeSpan.FromMinutes(5)).IsTrue(); // a source's own expiry wins
    }

    // Waits on its token, which only expiry will cancel.
    private sealed class Waiter : AInflection<Number>
    {
        protected override async ValueTask HandleAsync(Number message, ModuleContext context, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
