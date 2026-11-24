using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// An X.509 certificate, as DER, with the intermediate certificates sent with it, for <c>Verify.Certificate</c> to
/// build a chain to a trusted root.
/// </summary>
/// <remarks>
/// <para>
/// Anyone can make one from a certificate's bytes; it means nothing until it's verified. What it says about itself
/// (subject, issuer, dates) is read from <see cref="Der"/> when it's made, for showing and for conditions, not for
/// deciding trust.
/// </para>
/// <para>
/// A Listen that requires client certificates attaches the peer's as one marked <see cref="ProvenByTls"/>: the TLS
/// handshake proved the peer holds its key (Q253). Only the runtime can set that mark, and a copy made with
/// <c>with</c> loses it, so it can't be moved to another message.
/// </para>
/// </remarks>
public sealed record Certificate : Message
{
    private Certificate(byte[] der, IReadOnlyList<ReadOnlyMemory<byte>> intermediates)
    {
        Der = der;
        Intermediates = intermediates;
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        Subject = certificate.Subject;
        Issuer = certificate.Issuer;
        NotBefore = certificate.NotBefore.ToUniversalTime();
        NotAfter = certificate.NotAfter.ToUniversalTime();
        SerialNumber = certificate.SerialNumber;
        Thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    // A copy is a new message that hasn't come from a TLS handshake, so it loses the mark.
    private Certificate(Certificate original)
        : base(original)
    {
        Der = original.Der;
        Intermediates = original.Intermediates;
        Subject = original.Subject;
        Issuer = original.Issuer;
        NotBefore = original.NotBefore;
        NotAfter = original.NotAfter;
        SerialNumber = original.SerialNumber;
        Thumbprint = original.Thumbprint;
    }

    /// <summary>The certificate itself, as DER.</summary>
    public ReadOnlyMemory<byte> Der { get; }

    /// <summary>The intermediate certificates sent with it, as DER, nearest first.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Intermediates { get; }

    /// <summary>Its subject's distinguished name, such as <c>CN=alice</c>.</summary>
    public string Subject { get; }

    /// <summary>Its issuer's distinguished name.</summary>
    public string Issuer { get; }

    /// <summary>When it starts to be valid, in UTC.</summary>
    public DateTime NotBefore { get; }

    /// <summary>When it stops being valid, in UTC.</summary>
    public DateTime NotAfter { get; }

    /// <summary>Its serial number, in hexadecimal.</summary>
    public string SerialNumber { get; }

    /// <summary>Its SHA-256 hash, in hexadecimal.</summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Whether a TLS handshake proved that whoever sent it holds its key: set only by the runtime, for a client
    /// certificate a Listen required.
    /// </summary>
    public bool ProvenByTls { get; private set; }

    /// <summary>
    /// Makes one from a certificate's DER and the intermediates sent with it. Throws <see cref="FormatException"/> if
    /// the certificate can't be read.
    /// </summary>
    public static Certificate FromDer(ReadOnlySpan<byte> der, IEnumerable<ReadOnlyMemory<byte>>? intermediates = null)
    {
        try
        {
            return new Certificate(der.ToArray(), [.. (intermediates ?? []).Select(i => (ReadOnlyMemory<byte>)i.ToArray())]);
        }
        catch (CryptographicException exception)
        {
            throw new FormatException($"This certificate can't be read: {exception.Message.TrimEnd('.')}.", exception);
        }
    }

    /// <summary>
    /// Makes one from PEM text: the first certificate in it, with any after it as intermediates. Throws
    /// <see cref="FormatException"/> if there's none or one can't be read.
    /// </summary>
    public static Certificate FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var found = new List<byte[]>();
        var rest = pem.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            if (rest[fields.Label].SequenceEqual("CERTIFICATE"))
                found.Add(Convert.FromBase64String(rest[fields.Base64Data].ToString()));
            rest = rest[fields.Location.End..];
        }
        if (found.Count == 0)
            throw new FormatException("This has no PEM certificate (-----BEGIN CERTIFICATE-----) in it.");
        return FromDer(found[0], found.Skip(1).Select(der => (ReadOnlyMemory<byte>)der));
    }

    /// <summary>Loads the certificate for .NET's X.509 classes. The caller disposes of it.</summary>
    public X509Certificate2 ToX509() => X509CertificateLoader.LoadCertificate(Der.Span);

    /// <summary>Marks a new certificate as proven by a TLS handshake. Only the runtime calls this, before emitting it.</summary>
    internal Certificate MarkProvenByTls()
    {
        if (IsStamped)
            throw new InvalidOperationException("Only a certificate that hasn't been emitted can be marked as proven by TLS.");
        ProvenByTls = true;
        return this;
    }

    /// <inheritdoc/>
    public override string ToText() => Subject;
}
