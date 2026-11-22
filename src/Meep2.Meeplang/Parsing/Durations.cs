namespace Meep2.Meeplang.Parsing;

/// <summary>Durations as Meeplang writes them, for settings outside a pipeline file, such as a stream's interval.</summary>
public static class Durations
{
    /// <summary>Reads a duration such as 250ms, 5s or 1h30m, or a TimeSpan such as 00:00:05.</summary>
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var diagnostics = new Diagnostics(path: null);
        var tokens = Lexer.Lex(text.Trim(), 1, 1, diagnostics);
        if (diagnostics.HasErrors || tokens is not [{ Kind: TokenKind.Duration, Value: TimeSpan value }, { Kind: TokenKind.End }])
            return false;
        duration = value;
        return true;
    }

    /// <summary>Writes a duration as Meeplang does, such as 250ms, 30s or 1h30m.</summary>
    public static string Format(TimeSpan duration) => Meep2.Shared.DurationText.Write(duration);
}
