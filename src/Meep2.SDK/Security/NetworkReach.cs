using System.Net;
using System.Net.Sockets;

namespace Meep2.SDK.Security;

/// <summary>
/// Which addresses a connection may reach (Q313): what a <c>meep.network.send:public</c> or <c>:private</c> scope
/// allows, checked against the address actually connected to, not the name, so a name that resolves somewhere
/// unexpected, or changes between the check and the connection, can't steer a pipeline into the home network.
/// </summary>
[Flags]
public enum NetworkReach
{
    /// <summary>
    /// No address: multicast, broadcast and reserved addresses, and every address at once (<c>0.0.0.0</c>, <c>::</c>),
    /// which neither keyword allows.
    /// </summary>
    None = 0,

    /// <summary>Addresses on the internet: any unicast address that isn't private.</summary>
    Public = 1,

    /// <summary>
    /// This machine and private networks: loopback, 10/8, 172.16/12, 192.168/16, link-local, the shared range carriers
    /// use (100.64/10), 0/8, and IPv6's unique-local, link-local and site-local ranges.
    /// </summary>
    Private = 2,

    /// <summary>Anywhere: the permission was held for the host by name, or unscoped.</summary>
    Any = Public | Private,
}

/// <summary>Which <see cref="NetworkReach"/> an address is in, as the <c>public</c> and <c>private</c> scopes read it.</summary>
public static class NetworkAddresses
{
    /// <summary>The keyword scopes, as written after <c>meep.network.send:</c>.</summary>
    public const string PublicScope = "public";

    /// <inheritdoc cref="PublicScope"/>
    public const string PrivateScope = "private";

    /// <summary>
    /// Whether <paramref name="address"/> is public, private, or neither. The shared range carriers use (100.64/10) is
    /// private here, unlike Meep's sense of hosts it may tell about itself, since others' machines there, such as on
    /// Tailscale, aren't the internet either.
    /// </summary>
    public static NetworkReach ReachOf(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return NetworkReach.None; // every address at once, as a Listen binds to: neither one kind nor the other
        if (IPAddress.IsLoopback(address))
            return NetworkReach.Private;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            if (b[0] is 0 or 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64))
                return NetworkReach.Private;
            if (b[0] >= 224)
                return NetworkReach.None; // multicast, reserved and broadcast
            return NetworkReach.Public;
        }
        if (address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return NetworkReach.Private;
        if (address.IsIPv6Multicast)
            return NetworkReach.None;
        return NetworkReach.Public;
    }

    /// <summary>Whether <paramref name="reach"/> allows connecting to <paramref name="address"/>.</summary>
    public static bool Allows(this NetworkReach reach, IPAddress address) =>
        reach == NetworkReach.Any || (ReachOf(address) is var of && of != NetworkReach.None && (reach & of) == of);

    /// <summary>The reach in words: "public", "private" or "any".</summary>
    public static string Describe(this NetworkReach reach) => reach switch
    {
        NetworkReach.Public => "public",
        NetworkReach.Private => "private",
        NetworkReach.Any => "any",
        _ => "no",
    };
}
