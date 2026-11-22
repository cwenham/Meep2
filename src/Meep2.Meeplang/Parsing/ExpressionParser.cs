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

    private static readonly HashSet<string> s_reservedWords = ["and", "or", "not", "is", "as", "by"];

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
