using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Parsing;

/// <summary>Collects diagnostics for one file.</summary>
internal sealed class Diagnostics(string? path)
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>
    /// The file to record in the spans of tokens lexed with these diagnostics: set for an included file, so its
    /// templates still point at it once copied into a pipeline.
    /// </summary>
    public string? SpanFile { get; init; }

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.IsError);

    public void Error(string message, SourceSpan span) => _items.Add(new Diagnostic(DiagnosticSeverity.Error, message, span, path));

    public void Warning(string message, SourceSpan span) => _items.Add(new Diagnostic(DiagnosticSeverity.Warning, message, span, path));

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);
}
