using System.Security.Cryptography;
using System.Text;
using Meep2.Meeplang.Syntax;
using Meep2.Shared;

namespace Meep2.Meeplang.Parsing;

/// <summary>
/// Reads Meeplang text into a <see cref="PipelineSyntax"/>.
/// </summary>
/// <remarks>
/// <para>
/// Meeplang is line-based and uses indentation (layout A). A module indented under another receives what it emits;
/// modules at the same indentation under one module are branches. A line starting with <c>+</c> starts its own chain
/// and feeds the next unmarked line at the same indentation; several <c>+</c> lines in a row all feed it.
/// </para>
/// <para>
/// A line of the form <c>name = value</c> under a module is one of its parameters. <c>defaults</c> and <c>const</c>
/// lines come before the modules. Comments start with <c>//</c>. A tab counts as four spaces.
/// </para>
/// </remarks>
public static class MeeplangParser
{
    /// <summary>
    /// Parses a whole file, and the files its <c>#include</c> lines bring in, read from disk. Problems are reported in
    /// <see cref="PipelineSyntax.Diagnostics"/>, not thrown.
    /// </summary>
    public static PipelineSyntax Parse(SourceFile source) => Parse(source, new DiskIncludes(ReadFromDisk));

    /// <summary>
    /// Parses a whole file, and the files its <c>#include</c> lines bring in, read by <paramref name="read"/>: a full
    /// path in, the file's text out, or null if there's no such file.
    /// </summary>
    public static PipelineSyntax Parse(SourceFile source, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Parse(source, new DiskIncludes(path => read(path) is { } text ? Encoding.UTF8.GetBytes(text) : null));
    }

    /// <summary>
    /// Parses a whole file, and the files its <c>#include</c> lines bring in, found and read by
    /// <paramref name="includes"/>, as for a pipeline loaded from a URL or Git, whose includes come from there too.
    /// </summary>
    public static PipelineSyntax Parse(SourceFile source, IIncludeReader includes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(includes);
        var syntax = new LineParser(source).Parse();
        Include(syntax, syntax, includes, [includes.Resolve(source.Path, source.Path)]);
        return syntax;
    }

    /// <summary>The full path an <c>#include</c> line names, from the folder of the file it's in. <c>~</c> is your home folder.</summary>
    public static string IncludePath(string included, string from)
    {
        ArgumentNullException.ThrowIfNull(included);
        ArgumentNullException.ThrowIfNull(from);
        return Path.GetFullPath(HomeFolder.Expand(included), Path.GetDirectoryName(Full(from)) ?? Directory.GetCurrentDirectory());
    }

    // Parses what a file includes, and what they include, into the pipeline's list, refusing an include that loops.
    private static void Include(PipelineSyntax root, PipelineSyntax from, IIncludeReader includes, HashSet<string> including)
    {
        foreach (var include in from.Includes)
        {
            string path;
            string? text;
            try
            {
                path = includes.Resolve(include.Path, from.Source.Path);
                if (including.Contains(path))
                {
                    root.AddDiagnostic(new Diagnostic(DiagnosticSeverity.Error, $"{include.Path} is already being included, so including it here would go round in a loop.", include.Span, from.Source.Path));
                    continue;
                }
                if (root.Included.Any(i => i.Source.Path == path))
                    continue; // included once already, by another file
                var bytes = includes.Read(path, pinned: include.Sha256 is not null);
                if (bytes is not null && PinMismatch(bytes, include) is { } mismatch)
                    throw new IncludeException(mismatch);
                text = bytes is null ? null : Text(bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IncludeException)
            {
                root.AddDiagnostic(new Diagnostic(DiagnosticSeverity.Error, $"Couldn't include {include.Path}: {exception.Message}", include.Span, from.Source.Path));
                continue;
            }
            if (text is null)
            {
                root.AddDiagnostic(new Diagnostic(DiagnosticSeverity.Error, $"There's no file {path} to include.", include.Span, from.Source.Path));
                continue;
            }
            var included = new LineParser(new SourceFile(path, text), isInclude: true).Parse();
            root.AddIncluded(included);
            Include(root, included, includes, [.. including, path]);
        }
    }

    // Includes on this machine's disk, relative to the including file's folder.
    private sealed class DiskIncludes(Func<string, byte[]?> read) : IIncludeReader
    {
        public string Resolve(string included, string from) => included == from ? Full(from) : IncludePath(included, from);

        public byte[]? Read(string path, bool pinned) => read(path);
    }

    private static byte[]? ReadFromDisk(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    // Why an included file isn't the one its #include line pins, or null if it is, or the line doesn't pin it.
    internal static string? PinMismatch(byte[] bytes, IncludeDirective include) =>
        include.Sha256 is { } pin && Convert.ToHexStringLower(SHA256.HashData(bytes)) is var sha && sha != pin
            ? $"it has the SHA-256 {sha}, not the {pin} the #include line pins, so it has changed."
            : null;

    // A file's text from its bytes, as UTF-8, without the mark some editors put at the start.
    internal static string Text(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>Parses a single expression, for tests and tools.</summary>
    internal static ExpressionSyntax? ParseExpression(string text, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var collected = new Diagnostics(path: null);
        var tokens = Lexer.Lex(text, line: 1, firstColumn: 1, collected);
        var parser = new ExpressionParser(tokens, 0, collected);
        var expression = parser.Parse();
        if (expression is not null && parser.Current.Kind != TokenKind.End)
            collected.Error($"Unexpected {parser.Current}.", parser.Current.Span);
        diagnostics = collected.Items;
        return collected.HasErrors ? null : expression;
    }
}
