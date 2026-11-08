using BenchmarkDotNet.Attributes;
using Meep2.Runtime;
using static Meep2.Benchmarks.ChainBenchmarks;

namespace Meep2.Benchmarks;

/// <summary>Throughput when one source feeds four branches. Times and allocations are per source message.</summary>
[MemoryDiagnoser]
public class BranchBenchmarks
{
    [Benchmark(OperationsPerInvoke = MessagesPerRun)]
    public async Task FourBranches()
    {
        var builder = new PipelineBuilder("Branches", new PipelineOptions { BufferSize = MessagesPerRun });
        var source = builder.AddSource(() => new Counter(MessagesPerRun));
        var sinks = new Sink[4];
        for (var i = 0; i < sinks.Length; i++)
        {
            var sink = sinks[i] = new Sink();
            builder.Connect(source, builder.Add(() => sink));
        }

        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        foreach (var sink in sinks)
            sink.Check(MessagesPerRun);
    }
}
