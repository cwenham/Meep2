namespace Meep2.Meeplang.Syntax;

/// <summary>A named value: a parameter (<c>timeout = 5s</c> or <c>timeout=5s</c>) or a setting under <c>defaults</c>.</summary>
/// <param name="Name">The name as written.</param>
/// <param name="Value">The value.</param>
/// <param name="NameSpan">Where the name is.</param>
public sealed record ArgumentSyntax(string Name, ExpressionSyntax Value, SourceSpan NameSpan)
{
    /// <summary>
    /// Where the whole parameter is written, from its name to the end of its value, brackets and all. Null for one
    /// made other than by the parser.
    /// </summary>
    public SourceSpan? Span { get; init; }
}

/// <summary>An <c>@</c> modifier, such as <c>@bypass</c> or <c>@buffer=1000</c>.</summary>
/// <param name="Name">The name, without the <c>@</c>.</param>
/// <param name="Value">The value, if one was given.</param>
/// <param name="Span">Where the modifier is, from its <c>@</c> to the end of its value.</param>
public sealed record ModifierSyntax(string Name, ExpressionSyntax? Value, SourceSpan Span);
