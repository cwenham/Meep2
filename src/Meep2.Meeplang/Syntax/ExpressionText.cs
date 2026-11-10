using System.Globalization;
using System.Text;
using Meep2.Meeplang.Parsing;

namespace Meep2.Meeplang.Syntax;

/// <summary>
/// Writes an expression back as Meeplang text, from its syntax rather than the file: for comparing a module's
/// parameters, where a template's copy has its parameters' values in place of their names.
/// </summary>
public static class ExpressionText
{
    /// <summary>The expression as Meeplang would write it.</summary>
    public static string Of(ExpressionSyntax expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var text = new StringBuilder();
        Write(text, expression, inner: false);
        return text.ToString();
    }

    private static void Write(StringBuilder text, ExpressionSyntax expression, bool inner)
    {
        switch (expression)
        {
            case LiteralSyntax { Value: string value }:
                text.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)).Append('"');
                break;
            case LiteralSyntax { Value: TimeSpan duration }:
                text.Append(Durations.Format(duration));
                break;
            case LiteralSyntax { Value: bool flag }:
                text.Append(flag ? "true" : "false");
                break;
            case LiteralSyntax { Value: null }:
                text.Append("null");
                break;
            case LiteralSyntax literal:
                text.Append(Convert.ToString(literal.Value, CultureInfo.InvariantCulture));
                break;
            case TemplateSyntax template:
                text.Append('"');
                foreach (var part in template.Parts)
                {
                    if (part is TextPart words)
                    {
                        text.Append(words.Text.Replace("\"", "\\\"", StringComparison.Ordinal).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
                    }
                    else if (part is HolePart hole)
                    {
                        text.Append('{');
                        Write(text, hole.Expression, inner: false);
                        if (hole.Format is not null)
                            text.Append(':').Append(hole.Format);
                        text.Append('}');
                    }
                }
                text.Append('"');
                break;
            case NameSyntax name:
                text.Append(name.Name);
                break;
            case MemberSyntax member:
                Write(text, member.Target, inner: true);
                text.Append('.').Append(member.Member);
                break;
            case CallSyntax call:
                Write(text, call.Callee, inner: true);
                text.Append('(');
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    Write(text, call.Arguments[i], inner: false);
                }
                text.Append(')');
                break;
            case IndexSyntax index:
                Write(text, index.Target, inner: true);
                text.Append('[');
                Write(text, index.Index, inner: false);
                text.Append(']');
                break;
            case UnarySyntax unary:
                text.Append(unary.Operator == "not" ? "not " : unary.Operator);
                Write(text, unary.Operand, inner: true);
                break;
            case BinarySyntax binary:
                if (inner)
                    text.Append('(');
                Write(text, binary.Left, inner: true);
                text.Append(' ').Append(binary.Operator).Append(' ');
                Write(text, binary.Right, inner: true);
                if (inner)
                    text.Append(')');
                break;
            case IsSyntax test:
                if (inner)
                    text.Append('(');
                Write(text, test.Operand, inner: true);
                text.Append(test.Negated ? " is not " : " is ").Append(test.TypeName);
                if (inner)
                    text.Append(')');
                break;
            default:
                text.Append(expression);
                break;
        }
    }
}
