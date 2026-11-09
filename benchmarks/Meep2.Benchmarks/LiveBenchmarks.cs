using System.Text;
using BenchmarkDotNet.Attributes;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Runtime;

namespace Meep2.Benchmarks;

/// <summary>
/// What changing a running pipeline costs (goal 4): applying new text to a running pipeline of 20 modules, matching
/// its modules, and editing its text.
/// </summary>
[MemoryDiagnoser]
public class LiveBenchmarks
{
    private LivePipeline _live = null!;
    private Task _running = null!;
    private PipelineSyntax _a = null!;
    private PipelineSyntax _b = null!;
    private bool _toggle;
    private ModuleRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", p => new WriteLine(p, TextWriter.Null));
        _registry = registry;
        _a = Parse(Text(limit: 2));
        _b = Parse(Text(limit: 3));
        _live = LivePipeline.Load(_a, registry).Pipeline!;
        _running = _live.Pipeline.RunAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _live.Pipeline.Stop();
        _running.GetAwaiter().GetResult();
    }

    /// <summary>Loads new text with one module's parameter changed, matches it, and applies it to the running pipeline.</summary>
    [Benchmark]
    public async Task ApplyOneChange()
    {
        _toggle = !_toggle;
        var change = await _live.ChangeAsync(_toggle ? _b : _a);
        if (!change.Applied)
            throw new InvalidOperationException("The change didn't apply.");
    }

    /// <summary>Loads the text, as the change must first: resolving, binding and compiling its expressions.</summary>
    [Benchmark]
    public bool LoadOnly() => new PipelineLoader(_registry).Load(_b).Succeeded;

    /// <summary>Matches the modules of two versions of the text.</summary>
    [Benchmark]
    public int Match() => ModuleMatcher.Match(_a, _b).Count;

    /// <summary>Sets one parameter in the text, keeping its layout, and parses the result.</summary>
    [Benchmark]
    public int EditOneParameter() =>
        SourceEditor.Apply(_a, [new SourceEdit("Where #3", EditKind.Set, null, "msg.Count > 9")]).Modules.Count;

    private static PipelineSyntax Parse(string text) => MeeplangParser.Parse(new SourceFile("live.meep", text));

    // A source with four branches of a Where and three modules each: 20 modules, one of whose parameters changes.
    private static string Text(int limit)
    {
        var text = new StringBuilder("// A pipeline to change while it runs.\nInterval 1h\n");
        for (var branch = 0; branch < 4; branch++)
        {
            text.Append($"    Where msg.Count > {(branch == 0 ? limit : branch)}   // branch {branch}\n");
            text.Append("        Where msg.Count < 1000\n");
            text.Append("            Where msg.Count != 7\n");
            text.Append("                WriteLine \"{msg.Count}\"\n");
        }
        text.Append("    WriteLine \"{msg}\" as Last\n");
        text.Append("    WriteLine \"{msg}\" as Final\n");
        text.Append("    WriteLine \"{msg}\" as End\n");
        return text.ToString();
    }
}
