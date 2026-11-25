using System.Security.Cryptography;
using Meep2.SDK.Security;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Meep2.Security;

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
        Public = PublicKey.FromBlob(name, new SshWireWriter().Text("ssh-ed25519").String(key.GeneratePublicKey().GetEncoded()).ToArray());
    }

    private SshKeyPair(string name, ECDsa key)
    {
        _ecdsa = key;
        var parameters = key.ExportParameters(includePrivateParameters: false);
        Public = PublicKey.FromBlob(name, new SshWireWriter().Text("ecdsa-sha2-nistp256").Text("nistp256").String([4, .. parameters.Q.X!, .. parameters.Q.Y!]).ToArray());
    }

    public PublicKey Public { get; }

    public static SshKeyPair CreateEd25519(string name) => new(name, new Ed25519PrivateKeyParameters(new SecureRandom()));

    public static SshKeyPair CreateEcdsa(string name) => new(name, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Reads an unencrypted Ed25519 private key in OpenSSH's format, as <c>ssh-keygen -t ed25519 -N ""</c> writes it,
    /// such as this machine's authority key. Throws <see cref="FormatException"/>, saying why, for anything else: an
    /// encrypted key, another kind of key, or text that isn't one.
    /// </summary>
    public static SshKeyPair FromOpenSsh(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string Begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        const string End = "-----END OPENSSH PRIVATE KEY-----";
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < start)
            throw new FormatException("It isn't an OpenSSH private key.");
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(text[(start + Begin.Length)..end]);
        }
        catch (FormatException)
        {
            throw new FormatException("Its base64 isn't valid.");
        }
        return new(name, ReadEd25519(blob));
    }

    // openssh-key-v1: the cipher and KDF (none, unencrypted), one public key, then the private section: two equal
    // check numbers, the key's type, its public key, its private key (the seed and the public key), and a comment.
    private static Ed25519PrivateKeyParameters ReadEd25519(byte[] blob)
    {
        const string Magic = "openssh-key-v1\0";
        var reader = new SshWireReader(blob);
        if (blob.Length < Magic.Length || System.Text.Encoding.ASCII.GetString(reader.ReadRaw(Magic.Length)) != Magic)
            throw new FormatException("It isn't an OpenSSH private key.");
        if (reader.ReadText() != "none" || reader.ReadText() != "none")
            throw new FormatException("It's encrypted with a passphrase, so Meep can't sign with it unattended.");
        reader.ReadString(); // KDF options
        if (reader.ReadUInt32() != 1)
            throw new FormatException("It holds more than one key.");
        reader.ReadString(); // the public key
        var secret = new SshWireReader(reader.ReadString());
        if (secret.ReadUInt32() != secret.ReadUInt32())
            throw new FormatException("Its check numbers differ, so it's damaged or encrypted.");
        if (secret.ReadText() is var type && type != "ssh-ed25519")
            throw new FormatException($"It's an {type} key, and Meep signs only with Ed25519 keys.");
        var publicKey = secret.ReadString();
        var privateKey = secret.ReadString();
        if (publicKey.Length != 32 || privateKey.Length != 64 || !privateKey[32..].SequenceEqual(publicKey))
            throw new FormatException("Its Ed25519 key isn't the size it should be.");
        return new Ed25519PrivateKeyParameters(privateKey[..32].ToArray());
    }

    /// <summary>Signs a message as <c>ssh-keygen -Y sign -n {ns}</c> would, with SHA-512.</summary>
    public SshSignature Sign(byte[] message, string ns)
    {
        var data = SshSignature.SignedData(ns, "sha512", message);
        var writer = new SshWireWriter();
        if (_ed25519 is not null)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, _ed25519);
            signer.BlockUpdate(data, 0, data.Length);
            writer.Text("ssh-ed25519").String(signer.GenerateSignature());
        }
        else
        {
            var fixedSize = _ecdsa!.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var numbers = new SshWireWriter().Mpint(fixedSize[..32]).Mpint(fixedSize[32..]).ToArray();
            writer.Text("ecdsa-sha2-nistp256").String(numbers);
        }
        return new SshSignature(Public, ns, "sha512", writer.ToArray());
    }

    public void Dispose() => _ecdsa?.Dispose();
}
