using System.Security.Cryptography;
using Meep2.SDK.Security;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Meep2.Security;

/// <summary>
/// An SSH public key, for checking signatures: <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>, 384 or 521,
/// <c>ssh-rsa</c> of at least 2048 bits, and the security-key kinds <c>sk-ssh-ed25519@openssh.com</c> and
/// <c>sk-ecdsa-sha2-nistp256@openssh.com</c> that <c>ssh-keygen -t ed25519-sk</c> makes on a YubiKey (Q271). Written as
/// <c>name type base64</c>, a line of the allowed-signers file <c>ssh-keygen -Y verify</c> reads.
/// </summary>
/// <remarks>
/// A security key signs a hash of its application (usually <c>ssh:</c>), some flags and a counter along with the
/// message. A signature is only good if the flags say a person touched the key (user presence), as OpenSSH insists
/// unless told otherwise.
/// </remarks>
public sealed class PublicKey
{
    private const string Ed25519 = "ssh-ed25519";
    private const string Rsa = "ssh-rsa";
    private const string SkEd25519 = "sk-ssh-ed25519@openssh.com";
    private const string SkEcdsa = "sk-ecdsa-sha2-nistp256@openssh.com";

    // The flag a security key sets when a person touched it to sign.
    private const byte UserPresent = 0x01;

    private readonly byte[]? _ed25519;
    private readonly ECParameters _ecdsa;
    private readonly RSAParameters _rsa;
    private readonly string? _application;

    private PublicKey(string name, byte[] blob)
    {
        Name = name;
        Blob = blob;
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        var reader = new SshWireReader(blob);
        Type = reader.ReadText();
        switch (Type)
        {
            case Ed25519:
                _ed25519 = reader.ReadString().ToArray();
                if (_ed25519.Length != 32)
                    throw new FormatException("its Ed25519 key isn't 32 bytes");
                break;
            case "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521":
                _ecdsa = ReadEcdsa(ref reader, Type);
                break;
            case Rsa:
                _rsa = ReadRsa(ref reader);
                break;
            case SkEd25519:
                _ed25519 = reader.ReadString().ToArray();
                if (_ed25519.Length != 32)
                    throw new FormatException("its Ed25519 key isn't 32 bytes");
                _application = reader.ReadText();
                break;
            case SkEcdsa:
                _ecdsa = ReadEcdsa(ref reader, "ecdsa-sha2-nistp256");
                _application = reader.ReadText();
                break;
            default:
                throw new FormatException($"{Type} isn't a key type Meep knows. It knows ssh-ed25519, ecdsa-sha2-nistp256, 384 and 521, ssh-rsa, and the security keys {SkEd25519} and {SkEcdsa}");
        }
        reader.End();
    }

