using System.Globalization;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Numbers;

namespace Meep2.Meeplang.Parsing;

internal enum TokenKind
{
    Identifier,
    Number,
    Duration,
    String,
    RawString,
    Symbol,
    End,
}

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

/// <summary>
/// Splits one line of Meeplang (or one <c>{…}</c> hole in a template) into tokens. Comments start with <c>//</c>
/// outside a string and run to the end of the line.
/// </summary>
internal sealed class Lexer
{
    private static readonly string[] s_twoCharacterSymbols = ["==", "!=", "<=", ">="];
    private const string SingleCharacterSymbols = "<>+-*/%()[].,=@:#";

    private readonly string _text;
    private readonly int _line;
    private readonly int _firstColumn;
    private readonly Diagnostics _diagnostics;
    private readonly List<Token> _tokens = [];
    private int _position;

    private Lexer(string text, int line, int firstColumn, Diagnostics diagnostics)
    {
        _text = text;
        _line = line;
        _firstColumn = firstColumn;
        _diagnostics = diagnostics;
    }

    /// <summary>Lexes text that starts at <paramref name="firstColumn"/> of <paramref name="line"/>. Always ends with an End token.</summary>
    public static List<Token> Lex(string text, int line, int firstColumn, Diagnostics diagnostics)
    {
        var lexer = new Lexer(text, line, firstColumn, diagnostics);
        lexer.Run();
        return lexer._tokens;
    }

    private int Column(int position) => _firstColumn + position;

