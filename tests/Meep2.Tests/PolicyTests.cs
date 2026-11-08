using System.Collections.Concurrent;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>The error policy: logging, stopping a branch or the pipeline, and restarting sources (Q17, Q79).</summary>
public class PolicyTests
{
    private sealed class Run
    {
        public ConcurrentQueue<ErrorMessage> Unhandled { get; } = new();

        public ConcurrentQueue<string> Notices { get; } = new();

        public PipelineBuilder Builder(ErrorPolicy policy) =>
            new(options: new PipelineOptions { UnhandledError = Unhandled.Enqueue, Notice = Notices.Enqueue, ErrorPolicy = policy });
    }

    private static Map<Number> Fails() => new((n, context) => throw new InvalidOperationException($"boom {n.Value}"));

    [Test]
    public async Task Errors_a_policy_ignores_are_not_logged()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { LogUnhandled = false });
        builder.Connect(builder.AddSource(() => new Numbers(1, 2)), builder.Add(Fails));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(run.Unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_modules_own_policy_applies_to_its_errors()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default);
        var source = builder.AddSource(() => new Numbers(1));
        var quiet = builder.Add(Fails, name: "Quiet", options: new NodeOptions { ErrorPolicy = ErrorPolicy.Default with { LogUnhandled = false } });
        var loud = builder.Add(Fails, name: "Loud");
        builder.Connect(source, quiet);
        builder.Connect(source, loud);

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(run.Unhandled.Single().EmittedBy).IsSameReferenceAs(loud);
    }

    [Test]
    public async Task A_module_that_fails_too_often_stops_its_branch()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { StopBranch = new FailureLimit(3, TimeSpan.FromSeconds(10)) });
        var after = new Collect<Message>();
        var failing = builder.Add(Fails);
        builder.Chain(builder.AddSource(() => new Numbers(1, 2, 3, 4, 5, 6)), failing, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var stats = pipeline.GetStats(failing);
        await Assert.That(stats.Failed).IsEqualTo(3);
        await Assert.That(stats.Dropped).IsEqualTo(3);
        await Assert.That(after.Received.Count).IsEqualTo(3); // the three errors
        await Assert.That(run.Notices.Single()).IsEqualTo(
            "Stopped Map #1 and the branch below it, because it failed 3 times within 10s. Messages that reach it are dropped from now on.");
    }

    [Test]
    public async Task The_pipeline_stops_when_too_many_messages_from_its_sources_end_in_unhandled_errors()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { StopPipelineAtPercent = 50, StopBranch = null });
        builder.Connect(
            builder.AddSource(() => new Ticker(TimeSpan.FromMilliseconds(1))),
            builder.Add(() => new Map<Number>((n, context) =>
            {
                if (n.Value % 3 != 0)
                    context.EmitError("bad");
            })));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        // Two in three fail, which reaches 50% once 20 messages have been sent.
        await Assert.That(pipeline.StoppedBecause).IsNotNull();
        await Assert.That(pipeline.StoppedBecause!).Contains("messages from its sources ended in errors nothing handled, which reaches its limit of 50%");
        await Assert.That(run.Notices.Single()).StartsWith("Stopping the pipeline, because ");
    }

    [Test]
    public async Task A_source_that_throws_is_restarted_after_a_delay()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { RestartSources = new SourceRestart(TimeSpan.FromMilliseconds(10), 3) });
        var runs = 0;
        var collect = new Collect<Number>();
        var source = builder.AddSource(() => new Scripted(Interlocked.Increment(ref runs), emits: run => run > 1, fails: run => run == 1));
        builder.Connect(source, builder.Add(() => collect));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(collect.Received.Select(n => n.Value))).IsEqualTo("2");
        await Assert.That(pipeline.GetStats(source).Restarts).IsEqualTo(1);
        await Assert.That(run.Unhandled.Single().Error).IsEqualTo("run 1 broke");
    }

    [Test]
    public async Task A_source_that_keeps_failing_without_emitting_is_given_up_on()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { RestartSources = new SourceRestart(TimeSpan.FromMilliseconds(1), 3) });
        var runs = 0;
        var source = builder.AddSource(() => new Scripted(Interlocked.Increment(ref runs), emits: _ => false, fails: _ => true));
        builder.Connect(source, builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(pipeline.GetStats(source).Failed).IsEqualTo(3);
        await Assert.That(pipeline.GetStats(source).Restarts).IsEqualTo(2);
        await Assert.That(run.Notices.Single()).IsEqualTo("Gave up on Scripted #1: it failed 3 times in a row without emitting anything else.");
    }

    [Test]
    public async Task A_source_that_emits_between_failures_starts_the_count_again()
    {
        var run = new Run();
        var builder = run.Builder(ErrorPolicy.Default with { RestartSources = new SourceRestart(TimeSpan.FromMilliseconds(1), 2) });
        var runs = 0;
        var source = builder.AddSource(() => new Scripted(Interlocked.Increment(ref runs), emits: _ => true, fails: run => run < 5));
        builder.Connect(source, builder.Add(() => new Collect<Number>()));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        // Each run emits before it fails, so two failures in a row never happen, and the fifth run finishes.
        await Assert.That(pipeline.GetStats(source).Failed).IsEqualTo(4);
        await Assert.That(pipeline.GetStats(source).Restarts).IsEqualTo(4);
        await Assert.That(run.Notices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Policy_values_are_checked()
    {
        await Assert.That(() => new FailureLimit(0, TimeSpan.FromSeconds(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SourceRestart(TimeSpan.FromSeconds(-1), 3)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ErrorPolicy.Default with { StopPipelineAtPercent = 120 }).Throws<ArgumentOutOfRangeException>();
    }

    // A source for one run, numbered from 1: it may emit a Number with its run's number, then fails or finishes.
    private sealed class Scripted(int run, Func<int, bool> emits, Func<int, bool> fails) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            if (emits(run))
                context.Emit(new Number(run));
            return fails(run) ? throw new InvalidOperationException($"run {run} broke") : Task.CompletedTask;
        }
    }
}
