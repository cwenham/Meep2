using System.Security.Cryptography;
using System.Text;

namespace Meep2.Plugins;

/// <summary>
/// An SSH signature, in the armored form <c>ssh-keygen -Y sign</c> writes (OpenSSH's PROTOCOL.sshsig).
/// </summary>
public sealed record SshSignature(PublicKey Key, string Namespace, string HashAlgorithm, byte[] Signature)
{
    private const string Begin = "-----BEGIN SSH SIGNATURE-----";
    private const string End = "-----END SSH SIGNATURE-----";
    private static readonly byte[] s_magic = "SSHSIG"u8.ToArray();

    /// <summary>Reads an armored signature.</summary>
    /// <exception cref="FormatException">It isn't one.</exception>
    public static SshSignature Parse(string armored)
    {
        ArgumentNullException.ThrowIfNull(armored);
        var text = armored.Trim();
        if (!text.StartsWith(Begin, StringComparison.Ordinal) || !text.EndsWith(End, StringComparison.Ordinal))
            throw new FormatException($"it doesn't start with {Begin} and end with {End}");
        var reader = new SshReader(Convert.FromBase64String(text[Begin.Length..^End.Length]));
        if (!reader.ReadRaw(6).SequenceEqual(s_magic) || reader.ReadUInt32() != 1)
            throw new FormatException("it isn't version 1 of SSH's signature format");
        var key = PublicKey.FromBlob("signer", reader.ReadBytes());
        var ns = reader.ReadText();
        reader.ReadBytes(); // reserved
        var hash = reader.ReadText();
        var signature = reader.ReadBytes();
        reader.End();
        if (hash is not ("sha256" or "sha512"))
            throw new FormatException($"it hashes with {hash}, and SSH signatures use sha256 or sha512");
        return new SshSignature(key, ns, hash, signature);
    }

    /// <summary>Whether it's a signature of this message by its key.</summary>
    public bool Verifies(byte[] message) => Key.Verify(SignedData(Namespace, HashAlgorithm, message), Signature);

    /// <summary>The armored form.</summary>
    public string ToArmored()
    {
        var writer = new SshWriter();
        writer.WriteRaw(s_magic);
        writer.WriteUInt32(1);
        writer.WriteBytes(Key.Blob);
        writer.WriteText(Namespace);
        writer.WriteBytes([]);
        writer.WriteText(HashAlgorithm);
        writer.WriteBytes(Signature);
        var base64 = Convert.ToBase64String(writer.ToArray());
        var text = new StringBuilder(Begin).Append('\n');
        for (var i = 0; i < base64.Length; i += 70)
            text.Append(base64.AsSpan(i, Math.Min(70, base64.Length - i))).Append('\n');
        return text.Append(End).Append('\n').ToString();
    }

    // What the key actually signs: the namespace, the hash algorithm and the message's hash.
    internal static byte[] SignedData(string ns, string hashAlgorithm, byte[] message)
    {
        var writer = new SshWriter();
        writer.WriteRaw(s_magic);
        writer.WriteText(ns);
        writer.WriteBytes([]);
        writer.WriteText(hashAlgorithm);
        writer.WriteBytes(hashAlgorithm == "sha256" ? SHA256.HashData(message) : SHA512.HashData(message));
        return writer.ToArray();
    }
}
