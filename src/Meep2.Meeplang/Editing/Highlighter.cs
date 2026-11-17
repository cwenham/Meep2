using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>A stretch of a line to colour, and what it is.</summary>
/// <param name="Line">The line, counting from 1.</param>
/// <param name="Column">Its first column, counting from 1, in UTF-16 code units.</param>
/// <param name="Length">How many columns it covers, all on one line.</param>
/// <param name="Type">What it is: one of <see cref="Highlighter.Types"/>.</param>
/// <param name="Modifiers">More about it: a bit for each of <see cref="Highlighter.Modifiers"/>, by position.</param>
public readonly record struct HighlightToken(int Line, int Column, int Length, int Type, int Modifiers);

/// <summary>
/// Colours Meeplang from its own parser, for editors (<c>meep lsp</c>'s semantic tokens): verbs, inflections and the
/// plugins they come from, templates, module names, parameters, modifiers, constants, the message types after
/// <c>is</c>, members, strings with the expressions in their holes, numbers and durations, keywords and comments.
/// </summary>
/// <remarks>
/// Each line is lexed as the parser lexes it, then what the parser made of the file is laid over that: a word that's
/// a verb on one line and a member on another is coloured for what it is in each place. Text that doesn't parse is
/// still coloured as far as its words go. The type and modifier names are the Language Server Protocol's own, so an
/// editor's theme colours them without being told about Meeplang.
/// </remarks>
public static class Highlighter
{
    /// <summary>The kinds of token, by number, as <see cref="HighlightToken.Type"/> gives them.</summary>
    public static IReadOnlyList<string> Types { get; } =
        ["namespace", "type", "parameter", "variable", "property", "function", "method", "macro", "keyword", "comment", "string", "number", "operator", "decorator"];

    /// <summary>What else can be said about a token, by bit, as <see cref="HighlightToken.Modifiers"/> gives them.</summary>
    public static IReadOnlyList<string> Modifiers { get; } = ["declaration", "readonly", "defaultLibrary"];

    private const int Namespace = 0, Type = 1, Parameter = 2, Variable = 3, Property = 4, Function = 5, Method = 6, Macro = 7,
        Keyword = 8, Comment = 9, String = 10, Number = 11, Operator = 12, Decorator = 13;

    private const int Declaration = 1, Readonly = 2, DefaultLibrary = 4;

    private static readonly HashSet<string> s_keywords = ["defaults", "const", "template", "as", "is", "not", "and", "or", "by", "true", "false", "null"];
    private static readonly HashSet<string> s_operators = ["==", "!=", "<=", ">=", "<", ">", "+", "-", "*", "/", "%", "="];

