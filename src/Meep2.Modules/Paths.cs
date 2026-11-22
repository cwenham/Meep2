using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>Resolves paths written in pipelines.</summary>
internal static class Paths
{
    /// <summary>Turns a leading <c>~</c> into the home folder, and makes the path absolute.</summary>
    public static string Expand(string path) => Path.GetFullPath(Home(path));

    /// <summary>
    /// The full path a computed path names, for a module that reads or writes it (S2). Unless
    /// <paramref name="anyPath"/> is set, what the message gave can't make the path absolute, start it with <c>~</c>,
    /// or climb out of a folder with <c>..</c>, and can't be part of a file:// address. With a
    /// <paramref name="root"/>, relative paths start from it, and the path, once worked out, has to be inside it.
    /// </summary>
    /// <exception cref="PathRefusedException">The path breaks one of those rules.</exception>
    public static string Resolve(IReadOnlyList<TextPiece> pieces, string? root, bool anyPath)
    {
        var text = string.Concat(pieces.Select(piece => piece.Text));
        if (!anyPath)
            CheckPieces(pieces, text);
        var local = Addresses.FilePath(text) ?? text;
        if (root is null)
            return Expand(local);

        var folder = Expand(root);
        string path;
        try
        {
            path = Path.GetFullPath(Home(local), folder);
        }
        catch (ArgumentException exception)
        {
            throw new PathRefusedException($"{text} isn't a path: {exception.Message}");
        }
        var relative = Path.GetRelativePath(folder, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new PathRefusedException($"{path} is outside {folder}, the root= folder.");
        return path;
    }

    // Refuses a path the message steered outside where the pipeline put it.
    private static void CheckPieces(IReadOnlyList<TextPiece> pieces, string text)
    {
        // Which characters of the joined text the message gave.
        var given = new bool[text.Length];
        var at = 0;
        var any = false;
        foreach (var piece in pieces)
        {
            if (piece.FromMessage)
            {
                Array.Fill(given, true, at, piece.Text.Length);
                any |= piece.Text.Length > 0;
            }
            at += piece.Text.Length;
        }
        if (!any)
            return;
        bool Given(int start, int length) => Array.IndexOf(given, true, start, length) >= 0;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile && !Path.IsPathRooted(text))
            throw Refused(text, "the message gave part of a file:// address; write the path without file:// to have it checked");
        if (text.StartsWith('~') && given[0])
            throw Refused(text, "the message started it with ~, which would put it in a home folder");
        if (Path.GetPathRoot(text) is { Length: > 0 } rootPart && Given(0, rootPart.Length))
            throw Refused(text, "the message made it an absolute path");

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '/' && text[i] != Path.DirectorySeparatorChar)
                continue;
            if (i - start == 2 && text[start] == '.' && text[start + 1] == '.' && Given(start, 2))
                throw Refused(text, "the message put '..' in it, which climbs out of the folder");
            start = i + 1;
        }
    }

    private static PathRefusedException Refused(string text, string why) =>
        new($"{text} isn't used, because {why}. Set any-path=true if the message should choose any path.");

    private static string Home(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;
}

/// <summary>A computed path was refused, because the message steered it somewhere the pipeline didn't put it (S2).</summary>
internal sealed class PathRefusedException(string message) : Exception(message);
