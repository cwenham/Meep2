using System.Security.Cryptography.X509Certificates;

namespace Meep2.Modules;

/// <summary>Loads certificates for https:// addresses.</summary>
internal static class Certificates
{
    /// <summary>
    /// Why a Listen's certificate and key don't suit its address, or null if they do: a secure address needs a
    /// certificate, any other takes none, and the files have to exist.
    /// </summary>
    /// <param name="secureAddress">The kind of secure address, with its article, such as <c>an https://</c>, for the reason given.</param>
    /// <param name="secure">Whether the address is secure.</param>
    /// <param name="certificate">The certificate file given, if any.</param>
    /// <param name="key">The key file given, if any.</param>
    public static string? WhyNot(string secureAddress, bool secure, string? certificate, string? key)
    {
        if (secure && certificate is null)
            return $"needs a certificate for {secureAddress} address, as in certificate=\"server.pem\"";
        if (!secure && (certificate ?? key) is not null)
            return $"only uses a certificate with {secureAddress} address";
        if (certificate is not null && !File.Exists(certificate))
            return $"can't find the certificate {certificate}";
        if (key is not null && !File.Exists(key))
            return $"can't find the key {key}";
        return null;
    }

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
