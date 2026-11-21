namespace Meep2.SDK.Modules;

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
            if ((System.IO.File.GetUnixFileMode(full) & ~Folder) != 0)
                System.IO.File.SetUnixFileMode(full, Folder);
            return full;
        }
        while (missing.TryPop(out var make))
            Directory.CreateDirectory(make, Folder);
        return full;
    }

    /// <summary>Whether anyone but the user can change a file or folder, as OpenSSH asks of what it trusts.</summary>
    public static bool OthersCanWrite(string path) =>
        !OperatingSystem.IsWindows()
        && (System.IO.File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;

    /// <summary>Options for a new file that only the user can read and write, appended to or created.</summary>
    public static FileStreamOptions NewFile(FileMode mode) => OperatingSystem.IsWindows()
        ? new FileStreamOptions { Mode = mode, Access = FileAccess.Write }
        : new FileStreamOptions { Mode = mode, Access = FileAccess.Write, UnixCreateMode = File };
}
