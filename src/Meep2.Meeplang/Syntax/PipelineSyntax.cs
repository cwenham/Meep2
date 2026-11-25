namespace Meep2.Meeplang.Syntax;

/// <summary>A parsed Meeplang file.</summary>
public sealed class PipelineSyntax
{
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<PipelineSyntax> _included = [];

    internal PipelineSyntax(
        SourceFile source,
        IReadOnlyList<ArgumentSyntax> defaults,
        IReadOnlyList<ConstantSyntax> constants,
        IReadOnlyList<PluginDirective> plugins,
        IReadOnlyList<ModuleSyntax> modules,
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<TemplateBlock>? templates = null,
        IReadOnlyList<IncludeDirective>? includes = null)
    {
        Source = source;
        Defaults = defaults;
        Constants = constants;
        Plugins = plugins;
        Modules = modules;
        _diagnostics = [.. diagnostics];
        Templates = templates ?? [];
        Includes = includes ?? [];
    }

    /// <summary>The file this came from.</summary>
    public SourceFile Source { get; }

    /// <summary>Settings from the <c>defaults</c> block.</summary>
    public IReadOnlyList<ArgumentSyntax> Defaults { get; }

    /// <summary>The <c>const</c> declarations, in order.</summary>
    public IReadOnlyList<ConstantSyntax> Constants { get; }

    /// <summary>The <c>#plugin</c> lines, in order.</summary>
    public IReadOnlyList<PluginDirective> Plugins { get; }

    /// <summary>The modules written at the left margin, in order, including <c>+</c> lines.</summary>
    public IReadOnlyList<ModuleSyntax> Modules { get; }

    /// <summary>The <c>template</c> blocks, in order.</summary>
    public IReadOnlyList<TemplateBlock> Templates { get; }

    /// <summary>The <c>#include</c> lines, in order.</summary>
    public IReadOnlyList<IncludeDirective> Includes { get; }

    /// <summary>The <c>#permission</c> lines, in order, which narrow the trust store's definitions for this pipeline.</summary>
    public IReadOnlyList<PermissionDirective> Permissions { get; init; } = [];

    /// <summary>The files <c>#include</c> brought in, and those they brought in, parsed, in the order they were met.</summary>
    public IReadOnlyList<PipelineSyntax> Included => _included;

    /// <summary>Problems found while parsing, the included files' among them.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Whether parsing found any errors.</summary>
    public bool HasErrors => _diagnostics.Any(d => d.IsError);

    internal void AddIncluded(PipelineSyntax included)
    {
        _included.Add(included);
        _diagnostics.AddRange(included._diagnostics);
    }

    internal void AddDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>
    /// This file with other modules in place of its own: the modules once templates are copied in, which is what's
    /// built and matched.
    /// </summary>
    internal PipelineSyntax WithModules(IReadOnlyList<ModuleSyntax> modules)
    {
        var copy = new PipelineSyntax(Source, Defaults, Constants, Plugins, modules, [], Templates, Includes) { Permissions = Permissions };
        copy._diagnostics.AddRange(_diagnostics);
        copy._included.AddRange(_included);
        return copy;
    }

    /// <summary>The source text a span covers.</summary>
    public string TextOf(SourceSpan span)
    {
        var lines = Source.Text.Split('\n');
        if (span.Line < 1 || span.Line > lines.Length)
            return "";
        var line = lines[span.Line - 1].TrimEnd('\r');
        var start = Math.Clamp(span.Column - 1, 0, line.Length);
        var length = Math.Clamp(span.Length, 0, line.Length - start);
        return line.Substring(start, length);
    }
}
