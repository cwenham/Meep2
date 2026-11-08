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
    /// <summary>Parses a whole file. Problems are reported in <see cref="PipelineSyntax.Diagnostics"/>, not thrown.</summary>
    public static PipelineSyntax Parse(SourceFile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LineParser(source).Parse();
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

internal sealed class LineParser(SourceFile source)
{
    private const int TabWidth = 4;

    private readonly Diagnostics _diagnostics = new(source.Path);
    private readonly List<ArgumentSyntax> _defaults = [];
    private readonly List<ConstantSyntax> _constants = [];
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
        return new PipelineSyntax(source, _defaults, _constants, _modules, _diagnostics.Items);
    }

    private void ParseLine(string text, int lineNumber)
    {
        var (indent, contentStart) = MeasureIndent(text);
        var tokens = Lexer.Lex(text[contentStart..], lineNumber, contentStart + 1, _diagnostics);
        if (tokens[0].Kind == TokenKind.End)
            return; // a blank line or a comment

        var owner = FindOwner(indent, tokens[0]);
        if (owner is NotPlaced)
            return;

        var first = tokens[0];
        if (first.Is("#"))
        {
            var directive = tokens[1].Kind == TokenKind.Identifier ? $"#{tokens[1].Text}" : "#";
            _diagnostics.Error($"{directive} isn't supported yet: #include arrives with templates, and #plugin with plugins.", first.Span);
            _lastOpener = null;
            return;
        }
        if (first.Kind == TokenKind.Identifier && tokens[1].Kind != TokenKind.Symbol)
        {
            switch (first.Text)
            {
                case "pipeline" or "template":
                    _diagnostics.Error($"'{first.Text}' blocks aren't supported yet. For now a file holds one pipeline.", first.Span);
                    _lastOpener = null;
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

    private void ParseDefaultsHeader(Token first, object? owner)
    {
        _lastOpener = null;
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
                _diagnostics.Error($"Unexpected {token}. After a module's value, write name=value, as Name, or @modifier.", token.Span);
                return;
            }
        }

        switch (owner)
        {
            case null:
                _modules.Add(module);
                break;
            case ModuleSyntax parent:
                parent.AddChild(module);
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
