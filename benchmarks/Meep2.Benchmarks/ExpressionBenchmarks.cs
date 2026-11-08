using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// The cost of working out Meeplang expressions for one message: typed, where the member is known when the pipeline
/// loads, against dynamic, where a JSON member is looked up as each message arrives.
/// </summary>
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    private CompiledComputed<bool> _typedCondition = null!;
    private CompiledComputed<bool> _dynamicCondition = null!;
    private CompiledComputed<string> _typedTemplate = null!;
    private CompiledComputed<string> _dynamicTemplate = null!;
    private CompiledComputed<bool> _typedSum = null!;
    private CompiledComputed<bool> _dynamicSum = null!;
    private CompiledComputed<bool> _bigSum = null!;
    private Count _count = null!;
    private JsonMessage _json = null!;
    private JsonMessage _bigJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _count = new Count(12);
        using var document = JsonDocument.Parse("""{ "route": "7", "minutesAway": 12 }""");
        _json = new JsonMessage(document.RootElement.Clone());
        using var big = JsonDocument.Parse("""{ "total": 1234567890123456789012345678901234567890 }""");
        _bigJson = new JsonMessage(big.RootElement.Clone());

        _typedCondition = Compile<bool>("msg.Value < 30");
        _dynamicCondition = Compile<bool>("msg.minutesAway < 30");
        _typedTemplate = Compile<string>("\"{msg.Value} min\"");
        _dynamicTemplate = Compile<string>("\"{msg.route} in {msg.minutesAway} min\"");
        _typedSum = Compile<bool>("msg.Value * 2 + 1 > 20");
        _dynamicSum = Compile<bool>("msg.minutesAway * 2 + 1 > 20");
        _bigSum = Compile<bool>("msg.total * 2 + 1 > 20");

        // Compile for each message type before measuring.
        _typedCondition.Evaluate(_count);
        _dynamicCondition.Evaluate(_json);
        _typedTemplate.Evaluate(_count);
        _dynamicTemplate.Evaluate(_json);
        _typedSum.Evaluate(_count);
        _dynamicSum.Evaluate(_json);
        _bigSum.Evaluate(_bigJson);
    }

    [Benchmark]
    public bool TypedCondition() => _typedCondition.Evaluate(_count);

    [Benchmark]
    public bool DynamicJsonCondition() => _dynamicCondition.Evaluate(_json);

    [Benchmark]
    public string TypedTemplate() => _typedTemplate.Evaluate(_count);

    [Benchmark]
    public string DynamicJsonTemplate() => _dynamicTemplate.Evaluate(_json);

    /// <summary>Sums in decimal on a typed member.</summary>
    [Benchmark]
    public bool TypedSum() => _typedSum.Evaluate(_count);

    /// <summary>Sums in decimal on a JSON member.</summary>
    [Benchmark]
    public bool DynamicJsonSum() => _dynamicSum.Evaluate(_json);

    /// <summary>Sums on a JSON number too big for decimal, so they're done with big numbers.</summary>
    [Benchmark]
    public bool BigJsonSum() => _bigSum.Evaluate(_bigJson);

    /// <summary>Parsing and loading a small pipeline file, including compiling its expressions.</summary>
    [Benchmark]
    public object? ParseAndLoad()
    {
        var syntax = MeeplangParser.Parse(new SourceFile("bench.meep", """
            const Limit = 30
            Interval 1m
                Where msg.Count < Limit
                    WriteLine "tick {msg.Count} at {msg.Created:HH:mm}"
            """));
        return new PipelineLoader(s_registry).Load(syntax).Pipeline;
    }

    private static readonly ModuleRegistry s_registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);

    private static CompiledComputed<T> Compile<T>(string text)
    {
        var syntax = MeeplangParser.ParseExpression(text, out _)!;
        var scope = new CompileScope(new Dictionary<string, ConstantValue>(), _ => null, _ => null, []);
        return new CompiledComputed<T>(syntax, scope, text);
    }
}
