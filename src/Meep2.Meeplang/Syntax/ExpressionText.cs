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
            case LiteralSyntax literal:
                WriteLiteral(text, literal);
                break;
            case TemplateSyntax template:
                WriteTemplate(text, template);
                break;
            case NameSyntax name:
                text.Append(name.Name);
                break;
            case MemberSyntax member:
                Write(text, member.Target, inner: true);
                text.Append('.').Append(member.Member);
                break;
            case CallSyntax call:
                WriteCall(text, call);
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
                Open(text, inner);
                Write(text, binary.Left, inner: true);
                text.Append(' ').Append(binary.Operator).Append(' ');
                Write(text, binary.Right, inner: true);
                Close(text, inner);
                break;
            case IsSyntax test:
                Open(text, inner);
                Write(text, test.Operand, inner: true);
                text.Append(test.Negated ? " is not " : " is ").Append(test.TypeName);
                Close(text, inner);
                break;
            default:
                text.Append(expression);
                break;
        }
    }

    // An operation inside another goes in brackets, so it reads back the same whatever the operators' precedence.
    private static void Open(StringBuilder text, bool inner)
    {
        if (inner)
            text.Append('(');
    }

    private static void Close(StringBuilder text, bool inner)
    {
        if (inner)
            text.Append(')');
    }

    private static void WriteLiteral(StringBuilder text, LiteralSyntax literal)
    {
        switch (literal.Value)
        {
            case string value:
                text.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)).Append('"');
                break;
            case TimeSpan duration:
                text.Append(Durations.Format(duration));
                break;
            case bool flag:
                text.Append(flag ? "true" : "false");
                break;
            case null:
                text.Append("null");
                break;
            default:
                text.Append(Convert.ToString(literal.Value, CultureInfo.InvariantCulture));
                break;
        }
    }

    // A string with holes: its text escaped, and each hole's expression and format in braces.
    private static void WriteTemplate(StringBuilder text, TemplateSyntax template)
    {
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
    }

    private static void WriteCall(StringBuilder text, CallSyntax call)
    {
        Write(text, call.Callee, inner: true);
        text.Append('(');
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            if (i > 0)
                text.Append(", ");
            Write(text, call.Arguments[i], inner: false);
        }
        text.Append(')');
    }
}
