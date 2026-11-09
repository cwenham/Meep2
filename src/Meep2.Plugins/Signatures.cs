using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

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
    public PublicKey? Find(PublicKey key) => Keys.FirstOrDefault(trusted => trusted.SameKeyAs(key) && trusted.MaySignFor(PluginSignature.Namespace));
}

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

/// <summary>
/// A key pair Meep holds itself, for signing without <c>ssh-keygen</c>: tests and benchmarks use it. People sign with
/// <c>meep sign</c>, which uses <c>ssh-keygen</c> and so their own SSH keys, agents and passphrases.
/// </summary>
internal sealed class SshKeyPair : IDisposable
{
    private readonly Ed25519PrivateKeyParameters? _ed25519;
    private readonly ECDsa? _ecdsa;

    private SshKeyPair(string name, Ed25519PrivateKeyParameters key)
    {
        _ed25519 = key;
        var writer = new SshWriter();
        writer.WriteText("ssh-ed25519");
        writer.WriteBytes(key.GeneratePublicKey().GetEncoded());
        Public = PublicKey.FromBlob(name, writer.ToArray());
    }

    private SshKeyPair(string name, ECDsa key)
    {
        _ecdsa = key;
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var writer = new SshWriter();
        writer.WriteText("ecdsa-sha2-nistp256");
        writer.WriteText("nistp256");
        writer.WriteBytes([4, .. parameters.Q.X!, .. parameters.Q.Y!]);
        Public = PublicKey.FromBlob(name, writer.ToArray());
    }

    public PublicKey Public { get; }

    public static SshKeyPair CreateEd25519(string name) => new(name, new Ed25519PrivateKeyParameters(new SecureRandom()));

    public static SshKeyPair CreateEcdsa(string name) => new(name, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Signs a message as <c>ssh-keygen -Y sign -n {ns}</c> would, with SHA-512.</summary>
    public SshSignature Sign(byte[] message, string ns)
    {
        var data = SshSignature.SignedData(ns, "sha512", message);
        var writer = new SshWriter();
        if (_ed25519 is not null)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, _ed25519);
            signer.BlockUpdate(data, 0, data.Length);
            writer.WriteText("ssh-ed25519");
            writer.WriteBytes(signer.GenerateSignature());
        }
        else
        {
            var fixedSize = _ecdsa!.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var numbers = new SshWriter();
            numbers.WriteMpint(fixedSize[..32]);
            numbers.WriteMpint(fixedSize[32..]);
            writer.WriteText("ecdsa-sha2-nistp256");
            writer.WriteBytes(numbers.ToArray());
        }
        return new SshSignature(Public, ns, "sha512", writer.ToArray());
    }

    public void Dispose() => _ecdsa?.Dispose();
}

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
        var signer = trusted.Find(signature.Key)
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

/// <summary>Reads SSH's wire format: big-endian lengths before strings and numbers.</summary>
internal ref struct SshReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _rest = data;

    public uint ReadUInt32()
    {
        if (_rest.Length < 4)
            throw new FormatException("it ends too soon");
        var value = BinaryPrimitives.ReadUInt32BigEndian(_rest);
        _rest = _rest[4..];
        return value;
    }

    public ReadOnlySpan<byte> ReadRaw(int length)
    {
        if (_rest.Length < length)
            throw new FormatException("it ends too soon");
        var value = _rest[..length];
        _rest = _rest[length..];
        return value;
    }

    public byte[] ReadBytes()
    {
        var length = ReadUInt32();
        if (length > (uint)_rest.Length)
            throw new FormatException("it ends too soon");
        return ReadRaw((int)length).ToArray();
    }

    public string ReadText() => Encoding.UTF8.GetString(ReadBytes());

    // An unsigned mpint, without the leading zero that keeps its top bit clear.
    public byte[] ReadMpint()
    {
        var bytes = ReadBytes();
        if (bytes.Length > 0 && (bytes[0] & 0x80) != 0)
            throw new FormatException("it has a negative number");
        var start = 0;
        while (start < bytes.Length && bytes[start] == 0)
            start++;
        return bytes[start..];
    }

    public readonly void End()
    {
        if (!_rest.IsEmpty)
            throw new FormatException("it has bytes left over");
    }
}

/// <summary>Writes SSH's wire format.</summary>
internal sealed class SshWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteUInt32((uint)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteText(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));

    // An unsigned number as an mpint: no leading zeros, except one to keep the top bit clear.
    public void WriteMpint(ReadOnlySpan<byte> unsigned)
    {
        var start = 0;
        while (start < unsigned.Length && unsigned[start] == 0)
            start++;
        var digits = unsigned[start..];
        if (!digits.IsEmpty && (digits[0] & 0x80) != 0)
            WriteBytes([0, .. digits]);
        else
            WriteBytes(digits);
    }

    public byte[] ToArray() => _stream.ToArray();
}
