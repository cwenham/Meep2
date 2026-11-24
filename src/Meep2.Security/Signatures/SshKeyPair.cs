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
