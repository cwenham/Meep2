using System.Security.Cryptography.X509Certificates;

namespace Meep2.Modules;

/// <summary>Loads certificates for https:// addresses.</summary>
internal static class Certificates
{
    public static X509Certificate2 Load(string certificate, string? key)
    {
        var path = System.IO.Path.GetFullPath(certificate);
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".pfx" or ".p12")
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);

        // A key loaded from PEM lives only in memory, which TLS on macOS and Windows can't use, so it goes through
        // PKCS#12 first.
        using var pem = X509Certificate2.CreateFromPemFile(path, key is null ? null : System.IO.Path.GetFullPath(key));
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
