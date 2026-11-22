namespace Meep2.Modules;

/// <summary>Tells web addresses and file paths apart, for Fetch's inflections.</summary>
internal static class Addresses
{
    /// <summary>Whether an address is an http or https address.</summary>
    public static bool IsWeb(string address, out Uri? uri) =>
        Uri.TryCreate(address, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The file path an address names: a file:// address, or a path such as <c>~/data.json</c>, <c>/var/data.json</c>
    /// or <c>data.json</c>. Null for an address with another scheme, such as https.
    /// </summary>
    public static string? FilePath(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : null;
        return address.Length > 0 ? address : null;
    }
}
