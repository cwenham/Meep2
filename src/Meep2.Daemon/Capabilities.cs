using System.Security.Cryptography;
using System.Text;

namespace Meep2.Daemon;

/// <summary>
/// What a client of the daemon may do (Q40, Q63). The user running the daemon may do everything. A token can give a
/// client less, such as a dashboard that should only observe.
/// </summary>
[Flags]
public enum Capabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>List pipelines, watch their modules' counts, and tap the messages passing a module.</summary>
    Observe = 1,

    /// <summary>Send test messages into a running pipeline (Q72).</summary>
    Inject = 2,

    /// <summary>Start and stop pipelines, and stop the daemon.</summary>
    Control = 4,

    /// <summary>Everything.</summary>
    All = Observe | Inject | Control,
}

/// <summary>Reads and writes capabilities as words: <c>observe,inject</c>, or <c>all</c>.</summary>
public static class CapabilityNames
{
    /// <summary>The capabilities as lowercase words.</summary>
    public static IReadOnlyList<string> Of(Capabilities capabilities) =>
        [.. Enum.GetValues<Capabilities>()
            .Where(c => c is not (Capabilities.None or Capabilities.All) && capabilities.HasFlag(c))
            .Select(c => c.ToString().ToLowerInvariant())];

    /// <summary>Reads words such as <c>observe,inject</c>, or <c>all</c>.</summary>
    /// <exception cref="FormatException">One isn't a capability.</exception>
    public static Capabilities Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var capabilities = Capabilities.None;
        foreach (var word in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            capabilities |= Enum.TryParse<Capabilities>(word, ignoreCase: true, out var one) && one != Capabilities.None
                ? one
                : throw new FormatException($"{word} isn't a capability. They're observe, inject, control, or all.");
        }
        return capabilities;
    }
}

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
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(folder);
        var line = $"{name} {string.Join(',', CapabilityNames.Of(capabilities))} sha256:{Convert.ToHexStringLower(Hash(token))}{Environment.NewLine}";
        File.AppendAllText(path, line);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
