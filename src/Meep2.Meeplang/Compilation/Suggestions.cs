namespace Meep2.Meeplang.Compilation;

/// <summary>Finds near-misses for "did you mean" hints.</summary>
internal static class Suggestions
{
    /// <summary>" Did you mean 'X'?" for the closest candidate, or nothing if none is close.</summary>
    public static string For(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates.Distinct())
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return $" Did you mean '{candidate}'?";
            var distance = Distance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        var allowed = Math.Max(1, name.Length / 3);
        return best is not null && bestDistance <= allowed ? $" Did you mean '{best}'?" : "";
    }

    // Edit distance where swapping two neighbouring letters counts as one edit, since that's a common typo.
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
