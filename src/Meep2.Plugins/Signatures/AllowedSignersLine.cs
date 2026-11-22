using System.Text;

namespace Meep2.Plugins;

/// <summary>
/// One line of an allowed-signers file, as <c>ssh-keygen -Y verify</c> reads it: <c>name [options] type base64
/// [comment]</c>. Of the options, only <c>namespaces</c> is used.
/// </summary>
internal static class AllowedSignersLine
{
    /// <summary>The key a line names, with the namespaces its options let it sign for.</summary>
    /// <exception cref="FormatException">The line isn't one, or its key can't be used.</exception>
    public static PublicKey Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = Split(line.Trim(), ' ');
        if (words.Count < 3)
            throw new FormatException($"A key is written as: name type base64, as in alice ssh-ed25519 AAAAC3Nz…. This isn't one: {line.Trim()}");
        var name = words[0];
        var options = words.Count > 3 && !PublicKey.IsType(words[1]) ? words[1] : null;
        var at = options is null ? 1 : 2;
        var key = PublicKey.Parse(name, string.Join(' ', words.Skip(at)));
        return options is null ? key : key.WithNamespaces(NamespacesOf(options));
    }

    // The namespaces an options word names, as in namespaces="meep-plugin,git", or null for any.
    private static string[]? NamespacesOf(string options)
    {
        string[]? namespaces = null;
        foreach (var option in Split(options, ','))
        {
            if (option.StartsWith("namespaces=", StringComparison.OrdinalIgnoreCase))
                namespaces = option["namespaces=".Length..].Trim('"').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return namespaces;
    }

    // Splits at a separator (or any space, for ' '), keeping quoted text whole, with its quotes.
    private static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
                quoted = !quoted;
            if (!quoted && (c == separator || (separator == ' ' && char.IsWhiteSpace(c))))
            {
                if (current.Length > 0)
                    parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}
