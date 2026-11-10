namespace Meep2.Meeplang.Syntax;

/// <summary>A Meeplang file's text and where it came from.</summary>
/// <param name="Path">The file's path, used in diagnostics.</param>
/// <param name="Text">The file's contents.</param>
public sealed record SourceFile(string Path, string Text);

/// <summary>A place in a source file.</summary>
/// <param name="Line">The line, counting from 1.</param>
/// <param name="Column">The column, counting from 1.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="File">
/// The file it's in, when that isn't the file being loaded: a file brought in with <c>#include</c>, whose templates
/// are copied into the pipeline. Null for the pipeline's own file.
/// </param>
public readonly record struct SourceSpan(int Line, int Column, int Length, string? File = null)
{
    /// <summary>The span from the start of <paramref name="first"/> to the end of <paramref name="last"/>, on one line.</summary>
    public static SourceSpan Between(SourceSpan first, SourceSpan last) =>
        new(first.Line, first.Column, Math.Max(last.Column + last.Length - first.Column, first.Length), first.File);
}

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
