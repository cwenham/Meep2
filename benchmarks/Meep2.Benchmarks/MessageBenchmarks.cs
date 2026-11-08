using BenchmarkDotNet.Attributes;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>The cost of making messages and looking up their ancestors.</summary>
[MemoryDiagnoser]
public class MessageBenchmarks
{
    private Count _message = null!;
    private Message _twentyDeep = null!;

    [GlobalSetup]
    public void Setup()
    {
        _message = new Count(1);

        var root = new ModuleInfo(1, "Root", null);
        var step = new ModuleInfo(2, "Step", null);
        Message message = new Count(0) { };
        message.EmittedBy = root;
        for (var i = 1; i <= 20; i++)
        {
            var child = new Count(i) { Parents = new Parents(message) };
            child.EmittedBy = step;
            message = child;
        }
        _twentyDeep = message;
    }

    /// <summary>A new message: an Id, a timestamp and an allocation.</summary>
    [Benchmark]
    public Count Create() => new(1);

    /// <summary>A copy made with <c>with</c>, which is a child of the original.</summary>
    [Benchmark]
    public Count Copy() => _message with { Value = 2 };

    /// <summary>Finding an ancestor 20 generations up.</summary>
    [Benchmark]
    public Message? FamTwentyGenerationsUp() => _twentyDeep.Fam["Root"];
}