    private void Run()
    {
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (char.IsWhiteSpace(c))
            {
                _position++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                break; // a comment: the rest of the line is ignored
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                ReadIdentifier();
            }
            else if (char.IsAsciiDigit(c))
            {
                ReadNumberOrDuration();
            }
            else if (c == '"')
            {
                ReadString();
            }
            else if (c == '\'')
            {
                ReadRawString();
            }
            else if (TryReadSymbol())
            {
            }
            else
            {
                _diagnostics.Error($"'{c}' isn't something Meeplang understands here.", new SourceSpan(_line, Column(_position), 1, _diagnostics.SpanFile));
                _position++;
            }
        }
        _tokens.Add(new Token(TokenKind.End, "", _line, Column(_text.Length), 0, null, _diagnostics.SpanFile));
    }

    private char Peek(int offset) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

    private void ReadIdentifier()
    {
        var start = _position;
        SkipIdentifierCharacters();

        // Kebab-case names such as on-error are allowed where a name is expected: after @, or before a single =.
        var end = _position;
        var scan = _position;
        while (scan + 1 < _text.Length && _text[scan] == '-' && (char.IsAsciiLetter(_text[scan + 1]) || _text[scan + 1] == '_'))
        {
            scan++;
            while (scan < _text.Length && (char.IsAsciiLetterOrDigit(_text[scan]) || _text[scan] == '_'))
                scan++;
        }
        if (scan > end && (PreviousIs("@") || IsFollowedBySingleEquals(scan)))
            end = scan;

        _position = end;
        _tokens.Add(new Token(TokenKind.Identifier, _text[start..end], _line, Column(start), end - start, null, _diagnostics.SpanFile));
    }

    private void SkipIdentifierCharacters()
    {
        while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] == '_'))
            _position++;
    }

    private bool PreviousIs(string symbol) => _tokens.Count > 0 && _tokens[^1].Is(symbol);

    private bool IsFollowedBySingleEquals(int position)
    {
        while (position < _text.Length && _text[position] == ' ')
            position++;
        return position < _text.Length && _text[position] == '=' && (position + 1 >= _text.Length || _text[position + 1] != '=');
    }

    private void ReadNumberOrDuration()
    {
        var start = _position;
        SkipDigits();
        if (_position < _text.Length && _text[_position] == '.' && char.IsAsciiDigit(Peek(1)))
        {
            _position++;
            SkipDigits();
        }

        // TimeSpan formats, such as 00:00:05 or 1.00:00:00.
        if (_position < _text.Length && _text[_position] == ':' && char.IsAsciiDigit(Peek(1)))
        {
            while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] is ':' or '.'))
                _position++;
            var text = _text[start.._position];
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
                Add(TokenKind.Duration, text, start, span);
            else
                DurationError($"{text} isn't a duration Meeplang can read.", Span(start), start);
            return;
        }

        // Durations with units, such as 5s, 250ms or 1h30m.
        if (_position < _text.Length && char.IsAsciiLetter(_text[_position]))
        {
            ReadDurationUnits(start);
            return;
        }

        // Numbers are decimals, or big numbers if a decimal can't hold them exactly (Q81).
        var number = _text[start.._position];
        try
        {
            Add(TokenKind.Number, number, start, Numbers.Parse(number));
        }
        catch (OverflowException)
        {
            _diagnostics.Error($"This number has more than {BigDecimal.MaxDigits:N0} digits, which is more than Meeplang works with.", Span(start));
            Add(TokenKind.Number, number, start, 0m);
        }
    }

    private void ReadDurationUnits(int start)
    {
        var total = TimeSpan.Zero;
        var amountStart = start;
        while (true)
        {
            var amount = double.Parse(_text.AsSpan(amountStart, _position - amountStart), CultureInfo.InvariantCulture);
            var unitStart = _position;
            while (_position < _text.Length && char.IsAsciiLetter(_text[_position]))
                _position++;
            var unit = _text[unitStart.._position];
            TimeSpan? part = unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => null,
            };
            if (part is null)
            {
                DurationError($"'{unit}' isn't a unit Meeplang knows. Use ms, s, m, h or d, as in 250ms or 1h30m.", new SourceSpan(_line, Column(unitStart), unit.Length, _diagnostics.SpanFile), start);
                return;
            }
            total += part.Value;

            // A compound duration such as 1h30m continues with another number.
            if (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            {
                amountStart = _position;
                SkipDigits();
                if (_position < _text.Length && _text[_position] == '.' && char.IsAsciiDigit(Peek(1)))
                {
                    _position++;
                    SkipDigits();
                }
                if (_position >= _text.Length || !char.IsAsciiLetter(_text[_position]))
                {
                    DurationError("A number after a duration needs a unit too, as in 1h30m.", Span(start), start);
                    return;
                }
                continue;
            }
            Add(TokenKind.Duration, _text[start.._position], start, total);
            return;
        }
    }

    // Reports a bad duration, then stands in a zero duration so the rest of the line parses without knock-on errors.
    private void DurationError(string message, SourceSpan span, int start)
    {
        _diagnostics.Error(message, span);
        Add(TokenKind.Duration, _text[start.._position], start, TimeSpan.Zero);
    }

    private void SkipDigits()
    {
        while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            _position++;
    }

    private void ReadString()
    {
        var start = _position;
        _position++; // the opening quote
        var contentStart = _position;
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (c == '\\')
            {
                _position += 2;
                continue;
            }
            if (c == '"')
            {
                _tokens.Add(new Token(TokenKind.String, _text[contentStart.._position], _line, Column(start), _position + 1 - start, null, _diagnostics.SpanFile));
                _position++;
                return;
            }
            _position++;
        }
        _diagnostics.Error("This string isn't closed with a \".", Span(start));
        _position = _text.Length;
    }

    private void ReadRawString()
    {
        var start = _position;
        _position++;
        var value = new System.Text.StringBuilder();
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    value.Append('\'');
                    _position += 2;
                    continue;
                }
                _position++;
                Add(TokenKind.RawString, _text[start.._position], start, value.ToString());
                return;
            }
            value.Append(c);
            _position++;
        }
        _diagnostics.Error("This string isn't closed with a '.", Span(start));
        _position = _text.Length;
    }

    private bool TryReadSymbol()
    {
        foreach (var symbol in s_twoCharacterSymbols)
        {
            if (string.CompareOrdinal(_text, _position, symbol, 0, 2) == 0)
            {
                Add(TokenKind.Symbol, symbol, _position, null);
                _position += 2;
                return true;
            }
        }
        if (SingleCharacterSymbols.Contains(_text[_position], StringComparison.Ordinal))
        {
            Add(TokenKind.Symbol, _text[_position].ToString(), _position, null);
            _position++;
            return true;
        }
        return false;
    }

    private void Add(TokenKind kind, string text, int start, object? value) =>
        _tokens.Add(new Token(kind, text, _line, Column(start), Math.Max(text.Length, 1), value, _diagnostics.SpanFile));

    private SourceSpan Span(int start) => new(_line, Column(start), Math.Max(_position - start, 1), _diagnostics.SpanFile);
}

/// <summary>Collects diagnostics for one file.</summary>
internal sealed class Diagnostics(string? path)
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>
    /// The file to record in the spans of tokens lexed with these diagnostics: set for an included file, so its
    /// templates still point at it once copied into a pipeline.
    /// </summary>
    public string? SpanFile { get; init; }

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.IsError);

    public void Error(string message, SourceSpan span) => _items.Add(new Diagnostic(DiagnosticSeverity.Error, message, span, path));

    public void Warning(string message, SourceSpan span) => _items.Add(new Diagnostic(DiagnosticSeverity.Warning, message, span, path));

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);
}
