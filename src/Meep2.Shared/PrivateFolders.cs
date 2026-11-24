namespace Meep2.Shared;

/// <summary>
/// Folders and files kept to the user who runs Meep (S7): <c>~/.meep</c> and what's in it, such as detached pipelines'
/// logs and learned trees, which can hold private text. A plugin that keeps files in Meep's folder can use it too. On
/// Windows, folders keep the permissions they inherit.
/// </summary>
public static class PrivateFolders
{
    /// <summary>Read, write and search for the user alone: 700.</summary>
    public const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Read and write for the user alone: 600.</summary>
    public const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Makes a folder of Meep's, and any folders above it that aren't there yet, readable by the user alone, and
    /// narrows the folder itself if it's there already and others can reach into it. Returns the folder.
    /// </summary>
    public static string Create(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(full);
            return full;
        }

        // Each missing folder is made with the mode, since Directory.CreateDirectory gives it only to the last.
        var missing = new Stack<string>();
        for (var at = full; !Directory.Exists(at); at = Path.GetDirectoryName(at)!)
        {
            missing.Push(at);
            if (Path.GetDirectoryName(at) is null)
                break;
        }
        if (missing.Count == 0)
        {
            Narrow(full);
            return full;
        }
        while (missing.TryPop(out var make))
            Directory.CreateDirectory(make, Folder);
        return full;
    }

    /// <summary>
    /// Narrows a folder that's there already to the user alone (700) if others can reach into it. Returns whether it
    /// did. Does nothing on Windows, or if the folder isn't there.
    /// </summary>
    public static bool Narrow(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (OperatingSystem.IsWindows() || !Directory.Exists(folder) || (System.IO.File.GetUnixFileMode(folder) & ~Folder) == 0)
            return false;
        System.IO.File.SetUnixFileMode(folder, Folder);
        return true;
    }

    /// <summary>
    /// Whether anyone but the user can change a file or folder, as OpenSSH asks of what it trusts. One its group can
    /// write to is still the user's alone if it's theirs and the group is their own, with no other members, as
    /// Debian's OpenSSH allows, since Debian gives each user a group of their own and a umask of 002 (Q284).
    /// </summary>
    public static bool OthersCanWrite(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;
        var mode = System.IO.File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.OtherWrite) != 0)
            return true;
        return (mode & UnixFileMode.GroupWrite) != 0 && !UserPrivateGroups.IsOwnersAlone(path);
    }

    /// <summary>Options for a new file that only the user can read and write, appended to or created.</summary>
    public static FileStreamOptions NewFile(FileMode mode) => OperatingSystem.IsWindows()
        ? new FileStreamOptions { Mode = mode, Access = FileAccess.Write }
        : new FileStreamOptions { Mode = mode, Access = FileAccess.Write, UnixCreateMode = File };
}
