using System.Net;

namespace Meep2.Modules;

/// <summary>Reads the addresses Listen takes.</summary>
internal static class Endpoints
{
    /// <summary>The host and port of a tcp:// address.</summary>
    public static bool TryTcp(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "tcp" || uri.Port is < 1 or > 65535)
            return false;
        host = uri.Host.Trim('[', ']');
        port = uri.Port;
        return true;
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (host is "0.0.0.0" or "*")
            return IPAddress.Any;
        if (host == "::")
            return IPAddress.IPv6Any;
        if (IPAddress.TryParse(host, out var address))
            return address;
        if (host == "localhost")
            return IPAddress.Loopback;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0 ? addresses[0] : throw new InvalidOperationException($"Couldn't find an address for {host}.");
    }
}
