using System.Text;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Parsing;

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
                    diagnostics.Error("This { isn't closed with a }. Write {{ for a brace.", new SourceSpan(token.Line, contentColumn + i, 1, diagnostics.SpanFile));
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
                diagnostics.Error("A } on its own isn't allowed in a string. Write }} for a brace.", new SourceSpan(token.Line, contentColumn + i, 1, diagnostics.SpanFile));
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
            diagnostics.Error("This {} has nothing in it. Put an expression inside, as in {msg.Name}.", new SourceSpan(line, contentColumn + start - 1, end - start + 2, diagnostics.SpanFile));
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
