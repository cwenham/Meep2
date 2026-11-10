using System.Security.Cryptography;
using System.Text;
using Meep2.Meeplang.Syntax;

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
        if (included == "~" || included.StartsWith("~/", StringComparison.Ordinal))
            included = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), included.Length > 2 ? included[2..] : "");
        return Path.GetFullPath(included, Path.GetDirectoryName(Full(from)) ?? Directory.GetCurrentDirectory());
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
                if (bytes is not null && include.Sha256 is { } pin && Convert.ToHexStringLower(SHA256.HashData(bytes)) is var sha && sha != pin)
                    throw new IncludeException($"it has the SHA-256 {sha}, not the {pin} the #include line pins, so it has changed.");
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

    // A file's text from its bytes, as UTF-8, without the mark some editors put at the start.
    private static string Text(byte[] bytes) =>
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

/// <summary>
/// Finds and reads the files <c>#include</c> lines name, for a pipeline that isn't on this machine's disk, such as one
/// loaded from a URL or Git, so its includes come from there too, and are checked the same way.
/// </summary>
public interface IIncludeReader
{
    /// <summary>The included file's address, from the line as written and the address of the file that includes it.</summary>
    string Resolve(string included, string from);

    /// <summary>The file's bytes, or null if there's no such file.</summary>
    /// <param name="address">The file, as <see cref="Resolve"/> found it.</param>
    /// <param name="pinned">
    /// Whether the <c>#include</c> line pins the file with its SHA-256, which the parser checks. A pinned file is
    /// trusted as the file that includes it is, so it needs no signature of its own (Q136).
    /// </param>
    /// <exception cref="IncludeException">The file can't be used, such as one that isn't signed by a key you trust.</exception>
    byte[]? Read(string address, bool pinned);
}

/// <summary>An included file can't be used. The message says why.</summary>
public sealed class IncludeException(string message, Exception? innerException = null) : Exception(message, innerException);

internal sealed class LineParser(SourceFile source, bool isInclude = false)
{
    private const int TabWidth = 4;

    // An included file's spans name it, so its templates still point at it once copied into a pipeline.
    private readonly Diagnostics _diagnostics = new(source.Path) { SpanFile = isInclude ? source.Path : null };
    private readonly List<TemplateBlock> _templates = [];
    private readonly List<IncludeDirective> _includes = [];
    private readonly List<ArgumentSyntax> _defaults = [];
    private readonly List<ConstantSyntax> _constants = [];
    private readonly List<PluginDirective> _plugins = [];
    private readonly List<ModuleSyntax> _modules = [];

    // Open blocks, outermost first. Null owner means the left margin.
    private readonly List<(int Indent, object? Owner)> _levels = [(0, null)];

    // The last line that can have lines indented under it: a module, or the defaults header.
    private object? _lastOpener;
    private bool _seenModule;

    public PipelineSyntax Parse()
    {
        var lines = source.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            ParseLine(lines[i].TrimEnd('\r'), i + 1);

        CheckFeeders(_modules);
        foreach (var template in _templates)
            CheckFeeders(template.Body);
        return new PipelineSyntax(source, _defaults, _constants, _plugins, _modules, _diagnostics.Items, _templates, _includes);
    }

