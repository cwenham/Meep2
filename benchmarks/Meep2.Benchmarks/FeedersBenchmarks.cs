using BenchmarkDotNet.Attributes;
using Meep2.Runtime;

namespace Meep2.Benchmarks;

/// <summary>
/// Several sources feeding one module, each on its own thread (E8): the feeders count the module's messages received
/// while the module counts what it handled and emitted on its own thread. Times and allocations are per message.
/// </summary>
[MemoryDiagnoser]
public class FeedersBenchmarks
{
    private const int Messages = 120_000;

    [Params(1, 4, 8)]
    public int Feeders { get; set; } = 1;

    [Benchmark(OperationsPerInvoke = Messages)]
    public async Task Feed()
    {
        var builder = new PipelineBuilder("Feeders", new PipelineOptions { BufferSize = Messages });
        var passOn = builder.Add(() => new ChainBenchmarks.PassOn());
        for (var i = 0; i < Feeders; i++)
            builder.Connect(builder.AddSource(() => new ChainBenchmarks.Counter(Messages / Feeders)), passOn);
        var sink = new ChainBenchmarks.Sink();
        builder.Connect(passOn, builder.Add(() => sink));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(Messages);
    }
}
