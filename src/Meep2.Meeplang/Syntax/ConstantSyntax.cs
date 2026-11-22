namespace Meep2.Meeplang.Syntax;

/// <summary>A <c>const Name = value</c> declaration.</summary>
/// <param name="Name">The constant's name.</param>
/// <param name="Value">Its value.</param>
/// <param name="NameSpan">Where the name is.</param>
public sealed record ConstantSyntax(string Name, ExpressionSyntax Value, SourceSpan NameSpan);