    private void ParseLine(string text, int lineNumber)
    {
        var (indent, contentStart) = MeasureIndent(text);
        if (contentStart < text.Length && text[contentStart] == '#')
        {
            // Directives have a grammar of their own: addresses and versions, not Meeplang expressions.
            ParseDirective(text, contentStart, indent, lineNumber);
            return;
        }

        // on-error's value has a grammar of its own (log, stop-branch after 5 in 1s, …), so under defaults it's
        // kept as written. Problems lexing it as Meeplang only count if the line turns out to be something else.
        var setting = RawSetting(text, contentStart, lineNumber, _diagnostics.SpanFile);
        var lexing = setting is null ? _diagnostics : new Diagnostics(source.Path) { SpanFile = _diagnostics.SpanFile };
        var tokens = Lexer.Lex(text[contentStart..], lineNumber, contentStart + 1, lexing);
        if (tokens[0].Kind == TokenKind.End)
            return; // a blank line or a comment

        var owner = FindOwner(indent, tokens[0]);
        if (owner is NotPlaced)
            return;
        if (setting is not null && owner is List<ArgumentSyntax> defaults)
        {
            _lastOpener = null;
            defaults.Add(new ArgumentSyntax("on-error", setting, tokens[0].Span));
            return;
        }
        if (!ReferenceEquals(lexing, _diagnostics))
        {
            foreach (var diagnostic in lexing.Items)
                _diagnostics.Add(diagnostic);
        }

        var first = tokens[0];
        if (first.Kind == TokenKind.Identifier && tokens[1].Kind != TokenKind.Symbol)
        {
            switch (first.Text)
            {
                case "template":
                    ParseTemplateHeader(tokens, owner);
                    return;
            }
        }
        if (first.IsWord("defaults") && tokens[1].Kind == TokenKind.End)
        {
            ParseDefaultsHeader(first, owner);
            return;
        }
        if (first.IsWord("const"))
        {
            ParseConstant(tokens, owner);
            return;
        }
        if (first.Kind == TokenKind.Identifier && tokens[1].Is("="))
        {
            ParseParameterLine(tokens, owner);
            return;
        }
        ParseModuleLine(tokens, owner);
    }

    // The value of an on-error line, as written: everything after the = up to any comment. Null for other lines,
    // and when the value is in quotes, which parses as an ordinary string.
    private static LiteralSyntax? RawSetting(string text, int contentStart, int lineNumber, string? file)
    {
        const string Name = "on-error";
        var content = text.AsSpan(contentStart);
        if (!content.StartsWith(Name, StringComparison.Ordinal))
            return null;
        var afterName = content[Name.Length..];
        var equals = afterName.Length - afterName.TrimStart(' ').Length;
        if (equals >= afterName.Length || afterName[equals] != '=' || (equals + 1 < afterName.Length && afterName[equals + 1] == '='))
            return null;

        var valueStart = contentStart + Name.Length + equals + 1;
        var value = text[valueStart..];
        var comment = value.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
            value = value[..comment];
        var lead = value.Length - value.TrimStart().Length;
        value = value.Trim();
        if (value.Length == 0 || value[0] is '"' or '\'')
            return null;
        return new LiteralSyntax(value, new SourceSpan(lineNumber, valueStart + lead + 1, value.Length, file));
    }

    private static (int Indent, int ContentStart) MeasureIndent(string text)
    {
        var indent = 0;
        var i = 0;
        for (; i < text.Length; i++)
        {
            if (text[i] == ' ')
                indent++;
            else if (text[i] == '\t')
                indent += TabWidth;
            else
                break;
        }
        return (indent, i);
    }

    private sealed class NotPlaced
    {
        public static readonly NotPlaced Instance = new();
    }

    // Works out which block a line belongs to from its indentation: its owner is a module, the defaults header, or
    // null for the left margin.
    private object? FindOwner(int indent, Token first)
    {
        var current = _levels[^1];
        if (indent > current.Indent)
        {
            if (_lastOpener is null)
            {
                _diagnostics.Error("This line is indented, but the line above can't have anything under it.", first.Span);
                return NotPlaced.Instance;
            }
            _levels.Add((indent, _lastOpener));
            return _lastOpener;
        }

        while (indent < _levels[^1].Indent)
            _levels.RemoveAt(_levels.Count - 1);
        if (indent != _levels[^1].Indent)
        {
            _diagnostics.Error("This line's indentation doesn't line up with any line above it.", first.Span);
            return NotPlaced.Instance;
        }
        return _levels[^1].Owner;
    }

    private const string PluginUsage =
        "Write a plugin as #plugin github.com/owner/repo 1.2.0 sha256:…, #plugin nuget:Package.Id 1.2.0, or #plugin \"path/to/Plugin.dll\" for one on this machine. Any of them can end with 'as Name'.";

