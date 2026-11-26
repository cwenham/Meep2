using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// An SSH certificate authority's public key, as a <c>.pub</c> file has it: <c>ssh-ed25519 AAAA… comment</c> (Q303).
/// <c>Parse</c> reads one, and <c>Authorise.Authority</c> trusts it to issue credentials, adding it to the trust
/// store's <c>authorities</c>, as <c>meep trust add-authority</c> does.
/// </summary>
/// <remarks>
/// Only the key is read here, never checked: anyone can make one, and it means nothing until it's trusted. A
/// certificate, which is a key signed by an authority, is a <see cref="Credential"/> instead, and a private key isn't
/// read at all.
/// </remarks>
public sealed record AuthorityKey : Message
{
    // The key types Meep can check an authority's signature with.
    private static readonly HashSet<string> s_types = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
    };

    private AuthorityKey(string keyType, byte[] key, string? comment)
    {
        KeyType = keyType;
        Key = key;
        Comment = comment;
        Fingerprint = Credential.Fingerprint(key);
    }

    /// <summary>The key's type, such as <c>ssh-ed25519</c>.</summary>
    public string KeyType { get; }

    /// <summary>The public key in SSH's wire format, as the base64 in a <c>.pub</c> file holds it.</summary>
    public ReadOnlyMemory<byte> Key { get; }

    /// <summary>The comment after the key, such as <c>home-ca</c>, or null if there's none.</summary>
    public string? Comment { get; }

    /// <summary>The key's SHA-256 fingerprint, as <c>ssh-keygen -l</c> shows it.</summary>
    public string Fingerprint { get; }

    /// <summary>Whether <paramref name="type"/> is a kind of public key Meep reads.</summary>
    public static bool IsKeyType(ReadOnlySpan<char> type) => s_types.GetAlternateLookup<ReadOnlySpan<char>>().Contains(type);

    /// <summary>
    /// Reads a public key written as a <c>.pub</c> file has it: its type, its base64 and an optional comment. Throws
    /// <see cref="FormatException"/>, saying why, if it isn't one.
    /// </summary>
    public static AuthorityKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Trim().Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            throw new FormatException("A public key is written as its type, then its base64, as a .pub file has it: ssh-ed25519 AAAA… comment.");
        var type = words[0];
        if (type.EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal))
            throw new FormatException("This is a certificate, which is a credential, not an authority's key. An authority's key is a plain public key, as its .pub file has it.");
        if (!s_types.Contains(type))
            throw new FormatException($"{type} isn't a kind of public key Meep reads.");
        byte[] key;
        try
        {
            key = Convert.FromBase64String(words[1]);
        }
        catch (FormatException exception)
        {
            throw new FormatException("The key's base64 can't be read.", exception);
        }
        string inside;
        try
        {
            inside = new SshWireReader(key).ReadText();
        }
        catch (FormatException exception)
        {
            throw new FormatException($"The key's bytes aren't an SSH public key: {exception.Message}.", exception);
        }
        if (inside != type)
            throw new FormatException($"The key says it's {type}, but its bytes are {inside}.");
        return new AuthorityKey(type, key, words.Length > 2 ? words[2].Trim() : null);
    }

    /// <summary>The key as a <c>.pub</c> file has it: <c>type base64 comment</c>.</summary>
    public override string ToText() =>
        Comment is null ? $"{KeyType} {Convert.ToBase64String(Key.Span)}" : $"{KeyType} {Convert.ToBase64String(Key.Span)} {Comment}";
}
