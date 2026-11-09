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
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            return "-" + Format(duration.Negate());
        if (duration == TimeSpan.Zero)
            return "0s";
        var text = new System.Text.StringBuilder();
        if (duration.Days > 0)
            text.Append(duration.Days).Append('d');
        if (duration.Hours > 0)
            text.Append(duration.Hours).Append('h');
        if (duration.Minutes > 0)
            text.Append(duration.Minutes).Append('m');
        if (duration.Seconds > 0)
            text.Append(duration.Seconds).Append('s');
        if (duration.Milliseconds > 0)
            text.Append(duration.Milliseconds).Append("ms");
        return text.Length > 0 ? text.ToString() : $"{duration.TotalMilliseconds:0.###}ms";
    }
}