    // #plugin github.com/owner/repo 1.2.0 sha256:… | #plugin nuget:Id 1.2.0 [sha256:…] | #plugin "path", then [as Name]
    private void ParseDirective(string text, int start, int indent, int lineNumber)
    {
        _lastOpener = null;
        var words = DirectiveWords(text, start, lineNumber);
        var name = words[0];
        if (name.Text == "#include")
        {
            ParseInclude(words, indent);
            return;
        }
        if (name.Text != "#plugin")
        {
            _diagnostics.Error($"There's no {name.Text} directive. Meeplang has #plugin and #include.", name.Span);
            return;
        }
        if (isInclude)
        {
            _diagnostics.Error("An included file holds templates and constants; #plugin goes in the pipeline's own file.", name.Span);
            return;
        }
        if (indent > 0)
        {
            _diagnostics.Error("#plugin goes at the left margin.", name.Span);
            return;
        }
        while (_levels.Count > 1)
            _levels.RemoveAt(_levels.Count - 1);
        if (_seenModule)
        {
            _diagnostics.Error("#plugin goes before the pipeline's modules.", name.Span);
            return;
        }
        if (words.Count < 2)
        {
            _diagnostics.Error(PluginUsage, name.Span);
            return;
        }

        var span = SourceSpan.Between(name.Span, words[^1].Span);
        string? alias = null;
        if (words.Count > 2 && words[^2] is { Text: "as", Quoted: false })
        {
            alias = words[^1].Text;
            if (words[^1].Quoted || !IsIdentifier(alias))
            {
                _diagnostics.Error($"A plugin's name after 'as' is a word of letters and digits, such as Transit, not {words[^1].Text}.", words[^1].Span);
                return;
            }
            words.RemoveRange(words.Count - 2, 2);
        }
        else if (words.Count > 1 && words[^1] is { Text: "as", Quoted: false })
        {
            _diagnostics.Error("Expected a name for the plugin after 'as'.", words[^1].Span);
            return;
        }

        var reference = words[1];
        if (reference.Quoted)
        {
            if (words.Count > 2)
                _diagnostics.Error("A plugin on this machine takes just its path, with no version or SHA.", words[2].Span);
            else
                _plugins.Add(new PluginDirective(PluginOrigin.Local, reference.Text, null, null, span) { Alias = alias });
            return;
        }

        PluginOrigin origin;
        string id;
        if (reference.Text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            origin = PluginOrigin.GitHub;
            id = reference.Text["github.com/".Length..];
            if (id.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
            {
                _diagnostics.Error($"A plugin on GitHub is named as github.com/owner/repo, not {reference.Text}.", reference.Span);
                return;
            }
        }
        else if (reference.Text.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
        {
            origin = PluginOrigin.NuGet;
            id = reference.Text["nuget:".Length..];
            if (id.Length == 0 || id.Contains('/', StringComparison.Ordinal))
            {
                _diagnostics.Error($"A plugin on NuGet is named as nuget:Package.Id, not {reference.Text}.", reference.Span);
                return;
            }
        }
        else
        {
            _diagnostics.Error(PluginUsage, reference.Span);
            return;
        }

        if (words.Count < 3 || words[2].Text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Error($"{reference.Text} needs a version, as in {reference.Text} 1.2.0.", reference.Span);
            return;
        }
        var version = words[2].Text;
        string? sha = null;
        if (words.Count > 3)
        {
            var hash = words[3];
            if (!hash.Text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || hash.Text.Length != "sha256:".Length + 64
                || !hash.Text["sha256:".Length..].All(char.IsAsciiHexDigit))
            {
                _diagnostics.Error("A plugin's SHA is written as sha256: and 64 hex digits.", hash.Span);
                return;
            }
            sha = hash.Text["sha256:".Length..].ToLowerInvariant();
            if (words.Count > 4)
            {
                _diagnostics.Error("A #plugin line ends after the SHA, or after 'as' and a name.", words[4].Span);
                return;
            }
        }
        if (origin == PluginOrigin.GitHub && sha is null)
        {
            _diagnostics.Error(
                $"{reference.Text} {version} needs the SHA-256 of its release asset, as in sha256:3f9a…, so the release can't change under you.",
                span);
            return;
        }
        _plugins.Add(new PluginDirective(origin, id, version, sha, span) { Alias = alias });
    }

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    // Splits a directive line into words at spaces, keeping quoted strings whole and stopping at a // comment.
    private List<(string Text, SourceSpan Span, bool Quoted)> DirectiveWords(string text, int start, int lineNumber)
    {
        var words = new List<(string Text, SourceSpan Span, bool Quoted)>();
        var i = start;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                break;
            var wordStart = i;
            if (text[i] is '"' or '\'')
            {
                var quote = text[i];
                var end = text.IndexOf(quote, i + 1);
                if (end < 0)
                {
                    _diagnostics.Error($"This string isn't closed with a {quote}.", new SourceSpan(lineNumber, i + 1, text.Length - i, _diagnostics.SpanFile));
                    end = text.Length - 1;
                }
                words.Add((text[(i + 1)..end], new SourceSpan(lineNumber, wordStart + 1, end + 1 - wordStart, _diagnostics.SpanFile), true));
                i = end + 1;
                continue;
            }
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            words.Add((text[wordStart..i], new SourceSpan(lineNumber, wordStart + 1, i - wordStart, _diagnostics.SpanFile), false));
        }
        return words;
    }

    // #include "file.meep": at the margin, before the modules, like #plugin.
    private void ParseInclude(List<(string Text, SourceSpan Span, bool Quoted)> words, int indent)
    {
        var name = words[0];
        if (indent > 0)
        {
            _diagnostics.Error("#include goes at the left margin.", name.Span);
            return;
        }
        while (_levels.Count > 1)
            _levels.RemoveAt(_levels.Count - 1);
        if (_seenModule)
        {
            _diagnostics.Error("#include goes before the templates and modules.", name.Span);
            return;
        }
        if (words is not ([_, { Quoted: true }] or [_, { Quoted: true }, { Quoted: false }]))
        {
            _diagnostics.Error("Write an include as #include \"file.meep\", with the file in quotes, and sha256: and its SHA-256 after it to pin it.", name.Span);
            return;
        }
        var file = words[1];
        string? sha = null;
        if (words.Count == 3)
        {
            var pin = words[2];
            sha = pin.Text.StartsWith("sha256:", StringComparison.Ordinal) ? pin.Text["sha256:".Length..].ToLowerInvariant() : null;
            if (sha is not { Length: 64 } || !sha.All(char.IsAsciiHexDigit))
            {
                _diagnostics.Error("A pin is written as sha256: and the file's SHA-256, 64 hexadecimal digits, as in #include \"shared.meep\" sha256:3f9a….", pin.Span);
                return;
            }
        }
        _includes.Add(new IncludeDirective(file.Text, SourceSpan.Between(name.Span, words[^1].Span), sha));
    }

    // template Name or template Name(Parameter, Parameter = default): the modules indented under it are its body.
    private void ParseTemplateHeader(List<Token> tokens, object? owner)
    {
        _lastOpener = null;
        var first = tokens[0];
        if (owner is not null)
        {
            _diagnostics.Error("'template' goes at the left margin.", first.Span);
            return;
        }
        var name = tokens[1];
        if (name.Kind != TokenKind.Identifier)
        {
            _diagnostics.Error("Write a template as: template Name(Parameter, Parameter = default)", first.Span);
            return;
        }
        var parameters = new List<TemplateParameter>();
        var position = 2;
        if (tokens[position].Is("("))
        {
            position++;
            while (!tokens[position].Is(")"))
            {
                var parameter = tokens[position];
                if (parameter.Kind != TokenKind.Identifier)
                {
                    _diagnostics.Error($"Expected a parameter's name, not {parameter}.", parameter.Span);
                    return;
                }
                if (parameter.Text is "msg" or "fam")
                {
                    _diagnostics.Error($"{parameter.Text} is reserved, so it can't be a parameter's name.", parameter.Span);
                    return;
                }
                if (parameters.Any(p => p.Name == parameter.Text))
                {
                    _diagnostics.Error($"{name.Text} already has a parameter called {parameter.Text}.", parameter.Span);
                    return;
                }
                position++;
                ExpressionSyntax? fallback = null;
                if (tokens[position].Is("="))
                {
                    var parser = new ExpressionParser(tokens, position + 1, _diagnostics);
                    fallback = parser.Parse();
                    if (fallback is null)
                        return;
                    position = parser.Position;
                }
                else if (parameters.Count > 0 && parameters[^1].Default is not null)
                {
                    _diagnostics.Error($"{parameter.Text} needs a default, since the parameter before it has one.", parameter.Span);
                    return;
                }
                parameters.Add(new TemplateParameter(parameter.Text, fallback, parameter.Span));
                if (tokens[position].Is(","))
                    position++;
                else if (!tokens[position].Is(")"))
                {
                    _diagnostics.Error($"Expected ',' or ')' after the parameter {parameter.Text}, not {tokens[position]}.", tokens[position].Span);
                    return;
                }
            }
            position++;
        }
        if (!ExpectEnd(tokens[position]))
            return;
        if (_templates.Any(t => t.Name == name.Text))
        {
            _diagnostics.Error($"There's already a template called {name.Text}.", name.Span);
            return;
        }
        var template = new TemplateBlock(name.Text, name.Span, parameters, source.Path);
        _templates.Add(template);
        _seenModule = true;
        _lastOpener = template;
    }

    private void ParseDefaultsHeader(Token first, object? owner)
    {
        _lastOpener = null;
        if (isInclude)
        {
            _diagnostics.Error("An included file holds templates and constants; 'defaults' goes in the pipeline's own file.", first.Span);
            return;
        }
        if (owner is not null)
        {
            _diagnostics.Error("'defaults' goes at the left margin.", first.Span);
            return;
        }
        if (_seenModule)
        {
            _diagnostics.Error("'defaults' goes before the pipeline's modules.", first.Span);
            return;
        }
        _lastOpener = _defaults;
    }

    private void ParseConstant(List<Token> tokens, object? owner)
    {
        _lastOpener = null;
        var first = tokens[0];
        if (owner is not null)
        {
            _diagnostics.Error("'const' goes at the left margin.", first.Span);
            return;
        }
        if (_seenModule)
        {
            _diagnostics.Error("Constants go before the pipeline's modules.", first.Span);
            return;
        }
        if (tokens[1].Kind != TokenKind.Identifier || !tokens[2].Is("="))
        {
            _diagnostics.Error("Write a constant as: const Name = value", first.Span);
            return;
        }
        var parser = new ExpressionParser(tokens, 3, _diagnostics);
        var value = parser.Parse();
        if (value is null || !ExpectEnd(parser.Current))
            return;
        _constants.Add(new ConstantSyntax(tokens[1].Text, value, tokens[1].Span));
    }

    private void ParseParameterLine(List<Token> tokens, object? owner)
    {
        _lastOpener = null;
        var name = tokens[0];
        if (name.IsWord("by"))
        {
            _diagnostics.Error("Write by on the module's line, without =, as in Zip by msg.Key.", name.Span);
            return;
        }
        var parser = new ExpressionParser(tokens, 2, _diagnostics);
        var value = parser.Parse();
        if (value is null || !ExpectEnd(parser.Current))
            return;
        var argument = new ArgumentSyntax(name.Text, value, name.Span);
        switch (owner)
        {
            case ModuleSyntax module:
                module.AddArgument(argument);
                break;
            case List<ArgumentSyntax> defaults:
                defaults.Add(argument);
                break;
            default:
                _diagnostics.Error($"The parameter {name.Text} needs to be indented under the module it belongs to.", name.Span);
                break;
        }
    }

    private void ParseModuleLine(List<Token> tokens, object? owner)
    {
        _lastOpener = null;
        var position = 0;
        var isFeeder = tokens[0].Is("+");
        if (isFeeder)
            position++;

        var verb = tokens[position];
        if (verb.Kind != TokenKind.Identifier)
        {
            _diagnostics.Error($"Expected a module, such as Fetch or Where, not {verb}.", verb.Span);
            return;
        }
        position++;
        var module = new ModuleSyntax(isFeeder, verb.Text, verb.Span);

        if (tokens[position].Is(".") && tokens[position + 1].Kind == TokenKind.Identifier)
        {
            module.Inflection = tokens[position + 1].Text;
            position += 2;
            if (tokens[position].Is(".") && tokens[position + 1].Kind == TokenKind.Identifier)
            {
                // Plugin.Verb.Inflection
                module = new ModuleSyntax(isFeeder, module.Inflection, SourceSpan.Between(verb.Span, tokens[position - 1].Span))
                {
                    Plugin = verb.Text,
                    Inflection = tokens[position + 1].Text,
                };
                position += 2;
            }
        }

        if (!AtArgumentBoundary(tokens, position))
        {
            var parser = new ExpressionParser(tokens, position, _diagnostics);
            module.Positional = parser.Parse();
            if (module.Positional is null)
                return;
            position = parser.Position;
        }

        while (tokens[position].Kind != TokenKind.End)
        {
            var token = tokens[position];
            if (token.IsWord("as"))
            {
                var name = tokens[position + 1];
                if (name.Kind != TokenKind.Identifier)
                {
                    _diagnostics.Error("Expected a name after 'as'.", name.Span);
                    return;
                }
                if (module.Name is not null)
                {
                    _diagnostics.Error("This module already has a name.", token.Span);
                    return;
                }
                module.Name = name.Text;
                module.NameSpan = name.Span;
                position += 2;
            }
            else if (token.Is("@"))
            {
                var name = tokens[position + 1];
                if (name.Kind != TokenKind.Identifier)
                {
                    _diagnostics.Error("Expected a modifier name after '@', such as @bypass.", name.Span);
                    return;
                }
                position += 2;
                ExpressionSyntax? value = null;
                if (tokens[position].Is("="))
                {
                    var parser = new ExpressionParser(tokens, position + 1, _diagnostics);
                    value = parser.Parse();
                    if (value is null)
                        return;
                    position = parser.Position;
                }
                module.AddModifier(new ModifierSyntax(name.Text, value, SourceSpan.Between(token.Span, value?.Span ?? name.Span)));
            }
            else if (token.IsWord("by"))
            {
                // A key, as in Zip by msg.OrderId (Q130): the by parameter, written as a keyword.
                if (tokens[position + 1].Is("="))
                {
                    _diagnostics.Error("Write by without =, as in by msg.Key.", token.Span);
                    return;
                }
                var parser = new ExpressionParser(tokens, position + 1, _diagnostics);
                var value = parser.Parse();
                if (value is null)
                    return;
                module.AddArgument(new ArgumentSyntax("by", value, token.Span));
                position = parser.Position;
            }
            else if (token.Kind == TokenKind.Identifier && tokens[position + 1].Is("="))
            {
                var parser = new ExpressionParser(tokens, position + 2, _diagnostics);
                var value = parser.Parse();
                if (value is null)
                    return;
                module.AddArgument(new ArgumentSyntax(token.Text, value, token.Span));
                position = parser.Position;
            }
            else
            {
                _diagnostics.Error($"Unexpected {token}. After a module's value, write name=value, by key, as Name, or @modifier.", token.Span);
                return;
            }
        }

        switch (owner)
        {
            case null when isInclude:
                _diagnostics.Error("An included file holds templates and constants, not a pipeline's modules. Put these under a template.", verb.Span);
                return;
            case null:
                _modules.Add(module);
                break;
            case ModuleSyntax parent:
                parent.AddChild(module);
                break;
            case TemplateBlock template:
                template.AddModule(module);
                break;
            default:
                _diagnostics.Error("Only settings, written as name = value, go under 'defaults'.", verb.Span);
                return;
        }
        _seenModule = true;
        _lastOpener = module;
    }

    // Where a module's value ends and its named parts begin.
    private static bool AtArgumentBoundary(List<Token> tokens, int position)
    {
        var token = tokens[position];
        return token.Kind == TokenKind.End
            || token.Is("@")
            || token.IsWord("as")
            || token.IsWord("by")
            || (token.Kind == TokenKind.Identifier && tokens[position + 1].Is("="));
    }

    private bool ExpectEnd(Token token)
    {
        if (token.Kind == TokenKind.End)
            return true;
        _diagnostics.Error($"Unexpected {token}.", token.Span);
        return false;
    }

    // + lines must be followed, at the same indentation, by a line for them to feed.
    private void CheckFeeders(IReadOnlyList<ModuleSyntax> siblings)
    {
        ModuleSyntax? waiting = null;
        foreach (var module in siblings)
        {
            waiting = module.IsFeeder ? module : null;
            CheckFeeders(module.Children);
        }
        if (waiting is not null)
            _diagnostics.Error("Nothing follows this + line for it to feed. Put the module it feeds on the next line, at the same indentation.", waiting.VerbSpan);
    }
}
