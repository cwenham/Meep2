using System.Security.Cryptography;
using System.Text;

namespace Meep2.Daemon;

/// <summary>
/// Tokens that give a client of the daemon chosen capabilities, kept in <c>~/.meep/daemon/tokens</c> as their
/// SHA-256s, so the file doesn't hold the tokens themselves.
/// </summary>
public sealed class DaemonTokens
{
    private const string Prefix = "meep_";
    private readonly List<(string Name, Capabilities Capabilities, byte[] Hash)> _tokens;

    private DaemonTokens(List<(string, Capabilities, byte[])> tokens) => _tokens = tokens;

    /// <summary>Reads the tokens file, or none if there isn't one.</summary>
    public static DaemonTokens Load(string path)
    {
        var tokens = new List<(string, Capabilities, byte[])>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var name, var capabilities, ['s', 'h', 'a', '2', '5', '6', ':', .. var hash]])
                    tokens.Add((name, CapabilityNames.Parse(capabilities), Convert.FromHexString(hash)));
            }
        }
        return new DaemonTokens(tokens);
    }

    /// <summary>Makes a token, saves its hash, and returns the token, which is only shown this once.</summary>
    public static string Create(string path, string name, Capabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsWhiteSpace))
            throw new FormatException("A token's name can't have spaces.");
        var token = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Meep2.Shared.PrivateFolders.Create(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var line = $"{name} {string.Join(',', CapabilityNames.Of(capabilities))} sha256:{Convert.ToHexStringLower(Hash(token))}{Environment.NewLine}";
        // Made for you alone from the start, rather than narrowed after (S7).
        using (var file = new FileStream(path, Meep2.Shared.PrivateFolders.NewFile(FileMode.Append)))
            file.Write(System.Text.Encoding.UTF8.GetBytes(line));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // one made before this
        return token;
    }

    /// <summary>The name and capabilities a token gives, or null if it isn't one.</summary>
    public (string Name, Capabilities Capabilities)? Find(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var hash = Hash(token);
        foreach (var (name, capabilities, saved) in _tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, saved))
                return (name, capabilities);
        }
        return null;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
