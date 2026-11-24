using System.Security.Cryptography;
using System.Text;
using Meep2.SDK.Security;

namespace Meep2.Security;

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
        return FromBinary(Convert.FromBase64String(text[Begin.Length..^End.Length]));
    }

    /// <summary>
    /// Reads a signature in its binary form, the bytes inside the armor, as grants and request signatures carry it.
    /// </summary>
    /// <exception cref="FormatException">It isn't one.</exception>
    public static SshSignature FromBinary(ReadOnlySpan<byte> binary)
    {
        var reader = new SshWireReader(binary);
        if (!reader.ReadRaw(6).SequenceEqual(s_magic) || reader.ReadUInt32() != 1)
            throw new FormatException("it isn't version 1 of SSH's signature format");
        var key = PublicKey.FromBlob("signer", reader.ReadString().ToArray());
        var ns = reader.ReadText();
        reader.ReadString().ToArray(); // reserved
        var hash = reader.ReadText();
        var signature = reader.ReadString().ToArray();
        reader.End();
        if (hash is not ("sha256" or "sha512"))
            throw new FormatException($"it hashes with {hash}, and SSH signatures use sha256 or sha512");
        return new SshSignature(key, ns, hash, signature);
    }

    /// <summary>Whether it's a signature of this message by its key.</summary>
    public bool Verifies(byte[] message) => Key.Verify(SignedData(Namespace, HashAlgorithm, message), Signature);

    /// <summary>The binary form, the bytes inside the armor.</summary>
    public byte[] ToBinary() =>
        new SshWireWriter().Raw(s_magic).UInt32(1).String(Key.Blob).Text(Namespace).String([]).Text(HashAlgorithm).String(Signature).ToArray();

    /// <summary>The armored form.</summary>
    public string ToArmored()
    {
        var base64 = Convert.ToBase64String(ToBinary());
        var text = new StringBuilder(Begin).Append('\n');
        for (var i = 0; i < base64.Length; i += 70)
            text.Append(base64.AsSpan(i, Math.Min(70, base64.Length - i))).Append('\n');
        return text.Append(End).Append('\n').ToString();
    }

    // What the key actually signs: the namespace, the hash algorithm and the message's hash.
    internal static byte[] SignedData(string ns, string hashAlgorithm, byte[] message)
    {
        return new SshWireWriter()
            .Raw(s_magic)
            .Text(ns)
            .String([])
            .Text(hashAlgorithm)
            .String(hashAlgorithm == "sha256" ? SHA256.HashData(message) : SHA512.HashData(message))
            .ToArray();
    }
}
