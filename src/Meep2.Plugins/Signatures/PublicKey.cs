using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Meep2.Plugins;

/// <summary>
/// An SSH public key, for checking plugin signatures (Q101): <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>, 384 or
/// 521, or <c>ssh-rsa</c> of at least 2048 bits. Written as <c>name type base64</c>, a line of the allowed-signers file
/// <c>ssh-keygen -Y verify</c> reads.
/// </summary>
public sealed class PublicKey
{
    private const string Ed25519 = "ssh-ed25519";
    private const string Rsa = "ssh-rsa";

    private readonly byte[]? _ed25519;
    private readonly ECParameters _ecdsa;
    private readonly RSAParameters _rsa;

    private PublicKey(string name, byte[] blob)
    {
        Name = name;
        Blob = blob;
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        var reader = new SshReader(blob);
        Type = reader.ReadText();
        switch (Type)
        {
            case Ed25519:
                _ed25519 = reader.ReadBytes();
                if (_ed25519.Length != 32)
                    throw new FormatException("its Ed25519 key isn't 32 bytes");
                break;
            case "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521":
            {
                var curve = reader.ReadText();
                if (curve != Type["ecdsa-sha2-".Length..])
                    throw new FormatException($"its curve, {curve}, doesn't match its type, {Type}");
                var point = reader.ReadBytes();
                var size = FieldSize(Type);
                if (point.Length != 1 + 2 * size || point[0] != 4)
                    throw new FormatException("its point isn't an uncompressed point on its curve");
                _ecdsa = new ECParameters
                {
                    Curve = Curve(Type),
                    Q = new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] },
                };
                ECDsa.Create(_ecdsa).Dispose(); // importing checks the point is on the curve
                break;
            }
            case Rsa:
            {
                var exponent = reader.ReadMpint();
                var modulus = reader.ReadMpint();
                if (modulus.Length * 8 < 2048)
                    throw new FormatException($"it's an RSA key of {modulus.Length * 8} bits, and plugins need at least 2048");
                _rsa = new RSAParameters { Exponent = exponent, Modulus = modulus };
                break;
            }
            default:
                throw new FormatException(Type.StartsWith("sk-", StringComparison.Ordinal)
                    ? $"it's a security key ({Type}), which can't sign plugins yet"
                    : $"{Type} isn't a key type Meep knows. It knows ssh-ed25519, ecdsa-sha2-nistp256, 384 and 521, and ssh-rsa");
        }
        reader.End();
    }

    /// <summary>A label for the key, such as its owner's name. Only for display: the key itself is what's checked.</summary>
    public string Name { get; }

    /// <summary>The key's type, such as <c>ssh-ed25519</c>.</summary>
    public string Type { get; }

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
    public static PublicKey Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = Words(line.Trim());
        if (words.Count < 3)
            throw new FormatException($"A key is written as: name type base64, as in alice ssh-ed25519 AAAAC3Nz…. This isn't one: {line.Trim()}");
        var name = words[0];
        var options = words.Count > 3 && !LooksLikeType(words[1]) ? words[1] : null;
        var at = options is null ? 1 : 2;
        var key = Parse(name, string.Join(' ', words.Skip(at)));
        return options is null ? key : key.WithOptions(options);
    }

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
            var reader = new SshReader(signature);
            var algorithm = reader.ReadText();
            var signed = reader.ReadBytes();
            reader.End();
            switch (Type)
            {
                case Ed25519 when algorithm == Ed25519:
                {
                    var verifier = new Ed25519Signer();
                    verifier.Init(false, new Ed25519PublicKeyParameters(_ed25519!));
                    verifier.BlockUpdate(data, 0, data.Length);
                    return verifier.VerifySignature(signed);
                }
                case Rsa when algorithm is "rsa-sha2-256" or "rsa-sha2-512":
                {
                    using var rsa = RSA.Create(_rsa);
                    var hash = algorithm == "rsa-sha2-256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512;
                    return rsa.VerifyData(data, signed, hash, RSASignaturePadding.Pkcs1);
                }
                case not (Ed25519 or Rsa) when algorithm == Type:
                {
                    var size = FieldSize(Type);
                    var numbers = new SshReader(signed);
                    var r = numbers.ReadMpint();
                    var s = numbers.ReadMpint();
                    numbers.End();
                    if (r.Length > size || s.Length > size)
                        return false;
                    var fixedSize = new byte[2 * size];
                    r.CopyTo(fixedSize, size - r.Length);
                    s.CopyTo(fixedSize, 2 * size - s.Length);
                    using var ecdsa = ECDsa.Create(_ecdsa);
                    return ecdsa.VerifyData(data, fixedSize, Hash(Type), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
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

    private static bool LooksLikeType(string word) =>
        word is Ed25519 or Rsa || word.StartsWith("ecdsa-", StringComparison.Ordinal) || word.StartsWith("sk-", StringComparison.Ordinal);

    private PublicKey WithOptions(string options)
    {
        string[]? namespaces = null;
        foreach (var option in Split(options, ','))
        {
            if (option.StartsWith("namespaces=", StringComparison.OrdinalIgnoreCase))
                namespaces = option["namespaces=".Length..].Trim('"').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return new PublicKey(Name, Blob) { Namespaces = namespaces };
    }

    private static List<string> Words(string text) => Split(text, ' ');

    // Splits at a separator (or any space, for ' '), keeping quoted text whole, with its quotes.
    private static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
                quoted = !quoted;
            if (!quoted && (c == separator || (separator == ' ' && char.IsWhiteSpace(c))))
            {
                if (current.Length > 0)
                    parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}
