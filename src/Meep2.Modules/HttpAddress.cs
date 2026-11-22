namespace Meep2.Modules;

/// <summary>An http:// or https:// address Listen takes: where to listen, and the path whose requests are its own.</summary>
internal readonly record struct HttpAddress(bool Secure, string Host, int Port, string Path)
{
    public static bool TryParse(string address, out HttpAddress parsed, bool webSocket = false)
    {
        parsed = default;
        var (plain, secure) = webSocket ? (Uri.UriSchemeWs, Uri.UriSchemeWss) : (Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != plain && uri.Scheme != secure))
            return false;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        parsed = new HttpAddress(uri.Scheme == secure, uri.Host.Trim('[', ']'), uri.Port, Uri.UnescapeDataString(path));
        return true;
    }
}