    /// <summary>The tokens to colour in a parsed file's own text, in order, none overlapping.</summary>
    /// <param name="syntax">The file, parsed, even if it has errors.</param>
    /// <param name="registry">
    /// The verbs it can use, to tell <c>Plugin.Verb</c> from <c>Verb.Inflection</c>, or null to take two words as a
    /// verb and its inflection.
    /// </param>
    public static IReadOnlyList<HighlightToken> Tokens(PipelineSyntax syntax, ModuleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var lines = syntax.Source.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var paint = new Painter(lines);
        var tokens = new List<Token>[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            tokens[i] = Lexical(lines[i], i + 1, paint);

        var templates = syntax.Templates.Concat(syntax.Included.SelectMany(i => i.Templates)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var constants = syntax.Constants.Concat(syntax.Included.SelectMany(i => i.Constants)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var walker = new Walker(paint, tokens, templates, constants, registry);
        foreach (var setting in syntax.Defaults)
        {
            walker.Paint(setting.NameSpan, Property, 0);
            walker.Expression(setting.Value, []);
        }
        foreach (var constant in syntax.Constants)
        {
            walker.Paint(constant.NameSpan, Variable, Declaration | Readonly);
            walker.Expression(constant.Value, []);
        }
        foreach (var template in syntax.Templates)
        {
            walker.Paint(template.NameSpan, Macro, Declaration);
            var parameters = template.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var parameter in template.Parameters)
            {
                walker.Paint(new SourceSpan(parameter.Span.Line, parameter.Span.Column, parameter.Name.Length, parameter.Span.File), Parameter, Declaration);
                if (parameter.Default is { } value)
                    walker.Expression(value, []);
            }
            foreach (var module in template.Body)
                walker.Module(module, parameters);
        }
        foreach (var module in syntax.Modules)
            walker.Module(module, []);
        return paint.Tokens();
    }

    // The words of a line as the lexer finds them, coloured by what they are wherever they are: keywords, strings,
    // numbers, operators, modifiers, message types after is, members after a dot, and the comment.
    private static List<Token> Lexical(string line, int number, Painter paint)
    {
        var (tokens, comment) = Lexer.LexLine(line, number);
        if (comment is { } at)
            paint.Paint(number, at, line.Length - at + 1, Comment, 0);
        var start = 0;
        while (start < line.Length && line[start] is ' ' or '\t')
            start++;
        if (start < line.Length && line[start] == '#')
        {
            Directive(tokens, number, paint);
            return tokens;
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token.Kind)
            {
                case TokenKind.Number or TokenKind.Duration:
                    paint.Paint(token, Number, 0);
                    break;
                case TokenKind.String or TokenKind.RawString:
                    paint.Paint(token, String, 0);
                    break;
                case TokenKind.Symbol when token.Text == "@" && tokens[i + 1].Kind == TokenKind.Identifier && tokens[i + 1].Column == token.Column + 1:
                    paint.Paint(number, token.Column, 1 + tokens[i + 1].Length, Decorator, 0);
                    i++;
                    break;
                case TokenKind.Symbol when s_operators.Contains(token.Text):
                    paint.Paint(token, Operator, 0);
                    break;
                case TokenKind.Identifier when token.Text is "msg" or "fam":
                    paint.Paint(token, Variable, Readonly | DefaultLibrary);
                    break;
                case TokenKind.Identifier when s_keywords.Contains(token.Text):
                    paint.Paint(token, Keyword, 0);
                    break;
                case TokenKind.Identifier when i > 0 && (tokens[i - 1].IsWord("is") || (tokens[i - 1].IsWord("not") && i > 1 && tokens[i - 2].IsWord("is"))):
                    paint.Paint(token, Type, 0);
                    break;
            }
        }
        return tokens;
    }

    // #plugin and #include: the directive and as are keywords, the plugin's name a namespace, the rest a string.
    private static void Directive(List<Token> tokens, int number, Painter paint)
    {
        var i = 0;
        if (tokens[i].Is("#") && tokens[i + 1].Kind == TokenKind.Identifier)
        {
            paint.Paint(number, tokens[i].Column, tokens[i + 1].Column + tokens[i + 1].Length - tokens[i].Column, Keyword, 0);
            i += 2;
        }
        var restStart = i;
        var restEnd = tokens.Count - 1; // the End token
        for (var j = i; j < tokens.Count - 1; j++)
        {
            if (tokens[j].IsWord("as"))
            {
                restEnd = j;
                paint.Paint(tokens[j], Keyword, 0);
                if (tokens[j + 1].Kind == TokenKind.Identifier)
                    paint.Paint(tokens[j + 1], Namespace, Declaration);
                break;
            }
        }
        if (restEnd > restStart)
        {
            var first = tokens[restStart];
            var last = tokens[restEnd - 1];
            paint.Paint(number, first.Column, last.Column + last.Length - first.Column, String, 0);
        }
    }

    // Lays what the parser made of the file over the lexer's colours.
    private sealed class Walker(Painter paint, List<Token>[] lines, HashSet<string> templates, HashSet<string> constants, ModuleRegistry? registry)
    {
        public void Paint(SourceSpan span, int type, int modifiers)
        {
            if (span.File is null)
                paint.Paint(span.Line, span.Column, span.Length, type, modifiers);
        }

        public void Module(ModuleSyntax module, HashSet<string> parameters)
        {
            if (module.VerbSpan.File is null)
                Verb(module);
            if (module.NameSpan is { } name)
                Paint(name, Variable, Declaration);
            if (module.Positional is { } positional)
                Expression(positional, parameters);
            foreach (var argument in module.Arguments)
            {
                if (argument.Name != "by")
                    Paint(argument.NameSpan, Parameter, 0);
                Expression(argument.Value, parameters);
            }
            foreach (var modifier in module.Modifiers)
            {
                if (modifier.Value is { } value)
                    Expression(value, parameters);
            }
            foreach (var child in module.Children)
                Module(child, parameters);
        }

        // The verb and the words dotted onto it: Verb, Verb.Inflection, Plugin.Verb or Plugin.Verb.Inflection, or a
        // template's name.
        private void Verb(ModuleSyntax module)
        {
            var span = module.VerbSpan;
            if (span.Line < 1 || span.Line > lines.Length)
                return;
            var tokens = lines[span.Line - 1];
            var at = tokens.FindIndex(t => t.Column == span.Column && t.Kind == TokenKind.Identifier);
            if (at < 0)
                return;
            var words = new List<Token> { tokens[at] };
            for (var i = at + 1; i + 1 < tokens.Count && tokens[i].Is(".") && tokens[i + 1].Kind == TokenKind.Identifier && words.Count < 3; i += 2)
                words.Add(tokens[i + 1]);
            int[] types;
            if (module.Plugin is not null && words.Count == 3)
                types = [Namespace, Function, Method];
            else if (words.Count == 1)
                types = [templates.Contains(module.Verb) ? Macro : Function];
            else if (registry is not null && !registry.TryGetVerb(words[0].Text, out _) && registry.IsPlugin(words[0].Text))
                types = [Namespace, Function];
            else
                types = [Function, Method];
            for (var i = 0; i < words.Count && i < types.Length; i++)
                paint.Paint(words[i], types[i], 0);
        }

        public void Expression(ExpressionSyntax expression, HashSet<string> parameters)
        {
            switch (expression)
            {
                case NameSyntax name when name.Name is "msg" or "fam":
                    Paint(name.Span, Variable, Readonly | DefaultLibrary); // as the lexer has it, but in a string's hole too
                    break;
                case NameSyntax name when parameters.Contains(name.Name):
                    Paint(name.Span, Parameter, 0);
                    break;
                case NameSyntax name when constants.Contains(name.Name):
                    Paint(name.Span, Variable, Readonly);
                    break;
                case NameSyntax name:
                    Paint(name.Span, Variable, 0);
                    break;
                case MemberSyntax member:
                    Expression(member.Target, parameters);
                    Paint(member.MemberSpan, Property, 0);
                    break;
                case CallSyntax call:
                    if (call.Callee is MemberSyntax method)
                    {
                        Expression(method.Target, parameters);
                        Paint(method.MemberSpan, Method, 0);
                    }
                    else
                    {
                        Expression(call.Callee, parameters);
                    }
                    foreach (var argument in call.Arguments)
                        Expression(argument, parameters);
                    break;
                case IndexSyntax index:
                    Expression(index.Target, parameters);
                    Expression(index.Index, parameters);
                    break;
                case UnarySyntax unary:
                    Expression(unary.Operand, parameters);
                    break;
                case BinarySyntax binary:
                    Expression(binary.Left, parameters);
                    Expression(binary.Right, parameters);
                    break;
                case IsSyntax test:
                    Expression(test.Operand, parameters);
                    break;
                case TemplateSyntax template:
                    foreach (var hole in template.Parts.OfType<HolePart>())
                        Expression(hole.Expression, parameters);
                    break;
            }
        }
    }

    // Each line's columns, coloured by what was laid over them last, then read off as tokens.
    private sealed class Painter(string[] lines)
    {
        private readonly int[]?[] _cells = new int[]?[lines.Length];

        public void Paint(Token token, int type, int modifiers) => Paint(token.Line, token.Column, token.Length, type, modifiers);

        public void Paint(int line, int column, int length, int type, int modifiers)
        {
            if (line < 1 || line > lines.Length || column < 1)
                return;
            var text = lines[line - 1];
            var cells = _cells[line - 1] ??= Enumerable.Repeat(-1, text.Length).ToArray();
            var end = Math.Min(column - 1 + length, text.Length);
            for (var i = column - 1; i < end; i++)
                cells[i] = (type << 8) | modifiers;
        }

        // Runs of the same colour, without the spaces between words, so a token never starts or ends with a space.
        public IReadOnlyList<HighlightToken> Tokens()
        {
            var tokens = new List<HighlightToken>();
            for (var line = 0; line < lines.Length; line++)
            {
                if (_cells[line] is not { } cells)
                    continue;
                var text = lines[line];
                var i = 0;
                while (i < cells.Length)
                {
                    var value = cells[i];
                    if (value < 0 || (value >> 8 != Comment && value >> 8 != String && char.IsWhiteSpace(text[i])))
                    {
                        i++;
                        continue;
                    }
                    var start = i;
                    while (i < cells.Length && cells[i] == value && (value >> 8 == Comment || value >> 8 == String || !char.IsWhiteSpace(text[i])))
                        i++;
                    tokens.Add(new HighlightToken(line + 1, start + 1, i - start, value >> 8, value & 0xFF));
                }
            }
            return tokens;
        }
    }
}
