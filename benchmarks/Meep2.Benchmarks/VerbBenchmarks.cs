using BenchmarkDotNet.Attributes;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// The cost of a verb choosing between inflections: source → verb with two inflections → sink, against the
/// one-step chain in <see cref="ChainBenchmarks"/>. Times and allocations are per message, end to end.
/// </summary>
[MemoryDiagnoser]
public class VerbBenchmarks
{
    /// <summary>
    /// False: the verb chooses by type alone, so every message goes to the closest fit. True: the closest fit's
    /// parameters refuse every other message, which then goes to the next inflection, so each message is asked about.
    /// </summary>
    [Params(false, true)]
    public bool ChooseByParameters { get; set; }

    [Benchmark(OperationsPerInvoke = ChainBenchmarks.MessagesPerRun)]
    public async Task Verb()
    {
        var builder = new PipelineBuilder("Verb", new PipelineOptions { BufferSize = ChainBenchmarks.MessagesPerRun });
        var source = builder.AddSource(() => new ChainBenchmarks.Counter(ChainBenchmarks.MessagesPerRun));
        Func<Message, string?>? odd = ChooseByParameters ? m => ((Count)m).Value % 2 != 0 ? null : "takes odd counts" : null;
        var verb = builder.AddVerb("Route",
        [
            new VerbInflection("Odd", () => new ChainBenchmarks.PassOn(), odd),
            new VerbInflection("Any", () => new PassOnAnything()),
        ]);
        var sink = new ChainBenchmarks.Sink();
        builder.Chain(source, verb, builder.Add(() => sink));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(ChainBenchmarks.MessagesPerRun);
    }

    private sealed class PassOnAnything : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }
    }
}
