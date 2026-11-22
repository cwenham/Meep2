using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;

namespace Meep2.Meeplang.Loading;

/// <summary>The outcome of loading a pipeline: the pipeline if it loaded, and any problems found.</summary>
/// <param name="Pipeline">The pipeline, ready to run, or null if there were errors.</param>
/// <param name="Diagnostics">Errors and warnings from parsing and loading.</param>
public sealed record LoadResult(Pipeline? Pipeline, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether the pipeline loaded.</summary>
    public bool Succeeded => Pipeline is not null;
}

/// <summary>
/// Turns a parsed Meeplang file into a pipeline: finds each verb's inflections, fills in parameters, connects
/// modules, and checks expressions against the message types that can reach them.
/// </summary>
public sealed class PipelineLoader(ModuleRegistry registry)
{
    /// <summary>Loads a parsed file. Problems are reported in the result, not thrown.</summary>
    /// <param name="syntax">The parsed file.</param>
    /// <param name="options">Settings for the pipeline; the file's <c>defaults</c> block overrides them.</param>
    public LoadResult Load(PipelineSyntax syntax, PipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var diagnostics = new Diagnostics(syntax.Source.Path);
        foreach (var diagnostic in syntax.Diagnostics)
            diagnostics.Add(diagnostic);
        if (diagnostics.HasErrors)
            return new LoadResult(null, diagnostics.Items);

        var load = new Load(registry, syntax, diagnostics);
        var pipeline = load.Run(options ?? PipelineOptions.Default);
        var sorted = diagnostics.Items.OrderBy(d => d.Span.Line).ThenBy(d => d.Span.Column).ToList();
        return new LoadResult(diagnostics.HasErrors ? null : pipeline, sorted);
    }

    /// <summary>
    /// Loads a parsed file as far as a builder, knowing which module of it each module line became, so a running
    /// pipeline can be changed into it (goal 4). Null, with the problems, if it doesn't load.
    /// </summary>
    internal WithDiagnostics<LoadedModules> Define(PipelineSyntax syntax, PipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var diagnostics = new Diagnostics(syntax.Source.Path);
        foreach (var diagnostic in syntax.Diagnostics)
            diagnostics.Add(diagnostic);
        LoadedModules? modules = null;
        if (!diagnostics.HasErrors)
        {
            var load = new Load(registry, syntax, diagnostics);
            if (load.Prepare(options ?? PipelineOptions.Default) && !diagnostics.HasErrors)
                modules = load.Define(load.Options);
        }
        var sorted = diagnostics.Items.OrderBy(d => d.Span.Line).ThenBy(d => d.Span.Column).ToList();
        return new(diagnostics.HasErrors ? null : modules, sorted);
    }
}
