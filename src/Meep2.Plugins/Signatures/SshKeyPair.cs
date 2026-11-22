using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Meep2.Plugins;

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
