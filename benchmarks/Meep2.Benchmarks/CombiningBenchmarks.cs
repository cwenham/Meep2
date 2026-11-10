using BenchmarkDotNet.Attributes;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using static Meep2.Benchmarks.ChainBenchmarks;

namespace Meep2.Benchmarks;

/// <summary>
/// What combining costs, per message from the source: Batch gathering 100 at a time, Zip pairing two sources, and
/// Distribute spreading one stream across four copies of a step, each handled by its own loop (step 9).
/// </summary>
[MemoryDiagnoser]
public class CombiningBenchmarks
{
    [Benchmark(OperationsPerInvoke = MessagesPerRun)]
    public async Task Batch100()
    {
        var builder = new PipelineBuilder("Batch", new PipelineOptions { BufferSize = MessagesPerRun });
        var sink = new Items();
        builder.Chain(
            builder.AddSource(() => new Counter(MessagesPerRun)),
            builder.Add(() => new Batch(new BatchParameters { Size = 100 })),
            builder.Add(() => sink));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(MessagesPerRun);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerRun)]
    public async Task ZipInOrder()
    {
        var builder = new PipelineBuilder("Zip", new PipelineOptions { BufferSize = MessagesPerRun });
        var sink = new Pairs();
        var zip = builder.Add(() => new Zip(new ZipParameters()));
        builder.Connect(builder.AddSource(() => new Counter(MessagesPerRun / 2)), zip);
        builder.Connect(builder.AddSource(() => new Counter(MessagesPerRun / 2)), zip);
        builder.Connect(zip, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(MessagesPerRun / 2);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerRun)]
    public async Task Distribute4()
    {
        var builder = new PipelineBuilder("Distribute", new PipelineOptions { BufferSize = MessagesPerRun });
        var sink = new Sink();
        var distribute = builder.Add(() => new Distribute(new DistributeParameters { Copies = 4, By = Computed<object?>.From(m => ((Count)m).Value % 16) }));
        builder.Connect(builder.AddSource(() => new Counter(MessagesPerRun)), distribute);
        var end = builder.Add(() => sink);
        for (var lane = 0; lane < 4; lane++)
        {
            var step = builder.Add(() => new Increment());
            builder.Connect(distribute, step).Connect(step, end);
        }
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(MessagesPerRun);
    }

    private sealed class Items : AInflection<Batched>
    {
        private int _items;

        public void Check(int expected)
        {
            if (_items != expected)
                throw new InvalidOperationException($"Expected {expected} items but {_items} arrived.");
        }

        protected override ValueTask HandleAsync(Batched message, ModuleContext context, CancellationToken cancellationToken)
        {
            _items += message.Count;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Pairs : AInflection<Zipped>
    {
        private int _pairs;

        public void Check(int expected)
        {
            if (_pairs != expected)
                throw new InvalidOperationException($"Expected {expected} pairs but {_pairs} arrived.");
        }

        protected override ValueTask HandleAsync(Zipped message, ModuleContext context, CancellationToken cancellationToken)
        {
            _pairs++;
            return ValueTask.CompletedTask;
        }
    }
}
