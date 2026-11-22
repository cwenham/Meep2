namespace Meep2.Meeplang.Syntax;

/// <summary>How serious a diagnostic is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The pipeline can't load.</summary>
    Error,

    /// <summary>The pipeline loads, but something may not work as intended.</summary>
    Warning,
}

/// <summary>A problem found while reading or loading a pipeline.</summary>
/// <param name="Severity">Whether it stops the pipeline loading.</param>
/// <param name="Message">What's wrong, in words.</param>
/// <param name="Span">Where it is.</param>
/// <param name="Path">The file it's in.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, SourceSpan Span, string? Path)
{
    /// <summary>Whether this stops the pipeline loading.</summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;

    /// <summary>The diagnostic in the usual compiler format, which editors can link to: <c>file(line,column): error: …</c>.</summary>
    public override string ToString() =>
        $"{Span.File ?? Path ?? "pipeline"}({Span.Line},{Span.Column}): {(IsError ? "error" : "warning")}: {Message}";
}
