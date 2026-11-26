using System.Runtime.InteropServices;

namespace Meep2.Shared;

/// <summary>
/// The real form of a path, as permissions scoped by path are matched against it (Q269): whole, with <c>~</c> read as
/// the home folder, symbolic links followed, <c>.</c> and <c>..</c> resolved, and in the case the file system has it,
/// so <c>~/Notes/a.md</c> on a Mac's disk, where the folder is <c>notes</c>, is <c>/Users/you/notes/a.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The part of the path that exists is read with the system's <c>realpath</c>, which on macOS also gives the case on
/// disk. The rest, which doesn't exist yet, such as a file about to be saved, is added as written. A symbolic link
/// whose target doesn't exist is followed all the same, since writing through it would create the target.
/// </para>
/// <para>
/// A path can change between being checked and being opened, as when someone replaces a folder with a link. Meep
/// doesn't guard against that: whoever can change the folders a pipeline writes to can do as much without Meep.
/// </para>
/// </remarks>
public static class RealPaths
{
    // As the kernel limits a chain of links.
    private const int MostLinks = 40;

    /// <summary>The real form of <paramref name="path"/>. A relative path starts from the current folder.</summary>
    public static string Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Of(path, 0);
    }

    // Folders' real forms, kept for a second, since a module checks the same few folders for every message and
    // realpath costs tens of microseconds on macOS. Cleared when it gets large.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Real, long Until)> s_folders = new(StringComparer.Ordinal);
    private const int MostFolders = 4096;

    /// <summary>
    /// The real form of <paramref name="path"/>, as <see cref="Of"/> gives it, with the folder it's in worked out at most
    /// once a second. The last part is followed if it's a link, and otherwise kept as written, so on a disk that ignores
    /// case it isn't put in the disk's case; a folder changed into a link within the second isn't noticed until then.
    /// </summary>
    public static string Quickly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = System.IO.Path.GetFullPath(HomeFolder.Expand(path));
        var folder = System.IO.Path.GetDirectoryName(full);
        var name = System.IO.Path.GetFileName(full);
        if (OperatingSystem.IsWindows() || folder is null || name.Length == 0 || LinkTarget(full) is not null)
            return Of(full);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!s_folders.TryGetValue(folder, out var known) || now >= known.Until)
        {
            if (s_folders.Count >= MostFolders)
                s_folders.Clear();
            known = (Of(folder), now + System.Diagnostics.Stopwatch.Frequency);
            s_folders[folder] = known;
        }
        return System.IO.Path.Join(known.Real, name);
    }

    private static string Of(string path, int links)
    {
        var full = System.IO.Path.GetFullPath(HomeFolder.Expand(path));
        if (OperatingSystem.IsWindows())
            return full;

        // The longest part that exists, and what's left after it.
        var existing = full;
        var rest = new List<string>();
        string? real;
        while ((real = RealPath(existing)) is null)
        {
            var parent = System.IO.Path.GetDirectoryName(existing);
            if (parent is null)
                return full;
            rest.Add(System.IO.Path.GetFileName(existing));
            existing = parent;
        }
        if (rest.Count == 0)
            return real;
        rest.Reverse();

        // The first part that doesn't exist may be a link to something that doesn't, which writing would create.
        var next = System.IO.Path.Join(real, rest[0]);
        if (links < MostLinks && LinkTarget(next) is { } target)
        {
            var followed = System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Join(real, target);
            return Of(System.IO.Path.Join([followed, .. rest.Skip(1)]), links + 1);
        }
        return System.IO.Path.Join([real, .. rest]);
    }

    // Where a symbolic link points, as written in it, or null if the path isn't a link.
    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The system's realpath, or null if the path doesn't exist or can't be reached.
    private static string? RealPath(string path)
    {
        var resolved = NativeRealPath(path, 0);
        if (resolved == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [DllImport("libc", EntryPoint = "realpath")]
    private static extern nint NativeRealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint pointer);
}
