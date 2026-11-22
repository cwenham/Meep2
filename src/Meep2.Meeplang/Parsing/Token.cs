using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Parsing;

/// <summary>A token on one line. <see cref="Column"/> counts from 1, as in the source file.</summary>
/// <param name="Text">For identifiers and symbols, the text; for double-quoted strings, the content between the quotes as written.</param>
/// <param name="Value">For numbers, durations and single-quoted strings, the value.</param>
internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Length, object? Value = null, string? File = null)
{
    public SourceSpan Span => new(Line, Column, Length, File);

    public bool Is(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

    public bool IsWord(string word) => Kind == TokenKind.Identifier && Text == word;

    public override string ToString() => Kind switch
    {
        TokenKind.End => "the end of the line",
        TokenKind.String => $"\"{Text}\"",
        _ => $"'{Text}'",
    };
}
