namespace Meep2.SDK.Modules;

/// <summary>The user's home folder: paths written with <c>~</c>, and the SSH key Meep signs with by default.</summary>
public static class HomeFolder
{
    /// <summary>The home folder of the user who runs Meep.</summary>
    public static string Path => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// A path with <c>~</c>, <c>~/</c> or <c>~\</c> at its start read as the home folder; any other path as it is.
    /// </summary>
    public static string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? System.IO.Path.Join(Path, path[1..].TrimStart('/', '\\'))
            : path;
    }

    /// <summary>
    /// The SSH private key to sign with when none is named: the first of <c>~/.ssh/id_ed25519</c>, <c>id_ecdsa</c> and
    /// <c>id_rsa</c> that's there, or null.
    /// </summary>
    public static string? SshKey()
    {
        var ssh = System.IO.Path.Join(Path, ".ssh");
        return new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(name => System.IO.Path.Join(ssh, name)).FirstOrDefault(File.Exists);
    }
}
