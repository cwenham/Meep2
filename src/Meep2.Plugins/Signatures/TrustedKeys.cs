namespace Meep2.Plugins;

/// <summary>
/// The keys whose plugins you trust: Meep's allowed-signers file, <c>~/.meep/allowed_signers</c>, in the form
/// <c>ssh-keygen -Y verify</c> reads. Lines starting with <c>#</c> are comments.
/// </summary>
public sealed class TrustedKeys
{
    /// <summary>Trusts these keys.</summary>
    public TrustedKeys(IEnumerable<PublicKey> keys) => Keys = [.. keys];

    /// <summary>No keys at all.</summary>
    public static TrustedKeys None { get; } = new([]);

    /// <summary>The keys.</summary>
    public IReadOnlyList<PublicKey> Keys { get; }

    /// <summary>Reads an allowed-signers file, or no keys if there isn't one.</summary>
    public static TrustedKeys Load(string path)
    {
        if (!File.Exists(path))
            return None;
        // As OpenSSH does, a list that someone else could have added a key to isn't trusted (S7).
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Meep2.SDK.Modules.PrivateFolders.OthersCanWrite(path) || Meep2.SDK.Modules.PrivateFolders.OthersCanWrite(folder))
        {
            throw new PluginException(
                $"{path} can be changed by other users, so Meep doesn't trust the keys in it. Make it yours alone with chmod go-w \"{path}\" \"{folder}\".");
        }
        var keys = new List<PublicKey>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Trim() is { Length: 0 } or ['#', ..])
                continue;
            try
            {
                keys.Add(PublicKey.Parse(line));
            }
            catch (FormatException exception)
            {
                throw new PluginException($"Line {number} of {path}: {exception.Message}", exception);
            }
        }
        return new TrustedKeys(keys);
    }

    /// <summary>The trusted key that's the same as this one and may sign plugins, if there is one.</summary>
    public PublicKey? Find(PublicKey key) => Find(key, PluginSignature.Namespace);

    /// <summary>The trusted key that matches, if it may sign for <paramref name="ns"/>, or null.</summary>
    public PublicKey? Find(PublicKey key, string ns) => Keys.FirstOrDefault(trusted => trusted.SameKeyAs(key) && trusted.MaySignFor(ns));
}
