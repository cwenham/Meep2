namespace Meep2.Meeplang.Syntax;

/// <summary>An expression in a pipeline, such as <c>msg.minutesAway &lt; 30</c>. Its span covers the whole expression.</summary>
/// <param name="Span">Where it is.</param>
public abstract record ExpressionSyntax(SourceSpan Span);

/// <summary>A literal: a number, a duration, a string without <c>{…}</c>, <c>true</c>, <c>false</c> or <c>null</c>.</summary>
/// <param name="Value">The value: an int, long, double, TimeSpan, string, bool, or null.</param>
public sealed record LiteralSyntax(object? Value, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A string with <c>{…}</c> holes, such as <c>"{msg.route} in {msg.minutesAway} min"</c>.</summary>
public sealed record TemplateSyntax(IReadOnlyList<TemplatePart> Parts, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>Part of a template.</summary>
public abstract record TemplatePart;

/// <summary>Text in a template, with escapes already applied.</summary>
public sealed record TextPart(string Text) : TemplatePart;

/// <summary>A <c>{expression:format}</c> hole in a template.</summary>
/// <param name="Expression">The expression.</param>
/// <param name="Format">The format after the colon, such as <c>yyyy-MM-dd</c>, if any.</param>
public sealed record HolePart(ExpressionSyntax Expression, string? Format) : TemplatePart;

/// <summary>A name: <c>msg</c>, <c>fam</c>, or a constant.</summary>
public sealed record NameSyntax(string Name, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A member: <c>target.Member</c>.</summary>
/// <param name="MemberSpan">Where the member's name is.</param>
public sealed record MemberSyntax(ExpressionSyntax Target, string Member, SourceSpan MemberSpan, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A call: <c>target.Method(arguments)</c>.</summary>
public sealed record CallSyntax(ExpressionSyntax Callee, IReadOnlyList<ExpressionSyntax> Arguments, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>An index: <c>target[index]</c>.</summary>
public sealed record IndexSyntax(ExpressionSyntax Target, ExpressionSyntax Index, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A unary operator: <c>-x</c> or <c>not x</c>.</summary>
public sealed record UnarySyntax(string Operator, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A binary operator, such as <c>a &lt; b</c> or <c>a and b</c>.</summary>
public sealed record BinarySyntax(string Operator, ExpressionSyntax Left, ExpressionSyntax Right, SourceSpan Span) : ExpressionSyntax(Span);

/// <summary>A type test: <c>msg is ErrorMessage</c> or <c>msg is not ErrorMessage</c>.</summary>
public sealed record IsSyntax(ExpressionSyntax Operand, string TypeName, bool Negated, SourceSpan Span) : ExpressionSyntax(Span);
