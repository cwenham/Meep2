namespace Meep2.SDK.Security;

/// <summary>
/// What a permission is limited to, such as <c>~/notes/**</c> in <c>meep.files.write:~/notes/**</c> (Q260). A gated
/// function matches it against what it's about to touch, such as a file's path or a host's name.
/// </summary>
/// <remarks>
/// <para>
/// A scope is a pattern of parts separated by <c>/</c>. In a part, <c>*</c> stands for any run of characters and
/// <c>?</c> for any one; a part that is only <c>**</c> stands for any number of parts, none included. Anything else
/// stands for itself, matched exactly, case and all. A <c>~</c> at the start, alone or before a <c>/</c>, is your home
/// folder, in the scope and in what it's matched against.
/// </para>
/// <para>
/// Matching fails closed: a target with a <c>.</c> or <c>..</c> part, or an empty part other than a leading <c>/</c>,
/// matches nothing, since it could reach outside what the scope names. Callers should pass full paths, as
/// <see cref="Path.GetFullPath(string)"/> gives them; this doesn't follow symbolic links.
/// </para>
/// </remarks>
public sealed record PermissionScope
{
    private PermissionScope(string pattern) => Pattern = pattern;

    /// <summary>The scope as written, such as <c>~/notes/**</c>.</summary>
    public string Pattern { get; }

    /// <summary>Reads a scope. Throws <see cref="FormatException"/> if it's empty, has control characters or a <c>.</c> or <c>..</c> part.</summary>
    public static PermissionScope Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return WhyNot(pattern) is { } why
            ? throw new FormatException($"{pattern} can't be a permission's scope: {why}.")
            : new PermissionScope(pattern);
    }

    /// <summary>Reads a scope, or returns null if it can't be one.</summary>
    public static PermissionScope? TryParse(string? pattern) =>
        pattern is not null && WhyNot(pattern) is null ? new PermissionScope(pattern) : null;

    private static string? WhyNot(string pattern)
    {
        if (pattern.Length == 0)
            return "it's empty";
        if (pattern.Any(char.IsControl))
            return "it has control characters";
        if (pattern.Split('/').Any(part => part is "." or ".."))
            return "it has a . or .. part, which could reach outside it";
        return null;
    }

    /// <summary>Whether <paramref name="target"/>, such as a full path, is within this scope.</summary>
    public bool Matches(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var targetParts = Expand(target).Split('/');
        if (!IsPlain(targetParts))
            return false;
        return MatchParts(Expand(Pattern).Split('/'), targetParts);
    }

    /// <summary>
    /// Whether everything <paramref name="inner"/> matches is sure to be within this scope, as when a permission is
    /// passed on narrowed (Q262). Answers no when it can't tell, so it can refuse a narrowing that was in fact fine,
    /// but never allows one that wasn't.
    /// </summary>
    /// <remarks>
    /// It can tell when the two are the same, when <paramref name="inner"/> has no wildcards and this scope matches
    /// it, and when this scope is a plain prefix followed by <c>/**</c> (or is <c>**</c>) and <paramref name="inner"/>
    /// starts with that prefix.
    /// </remarks>
    public bool Contains(PermissionScope inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner.Pattern == Pattern || Pattern == "**")
            return true;
        if (!HasWildcards(inner.Pattern))
            return Matches(inner.Pattern);
        var pattern = Expand(Pattern);
        if (!pattern.EndsWith("/**", StringComparison.Ordinal))
            return false;
        var prefix = pattern[..^3];
        if (HasWildcards(prefix))
            return false;
        var innerPattern = Expand(inner.Pattern);
        return innerPattern == prefix || innerPattern.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    /// <summary>The scope as written.</summary>
    public override string ToString() => Pattern;

    private static bool HasWildcards(string text) => text.AsSpan().IndexOfAny('*', '?') >= 0;

    // A target is plain when it has no . or .. parts and no empty parts, other than one before a leading /.
    private static bool IsPlain(string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..")
                return false;
            if (parts[i].Length == 0 && !(i == 0 && parts.Length > 1))
                return false;
        }
        return true;
    }

    private static string Expand(string text)
    {
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/') + text[1..];
        return text;
    }

    // Matches parts in order, with ** taking any number of target parts. Remembers the last ** to try it again with
    // one more part, so it takes time in proportion to the parts times the **s, not exponential time.
    private static bool MatchParts(string[] pattern, string[] target)
    {
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < target.Length)
        {
            if (p < pattern.Length && pattern[p] == "**")
            {
                starP = p++;
                starT = t;
            }
            else if (p < pattern.Length && MatchPart(pattern[p], target[t]))
            {
                p++;
                t++;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == "**")
            p++;
        return p == pattern.Length;
    }

    // Matches one part, with * taking any run of characters and ? any one, in the same way.
    private static bool MatchPart(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
