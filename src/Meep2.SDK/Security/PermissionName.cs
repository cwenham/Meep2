using System.Diagnostics.CodeAnalysis;

namespace Meep2.SDK.Security;

/// <summary>
/// A permission's name and, optionally, its scope: <c>ReadNotes</c>, or <c>meep.files.write:~/notes/**</c> (Q260).
/// </summary>
/// <remarks>
/// <para>
/// A name is one or more words separated by dots. A word starts with a letter and goes on with letters, digits,
/// <c>-</c> and <c>_</c>. Names are compared exactly, case and all. Everything after the first <c>:</c> is the scope
/// (<see cref="PermissionScope"/>).
/// </para>
/// <para>
/// A permission without a scope covers every scope of the same name, so <c>meep.files.write</c> covers
/// <c>meep.files.write:~/notes/**</c>, but not the other way round.
/// </para>
/// </remarks>
public sealed record PermissionName
{
    /// <summary>Makes a permission name with no scope. Throws <see cref="FormatException"/> if it isn't a name.</summary>
    public PermissionName(string name)
        : this(name, null)
    {
    }

    /// <summary>Makes a permission name with a scope, or none if <paramref name="scope"/> is null.</summary>
    public PermissionName(string name, PermissionScope? scope)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (WhyNotName(name) is { } why)
            throw new FormatException($"{name} can't be a permission's name: {why}.");
        Name = name;
        Scope = scope;
    }

    /// <summary>The name, such as <c>meep.files.write</c>.</summary>
    public string Name { get; }

    /// <summary>What it's limited to, or null for everything.</summary>
    public PermissionScope? Scope { get; }

    /// <summary>Reads <c>name</c> or <c>name:scope</c>. Throws <see cref="FormatException"/> if it can't.</summary>
    public static PermissionName Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? new PermissionName(text)
            : new PermissionName(text[..colon], PermissionScope.Parse(text[(colon + 1)..]));
    }

    /// <summary>Reads <c>name</c> or <c>name:scope</c>, or returns false if it can't.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PermissionName? permission)
    {
        permission = null;
        if (text is null)
            return false;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? text : text[..colon];
        if (WhyNotName(name) is not null)
            return false;
        PermissionScope? scope = null;
        if (colon >= 0 && (scope = PermissionScope.TryParse(text[(colon + 1)..])) is null)
            return false;
        permission = new PermissionName(name, scope);
        return true;
    }

    /// <summary>Whether <paramref name="name"/> could be a permission's name, leaving out any scope.</summary>
    public static bool IsName(string? name) => name is not null && WhyNotName(name) is null;

    /// <summary>
    /// Whether holding this permission is enough for <paramref name="needed"/>: the same name, and a scope at least
    /// as wide (<see cref="PermissionScope.Contains"/>).
    /// </summary>
    public bool Covers(PermissionName needed)
    {
        ArgumentNullException.ThrowIfNull(needed);
        if (needed.Name != Name)
            return false;
        if (Scope is null)
            return true;
        return needed.Scope is not null && Scope.Contains(needed.Scope);
    }

    /// <summary>
    /// Whether holding this permission allows the permission <paramref name="name"/> on <paramref name="target"/>,
    /// such as <c>meep.files.write</c> on a file's full path.
    /// </summary>
    public bool Allows(string name, string target)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(target);
        return name == Name && (Scope is null || Scope.Matches(target));
    }

    /// <summary>The name, then a colon and the scope if there is one.</summary>
    public override string ToString() => Scope is null ? Name : $"{Name}:{Scope.Pattern}";

    private static string? WhyNotName(string name)
    {
        if (name.Length == 0)
            return "it's empty";
        foreach (var word in name.Split('.'))
        {
            if (word.Length == 0)
                return "it has an empty word between dots";
            if (!char.IsAsciiLetter(word[0]))
                return "each word between dots starts with a letter";
            if (!word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return "it may only have letters, digits, -, _ and dots";
        }
        return null;
    }
}