    // An ECDSA key's curve and point, checked against its type and its curve.
    private static ECParameters ReadEcdsa(ref SshWireReader reader, string type)
    {
        var curve = reader.ReadText();
        if (curve != type["ecdsa-sha2-".Length..])
            throw new FormatException($"its curve, {curve}, doesn't match its type, {type}");
        var point = reader.ReadString().ToArray();
        var size = FieldSize(type);
        if (point.Length != 1 + 2 * size || point[0] != 4)
            throw new FormatException("its point isn't an uncompressed point on its curve");
        var parameters = new ECParameters
        {
            Curve = Curve(type),
            Q = new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] },
        };
        ECDsa.Create(parameters).Dispose(); // importing checks the point is on the curve
        return parameters;
    }

    // An RSA key's exponent and modulus, of at least 2048 bits.
    private static RSAParameters ReadRsa(ref SshWireReader reader)
    {
        var exponent = reader.ReadMpint();
        var modulus = reader.ReadMpint();
        if (modulus.Length * 8 < 2048)
            throw new FormatException($"it's an RSA key of {modulus.Length * 8} bits, and plugins need at least 2048");
        return new RSAParameters { Exponent = exponent, Modulus = modulus };
    }

    /// <summary>A label for the key, such as its owner's name. Only for display: the key itself is what's checked.</summary>
    public string Name { get; }

    /// <summary>The key's type, such as <c>ssh-ed25519</c>.</summary>
    public string Type { get; }

    /// <summary>Whether it's on a security key, such as a YubiKey, which signs only when touched.</summary>
    public bool IsSecurityKey => _application is not null;

    /// <summary>The key in SSH's wire format, as the base64 in a <c>.pub</c> file holds it.</summary>
    public byte[] Blob { get; }

    /// <summary>The key's SHA-256 fingerprint, as <c>ssh-keygen -l</c> shows it: <c>SHA256:</c> and base64.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// The signature namespaces the allowed-signers file lets this key sign for, or null for any. A key whose list
    /// leaves out <c>meep-plugin</c> isn't trusted for plugins.
    /// </summary>
    public IReadOnlyList<string>? Namespaces { get; init; }

    /// <summary>The key as a <c>.pub</c> file has it, without a comment: <c>type base64</c>.</summary>
    public string Text => $"{Type} {Convert.ToBase64String(Blob)}";

    /// <summary>
    /// Reads one line of an allowed-signers file: <c>name [options] type base64 [comment]</c>. Of the options, only
    /// <c>namespaces</c> is used.
    /// </summary>
    public static PublicKey Parse(string line) => AllowedSignersLine.Parse(line);

    /// <summary>Reads a key as a <c>.pub</c> file or GitHub has it, <c>type base64 [comment]</c>, and gives it a name.</summary>
    public static PublicKey Parse(string name, string key)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException($"An SSH public key is written as: type base64, as in ssh-ed25519 AAAAC3Nz…. This isn't one: {key.Trim()}");
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new FormatException($"{name}'s key isn't valid base64.");
        }
        var parsed = FromBlob(name, blob);
        if (parsed.Type != parts[0])
            throw new FormatException($"{name}'s key says it's {parts[0]}, but it's {parsed.Type}.");
        return parsed;
    }

    /// <summary>Reads a key from SSH's wire format.</summary>
    public static PublicKey FromBlob(string name, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        try
        {
            return new PublicKey(name, blob);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new FormatException($"{name}'s key can't be used: {exception.Message}.", exception);
        }
    }

    /// <summary>Whether this is the same key, whatever it's called.</summary>
    public bool SameKeyAs(PublicKey other) => other is not null && Blob.AsSpan().SequenceEqual(other.Blob);

    /// <summary>Whether the allowed-signers file lets this key sign for a namespace.</summary>
    public bool MaySignFor(string ns) => Namespaces is null || Namespaces.Any(allowed => allowed == "*" || allowed == ns);

    /// <summary>
    /// Checks an SSH signature over some data. The signature is in SSH's wire format: the algorithm's name, then the
    /// signature itself.
    /// </summary>
    public bool Verify(byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            var reader = new SshWireReader(signature);
            var algorithm = reader.ReadText();
            var signed = reader.ReadString().ToArray();
            if (IsSecurityKey)
                return algorithm == Type && VerifySecurityKey(data, signed, ref reader);
            reader.End();
            return Type switch
            {
                Ed25519 when algorithm == Ed25519 => VerifyEd25519(data, signed),
                Rsa when algorithm is "rsa-sha2-256" or "rsa-sha2-512" => VerifyRsa(data, signed, algorithm),
                not (Ed25519 or Rsa) when algorithm == Type => VerifyEcdsa(data, signed, Type),
                _ => false,
            };
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // A security key's signature is followed by its flags and counter, and covers a hash of its application, the
    // flags, the counter and a hash of the data, rather than the data itself (OpenSSH's PROTOCOL.u2f).
    private bool VerifySecurityKey(byte[] data, byte[] signed, ref SshWireReader reader)
    {
        var flags = reader.ReadByte();
        var counter = reader.ReadUInt32();
        reader.End();
        if ((flags & UserPresent) == 0)
            return false;
        var covered = new SshWireWriter()
            .Raw(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_application!)))
            .Raw([flags])
            .UInt32(counter)
            .Raw(SHA256.HashData(data))
            .ToArray();
        return Type == SkEd25519 ? VerifyEd25519(covered, signed) : VerifyEcdsa(covered, signed, "ecdsa-sha2-nistp256");
    }

    private bool VerifyEd25519(byte[] data, byte[] signed)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_ed25519!));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signed);
    }

    private bool VerifyRsa(byte[] data, byte[] signed, string algorithm)
    {
        using var rsa = RSA.Create(_rsa);
        var hash = algorithm == "rsa-sha2-256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512;
        return rsa.VerifyData(data, signed, hash, RSASignaturePadding.Pkcs1);
    }

    // An ECDSA signature is two numbers, r and s, which .NET takes as one fixed-size field each.
    private bool VerifyEcdsa(byte[] data, byte[] signed, string curveType)
    {
        var size = FieldSize(curveType);
        var numbers = new SshWireReader(signed);
        var r = numbers.ReadMpint();
        var s = numbers.ReadMpint();
        numbers.End();
        if (r.Length > size || s.Length > size)
            return false;
        var fixedSize = new byte[2 * size];
        r.CopyTo(fixedSize, size - r.Length);
        s.CopyTo(fixedSize, 2 * size - s.Length);
        using var ecdsa = ECDsa.Create(_ecdsa);
        return ecdsa.VerifyData(data, fixedSize, Hash(curveType), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>The key's line in an allowed-signers file: <c>name type base64</c>.</summary>
    public override string ToString() => $"{Name} {Text}";

    internal static int FieldSize(string type) => type switch
    {
        "ecdsa-sha2-nistp256" => 32,
        "ecdsa-sha2-nistp384" => 48,
        _ => 66,
    };

    internal static ECCurve Curve(string type) => type switch
    {
        "ecdsa-sha2-nistp256" => ECCurve.NamedCurves.nistP256,
        "ecdsa-sha2-nistp384" => ECCurve.NamedCurves.nistP384,
        _ => ECCurve.NamedCurves.nistP521,
    };

    internal static HashAlgorithmName Hash(string type) => type switch
    {
        "ecdsa-sha2-nistp256" => HashAlgorithmName.SHA256,
        "ecdsa-sha2-nistp384" => HashAlgorithmName.SHA384,
        _ => HashAlgorithmName.SHA512,
    };

    /// <summary>The same key, allowed to sign only for <paramref name="namespaces"/>, or for any if null.</summary>
    internal PublicKey WithNamespaces(IReadOnlyList<string>? namespaces) => new(Name, Blob) { Namespaces = namespaces };

    /// <summary>Whether a word is a key type, rather than an allowed-signers line's options.</summary>
    internal static bool IsType(string word) =>
        word is Ed25519 or Rsa || word.StartsWith("ecdsa-", StringComparison.Ordinal) || word.StartsWith("sk-", StringComparison.Ordinal);
}
