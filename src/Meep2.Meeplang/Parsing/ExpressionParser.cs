using System.Text;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Numbers;

namespace Meep2.Meeplang.Parsing;

/// <summary>
/// Parses expressions by precedence, lowest first: <c>or</c>, <c>and</c>, <c>not</c>, comparisons and <c>is</c>,
/// <c>+ -</c>, <c>* / %</c>, unary <c>-</c>, then <c>.member</c>, calls and <c>[index]</c>.
/// </summary>
/// <remarks>
/// An expression stops at anything that can't continue it, so on a module line it ends before <c>as</c>,
/// <c>@</c>, or a <c>name=</c> parameter.
/// </remarks>
internal sealed class ExpressionParser(List<Token> tokens, int position, Diagnostics diagnostics)
{
    private const int NotPower = 3;
    private const int UnaryMinusPower = 7;

    private static readonly HashSet<string> s_reservedWords = ["and", "or", "not", "is", "as"];

    public int Position => position;

    public Token Current => tokens[position];

    /// <summary>Parses one expression, or reports an error and returns null.</summary>
    public ExpressionSyntax? Parse(int minimumPower = 0)
    {
        var left = ParsePrefix();
        if (left is null)
            return null;

        while (true)
        {
            var token = Current;
            if (token.Is("."))
            {
                position++;
                if (Current.Kind != TokenKind.Identifier)
                {
                    diagnostics.Error($"Expected a member name after '.', not {Current}.", Current.Span);
                    return null;
                }
                var name = Current;
                position++;
                left = new MemberSyntax(left, name.Text, name.Span, SourceSpan.Between(left.Span, name.Span));
                continue;
            }
            if (token.Is("(") && left is MemberSyntax or NameSyntax)
            {
                left = ParseCall(left);
                if (left is null)
                    return null;
                continue;
            }
            if (token.Is("["))
            {
                position++;
                var index = Parse();
                if (index is null || !Expect("]"))
                    return null;
                left = new IndexSyntax(left, index, SourceSpan.Between(left.Span, tokens[position - 1].Span));
                continue;
            }

            var (op, power) = InfixOperator(token);
            if (op is null || power <= minimumPower)
                return left;
            position++;

            if (op == "is")
            {
                var negated = false;
                if (Current.IsWord("not"))
                {
                    negated = true;
                    position++;
                }
                if (Current.Kind != TokenKind.Identifier)
                {
                    diagnostics.Error($"Expected a message type after 'is', not {Current}.", Current.Span);
                    return null;
                }
                var type = Current;
                var typeName = type.Text;
                var typeSpan = type.Span;
                position++;
                if (Current.Is(".") && tokens[position + 1].Kind == TokenKind.Identifier)
                {
                    // A plugin's message type, as in msg is Transit.Departure (Q105).
                    typeName += "." + tokens[position + 1].Text;
                    typeSpan = SourceSpan.Between(type.Span, tokens[position + 1].Span);
                    position += 2;
                }
                left = new IsSyntax(left, typeName, negated, SourceSpan.Between(left.Span, typeSpan));
                continue;
            }

            var right = Parse(power);
            if (right is null)
                return null;
            left = new BinarySyntax(op, left, right, SourceSpan.Between(left.Span, right.Span));
        }
    }

    private ExpressionSyntax? ParsePrefix()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
            case TokenKind.Duration:
            case TokenKind.RawString:
                position++;
                return new LiteralSyntax(token.Value, token.Span);

            case TokenKind.String:
                position++;
                return TemplateParser.Parse(token, diagnostics);

            case TokenKind.Identifier:
                switch (token.Text)
                {
                    case "true" or "false":
                        position++;
                        return new LiteralSyntax(token.Text == "true", token.Span);
                    case "null":
                        position++;
                        return new LiteralSyntax(null, token.Span);
                    case "not":
                    {
                        position++;
                        var operand = Parse(NotPower);
                        return operand is null ? null : new UnarySyntax("not", operand, SourceSpan.Between(token.Span, operand.Span));
                    }
                }
                if (s_reservedWords.Contains(token.Text))
                {
                    diagnostics.Error($"Expected a value, not '{token.Text}'.", token.Span);
                    return null;
                }
                position++;
                return new NameSyntax(token.Text, token.Span);

            case TokenKind.Symbol when token.Text == "-":
            {
                position++;
                var operand = Parse(UnaryMinusPower);
                if (operand is null)
                    return null;
                var span = SourceSpan.Between(token.Span, operand.Span);
                return operand switch
                {
                    LiteralSyntax { Value: decimal value } => new LiteralSyntax(-value, span),
                    LiteralSyntax { Value: BigDecimal value } => new LiteralSyntax(-value, span),
                    LiteralSyntax { Value: TimeSpan value } => new LiteralSyntax(-value, span),
                    _ => new UnarySyntax("-", operand, span),
                };
            }

            case TokenKind.Symbol when token.Text == "(":
            {
                position++;
                var inner = Parse();
                return inner is not null && Expect(")") ? inner : null;
            }

