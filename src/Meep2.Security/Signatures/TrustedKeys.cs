using Meep2.Shared;

namespace Meep2.Security;

/// <summary>
/// Keys you trust, each for some signature namespaces: an allowed-signers file, in the form <c>ssh-keygen -Y verify</c>
/// reads, such as <c>~/.meep/allowed_signers</c> for plugins and pipelines, or the trust store's <c>authorities</c>.
/// Lines starting with <c>#</c> are comments. A file others could change is refused with a
/// <see cref="TrustStoreException"/>.
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
        if (PrivateFolders.OthersCanWrite(path) || PrivateFolders.OthersCanWrite(folder))
        {
            throw new TrustStoreException(
                $"{path} can be changed by other users, so Meep doesn't trust the keys in it. Make it yours alone with chmod go-w \"{path}\" \"{folder}\".");
        }
        return Parse(path, File.ReadLines(path));
    }

    /// <summary>Reads the lines of an allowed-signers file at <paramref name="path"/>, which is named in what's thrown.</summary>
    internal static TrustedKeys Parse(string path, IEnumerable<string> lines)
    {
        var keys = new List<PublicKey>();
        var number = 0;
        foreach (var line in lines)
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
                throw new TrustStoreException($"Line {number} of {path}: {exception.Message}", exception);
            }
        }
        return new TrustedKeys(keys);
    }

    /// <summary>The trusted key that matches, if it may sign for <paramref name="ns"/>, or null.</summary>
    public PublicKey? Find(PublicKey key, string ns) => Keys.FirstOrDefault(trusted => trusted.SameKeyAs(key) && trusted.MaySignFor(ns));
}
