using BenchmarkDotNet.Attributes;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// Throughput of the push core: a source emits messages as fast as it can into a chain of modules. Times and
/// allocations are per message, end to end.
/// </summary>
/// <remarks>
/// Buffers are as big as the run, because a source this fast would otherwise fill them and the oldest messages would
/// be dropped by design. Each run checks that every message arrived.
/// </remarks>
[MemoryDiagnoser]
public class ChainBenchmarks
{
    internal const int MessagesPerRun = 100_000;

    /// <summary>How many modules sit between the source and the sink.</summary>
    [Params(1, 4)]
    public int Steps { get; set; }

    /// <summary>Whether each step makes a new message, or passes on the one it was given.</summary>
    [Params(false, true)]
    public bool NewMessageEachStep { get; set; }

    [Benchmark(OperationsPerInvoke = MessagesPerRun)]
    public async Task Chain()
    {
        var builder = new PipelineBuilder("Chain", new PipelineOptions { BufferSize = MessagesPerRun });
        var previous = builder.AddSource(() => new Counter(MessagesPerRun));
        for (var i = 0; i < Steps; i++)
        {
            var step = NewMessageEachStep ? builder.Add(() => new Increment()) : builder.Add(() => new PassOn());
            builder.Connect(previous, step);
            previous = step;
        }
        var sink = new Sink();
        builder.Connect(previous, builder.Add(() => sink));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(MessagesPerRun);
    }

    internal sealed class Counter(int count) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
                context.Emit(new Count(i));
            return Task.CompletedTask;
        }
    }

    internal sealed class PassOn : AInflection<Count>
    {
        protected override ValueTask HandleAsync(Count message, ModuleContext context, CancellationToken cancellationToken)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Increment : AInflection<Count>
    {
        protected override ValueTask HandleAsync(Count message, ModuleContext context, CancellationToken cancellationToken)
        {
            context.Emit(message with { Value = message.Value + 1 });
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Sink : AInflection<Count>
    {
        private int _received;

        public void Check(int expected)
        {
            if (_received != expected)
                throw new InvalidOperationException($"Expected {expected} messages but {_received} arrived.");
        }

        protected override ValueTask HandleAsync(Count message, ModuleContext context, CancellationToken cancellationToken)
        {
            _received++;
            return ValueTask.CompletedTask;
        }
    }
}
