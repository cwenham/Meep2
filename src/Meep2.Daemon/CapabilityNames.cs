namespace Meep2.Daemon;

/// <summary>Reads and writes capabilities as words: <c>observe,inject</c>, or <c>all</c>.</summary>
public static class CapabilityNames
{
    /// <summary>The capabilities as lowercase words.</summary>
    public static IReadOnlyList<string> Of(Capabilities capabilities) =>
        [.. Enum.GetValues<Capabilities>()
            .Where(c => c is not (Capabilities.None or Capabilities.All) && capabilities.HasFlag(c))
            .Select(c => c.ToString().ToLowerInvariant())];

    /// <summary>Reads words such as <c>observe,inject</c>, or <c>all</c>.</summary>
    /// <exception cref="FormatException">One isn't a capability.</exception>
    public static Capabilities Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var capabilities = Capabilities.None;
        foreach (var word in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            capabilities |= Enum.TryParse<Capabilities>(word, ignoreCase: true, out var one) && one != Capabilities.None
                ? one
                : throw new FormatException($"{word} isn't a capability. They're observe, inject, control, or all.");
        }
        return capabilities;
    }
}