            default:
                diagnostics.Error($"Expected a value, not {token}.", token.Span);
                return null;
        }
    }

    private CallSyntax? ParseCall(ExpressionSyntax callee)
    {
        position++; // (
        var arguments = new List<ExpressionSyntax>();
        if (!Current.Is(")"))
        {
            while (true)
            {
                var argument = Parse();
                if (argument is null)
                    return null;
                arguments.Add(argument);
                if (Current.Is(","))
                {
                    position++;
                    continue;
                }
                break;
            }
        }
        if (!Expect(")"))
            return null;
        return new CallSyntax(callee, arguments, SourceSpan.Between(callee.Span, tokens[position - 1].Span));
    }

    private bool Expect(string symbol)
    {
        if (Current.Is(symbol))
        {
            position++;
            return true;
        }
        diagnostics.Error($"Expected '{symbol}', not {Current}.", Current.Span);
        return false;
    }

    private static (string? Operator, int Power) InfixOperator(Token token) => token.Kind switch
    {
        TokenKind.Identifier => token.Text switch
        {
            "or" => ("or", 1),
            "and" => ("and", 2),
            "is" => ("is", 4),
            _ => (null, 0),
        },
        TokenKind.Symbol => token.Text switch
        {
            "==" or "!=" or "<" or "<=" or ">" or ">=" => (token.Text, 4),
            "+" or "-" => (token.Text, 5),
            "*" or "/" or "%" => (token.Text, 6),
            _ => (null, 0),
        },
        _ => (null, 0),
    };
}

/// <summary>
/// Turns a double-quoted string into a template. <c>{expression}</c> and <c>{expression:format}</c> are holes;
/// <c>{{</c> and <c>}}</c> are literal braces; <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\r</c> and <c>\t</c> are escapes.
/// A string without holes becomes a plain literal.
/// </summary>
internal static class TemplateParser
{
    public static ExpressionSyntax? Parse(Token token, Diagnostics diagnostics)
    {
        var content = token.Text;
        var contentColumn = token.Column + 1;
        var parts = new List<TemplatePart>();
        var text = new StringBuilder();
        var ok = true;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\\' && i + 1 < content.Length && content[i + 1] == 'u' && i + 5 < content.Length
                && ushort.TryParse(content.AsSpan(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code))
            {
                // \u0003: a character by its code, for control characters such as the end of a TCP message.
                text.Append((char)code);
                i += 5;
                continue;
            }
            if (c == '\\' && i + 1 < content.Length)
            {
                var next = content[++i];
                text.Append(next switch
                {
                    'n' => "\n",
                    'r' => "\r",
                    't' => "\t",
                    '0' => "\0",
                    '"' => "\"",
                    '\\' => "\\",
                    _ => "\\" + next, // not an escape: keep the backslash, as in a Windows path
                });
                continue;
            }
            if (c == '{')
            {
                if (i + 1 < content.Length && content[i + 1] == '{')
                {
                    text.Append('{');
                    i++;
                    continue;
                }
                var end = FindHoleEnd(content, i + 1);
                if (end < 0)
                {
                    diagnostics.Error("This { isn't closed with a }. Write {{ for a brace.", new SourceSpan(token.Line, contentColumn + i, 1));
                    return null;
                }
                if (text.Length > 0)
                {
                    parts.Add(new TextPart(text.ToString()));
                    text.Clear();
                }
                var hole = ParseHole(content, i + 1, end, token.Line, contentColumn, diagnostics);
                if (hole is null)
                    ok = false;
                else
                    parts.Add(hole);
                i = end;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < content.Length && content[i + 1] == '}')
                {
                    text.Append('}');
                    i++;
                    continue;
                }
                diagnostics.Error("A } on its own isn't allowed in a string. Write }} for a brace.", new SourceSpan(token.Line, contentColumn + i, 1));
                ok = false;
                continue;
            }
            text.Append(c);
        }

        if (!ok)
            return null;
        if (parts.Count == 0)
            return new LiteralSyntax(text.ToString(), token.Span);
        if (text.Length > 0)
            parts.Add(new TextPart(text.ToString()));
        return new TemplateSyntax(parts, token.Span);
    }

    // Finds the } that closes a hole, allowing for brackets and strings inside it.
    private static int FindHoleEnd(string content, int start)
    {
        var depth = 0;
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
            if (c is '"' or '\'')
            {
                var quote = c;
                for (i++; i < content.Length && content[i] != quote; i++)
                {
                    if (content[i] == '\\')
                        i++;
                }
                continue;
            }
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']')
                depth--;
            else if (c == '}')
            {
                if (depth == 0)
                    return i;
                depth--;
            }
        }
        return -1;
    }

    private static HolePart? ParseHole(string content, int start, int end, int line, int contentColumn, Diagnostics diagnostics)
    {
        // The format follows the first colon that isn't inside brackets or a string.
        var expressionEnd = end;
        string? format = null;
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var c = content[i];
            if (c is '"' or '\'')
            {
                var quote = c;
                for (i++; i < end && content[i] != quote; i++)
                {
                    if (content[i] == '\\')
                        i++;
                }
                continue;
            }
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth--;
            else if (c == ':' && depth == 0)
            {
                expressionEnd = i;
                format = content[(i + 1)..end];
                break;
            }
        }

        var expressionText = content[start..expressionEnd];
        if (string.IsNullOrWhiteSpace(expressionText))
        {
            diagnostics.Error("This {} has nothing in it. Put an expression inside, as in {msg.Name}.", new SourceSpan(line, contentColumn + start - 1, end - start + 2));
            return null;
        }

        var tokens = Lexer.Lex(expressionText, line, contentColumn + start, diagnostics);
        var parser = new ExpressionParser(tokens, 0, diagnostics);
        var expression = parser.Parse();
        if (expression is null)
            return null;
        if (parser.Current.Kind != TokenKind.End)
        {
            diagnostics.Error($"Unexpected {parser.Current} in {{…}}.", parser.Current.Span);
            return null;
        }
        return new HolePart(expression, format);
    }
}
