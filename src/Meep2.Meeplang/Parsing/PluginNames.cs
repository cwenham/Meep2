namespace Meep2.Meeplang.Parsing;

/// <summary>
/// The rules for the words of a <c>#plugin</c> line, which become folder names in Meep's plugin cache, so text in a
/// pipeline can't name a folder outside it.
/// </summary>
public static class PluginNames
{
    /// <summary>A GitHub account's name: letters, digits and '-', up to 39 characters, as GitHub allows.</summary>
    public static bool IsGitHubOwner(string owner) =>
        owner.Length is > 0 and <= 39 && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>A GitHub repository's name: letters, digits, '-', '_' and '.', up to 100 characters, but not '.' or '..'.</summary>
    public static bool IsGitHubRepo(string repo) =>
        repo.Length is > 0 and <= 100 && repo is not ("." or "..")
        && repo.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// A NuGet package ID, by NuGet's own rule (<c>^\w+([.-]\w+)*$</c>, up to 100 characters), but with ASCII letters
    /// and digits only, as nuget.org accepts.
    /// </summary>
    public static bool IsNuGetId(string id)
    {
        if (id.Length is 0 or > 100)
            return false;
        var afterSeparator = true;
        foreach (var c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                afterSeparator = false;
            else if (c is '.' or '-' && !afterSeparator)
                afterSeparator = true;
            else
                return false;
        }
        return !afterSeparator;
    }

    /// <summary>A version: letters, digits, '.', '+' and '-', up to 64 characters, starting with a letter or digit.</summary>
    public static bool IsVersion(string version) =>
        version.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(version[0])
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');
}
