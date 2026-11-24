using System.Security.Cryptography;
using System.Text;
using Meep2.Security;

namespace Meep2.Plugins;

/// <summary>
/// A plugin's signature (Q101): <c>meep-files.txt</c>, listing each of its files with its SHA-256 as
/// <c>shasum -a 256</c> does, and <c>meep-files.txt.sig</c>, an SSH signature of that list made with
/// <c>ssh-keygen -Y sign -n meep-plugin</c>. They travel inside the plugin, so they work the same for a GitHub zip,
/// a NuGet package or a folder on this machine.
/// </summary>
public static class PluginSignature
{
    /// <summary>The list of files and their SHA-256s.</summary>
    public const string ListName = "meep-files.txt";

    /// <summary>The SSH signature of the list.</summary>
    public const string FileName = ListName + ".sig";

    /// <summary>The signature namespace, which keeps a plugin signature from passing as any other kind.</summary>
    public const string Namespace = "meep-plugin";

    /// <summary>
    /// Writes <c>meep-files.txt</c> into a folder, listing the files in it and below, or just the ones given, with
    /// their SHA-256s. Returns its path, for <c>ssh-keygen -Y sign</c> to sign.
    /// </summary>
    public static string WriteList(string folder, IEnumerable<string>? files = null)
    {
        folder = Path.GetFullPath(folder);
        var paths = (files ?? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Select(path => Relative(folder, path))
            .Where(path => path is not (ListName or FileName) && !Path.GetFileName(path).StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var text = new StringBuilder();
        foreach (var path in paths)
            text.Append(Hash(Path.Combine(folder, path))).Append("  ").Append(path).Append('\n');
        var output = Path.Combine(folder, ListName);
        File.WriteAllText(output, text.ToString());
        return output;
    }

    /// <summary>Lists and signs a plugin's files with a key Meep holds, rather than with ssh-keygen. Returns the signature's path.</summary>
    internal static string Sign(string folder, SshKeyPair key, IEnumerable<string>? files = null)
    {
        var list = WriteList(folder, files);
        var signature = key.Sign(File.ReadAllBytes(list), Namespace);
        var output = Path.Combine(Path.GetDirectoryName(list)!, FileName);
        File.WriteAllText(output, signature.ToArmored());
        return output;
    }

    /// <summary>The signature in a folder, without checking it, or null if there isn't one.</summary>
    /// <exception cref="FormatException">There's a signature, but Meep can't read it.</exception>
    public static SshSignature? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? SshSignature.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Checks a plugin's signature: that a trusted key signed its list of files, that each file listed is unchanged,
    /// and that every file <paramref name="mustBeCovered"/> picks out is listed. Returns the trusted key.
    /// </summary>
    /// <exception cref="UnsignedPluginException">The plugin isn't signed.</exception>
    /// <exception cref="PluginException">The signature doesn't hold.</exception>
    public static PublicKey Verify(string folder, TrustedKeys trusted, Func<string, bool> mustBeCovered, string plugin)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(mustBeCovered);
        var listPath = Path.Combine(folder, ListName);
        if (!File.Exists(Path.Combine(folder, FileName)))
        {
            throw new UnsignedPluginException(File.Exists(listPath)
                ? $"{plugin} isn't signed: it has a {ListName}, but no {FileName}."
                : $"{plugin} isn't signed: it has no {FileName}.");
        }
        if (!File.Exists(listPath))
            throw new PluginException($"{plugin} has a {FileName}, but not the {ListName} it signs.");

        SshSignature signature;
        try
        {
            signature = Read(folder)!;
        }
        catch (FormatException exception)
        {
            throw new PluginException($"{plugin}'s {FileName} isn't a signature Meep can read: {exception.Message}.", exception);
        }
        if (signature.Namespace != Namespace)
            throw new PluginException($"{plugin} is signed for {signature.Namespace}, not for Meep plugins. Sign it with -n {Namespace}.");
        var signer = trusted.Find(signature.Key, Namespace)
            ?? throw new UntrustedSignatureException(
                $"{plugin} is signed with the key {signature.Key.Fingerprint}, which isn't one you trust. If you trust its author, add their key with meep keys trust.");
        var list = File.ReadAllBytes(listPath);
        if (!signature.Verifies(list))
            throw new PluginException($"{plugin}'s signature doesn't match its {ListName}, so it may have been tampered with.");

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(list).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // shasum's form: the hash, two spaces (or a space and *), and the path.
            var (hash, file) = line.Length > 66 && line[64] == ' ' && line[65] is ' ' or '*' ? (line[..64], line[66..].TrimEnd('\r')) : ("", "");
            if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file))
                throw new PluginException($"{plugin}'s {ListName} has a line Meep can't read: {line}");
            var full = Path.Combine(folder, file);
            if (!File.Exists(full))
                throw new PluginException($"{plugin} is missing {file}, which its signature lists.");
            if (!string.Equals(Hash(full), hash, StringComparison.OrdinalIgnoreCase))
                throw new PluginException($"{plugin}'s {file} has changed since it was signed.");
            listed.Add(file);
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Relative(folder, f)))
        {
            if (file is not (ListName or FileName) && !listed.Contains(file) && mustBeCovered(file))
                throw new PluginException($"{plugin} has {file}, which its signature doesn't cover.");
        }
        return signer;
    }

    /// <summary>A file's SHA-256, in lowercase hex.</summary>
    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    internal static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');
}
