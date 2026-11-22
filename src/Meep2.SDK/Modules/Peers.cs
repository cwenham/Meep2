using System.Net;

namespace Meep2.SDK.Modules;

/// <summary>
/// How Meep hosts tell each other where a message comes from (prompt 7): W3C <c>traceparent</c> and <c>baggage</c> on
/// HTTP requests and WebSocket handshakes, and the <c>Meep-Host</c> header by which each end learns the other is Meep.
/// </summary>
/// <remarks>
/// <para>
/// A request to an address not yet known to be Meep asks, with <c>Meep-Host: ?</c>. A Listen that's asked, or that
/// receives an origin, answers with <c>Meep-Host: &lt;its host's ID&gt;</c>, and the asking end remembers the answer
/// for that host and port. Requests to a Meep host then carry <c>baggage: meep.origin=&lt;host ID&gt;/&lt;pipeline&gt;/&lt;module key&gt;</c>,
/// and nothing else learns the origin.
/// </para>
/// <para>
/// An origin's parts are each escaped as in a URL, so they never hold <c>/</c>, <c>,</c>, <c>;</c> or spaces.
/// A Listen counts what arrives from one as <c>meep://&lt;origin&gt;</c>, and what arrives from anywhere else by the
/// client's address.
/// </para>
/// </remarks>
public static class Peers
{
    /// <summary>The header by which a Meep end asks whether the other is Meep (<c>?</c>) and answers with its host's ID.</summary>
    public const string HostHeader = "Meep-Host";

    /// <summary>What a request carries in <see cref="HostHeader"/> to ask.</summary>
    public const string Ask = "?";

    /// <summary>The W3C baggage member naming where a request comes from.</summary>
    public const string OriginMember = "meep.origin";

    /// <summary>The prefix of an arrival's key for an origin in Meep, as in <c>meep://&lt;host ID&gt;/site/m3</c>.</summary>
    public const string MeepScheme = "meep://";

    /// <summary>The longest origin taken from a request, so a client can't fill a Listen's counts with junk.</summary>
    public const int LongestOrigin = 256;

    /// <summary>An origin: a host ID, a pipeline and a module key, each escaped as in a URL.</summary>
    public static string Origin(string hostId, string pipeline, string key) =>
        $"{Uri.EscapeDataString(hostId)}/{Uri.EscapeDataString(pipeline)}/{Uri.EscapeDataString(key)}";

    /// <summary>The W3C baggage that carries an origin.</summary>
    public static string Baggage(string origin) => $"{OriginMember}={origin}";

    /// <summary>
    /// The origin a W3C baggage header carries, or null if it has none, or one that isn't three parts, each
    /// non-empty, escaped as in a URL, and together no longer than <see cref="LongestOrigin"/>.
    /// </summary>
    public static string? OriginIn(string? baggage)
    {
        if (string.IsNullOrEmpty(baggage))
            return null;
        foreach (var member in baggage.AsSpan().Split(','))
        {
            var text = baggage.AsSpan()[member].Trim();
            var semicolon = text.IndexOf(';');
            if (semicolon >= 0)
                text = text[..semicolon]; // properties, which origins don't use
            var equals = text.IndexOf('=');
            if (equals < 0 || !text[..equals].Trim().SequenceEqual(OriginMember))
                continue;
            var value = text[(equals + 1)..].Trim();
            return IsOrigin(value) ? value.ToString() : null;
        }
        return null;
    }

    private static bool IsOrigin(ReadOnlySpan<char> value)
    {
        if (value.Length is 0 or > LongestOrigin)
            return false;
        var parts = 0;
        var length = 0;
        foreach (var c in value)
        {
            if (c == '/')
            {
                if (length == 0)
                    return false;
                parts++;
                length = 0;
                continue;
            }
            // Unreserved characters and percent escapes, as Uri.EscapeDataString leaves them.
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '%'))
                return false;
            length++;
        }
        return parts == 2 && length > 0;
    }

    /// <summary>
    /// The host and port an address reaches, as destinations are counted and Meep hosts remembered: the host in lower
    /// case (an IPv6 address in brackets), then the port, with 80 or 443 for web addresses that don't give one, as in
    /// <c>api.example.com:443</c>. Null for an address with no host, or that isn't http, https, ws, wss or tcp.
    /// </summary>
    public static string? EndpointOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? EndpointOf(uri) : null;

    /// <inheritdoc cref="EndpointOf(string?)"/>
    public static string? EndpointOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Host.Length == 0)
            return null;
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss" or "tcp") || uri.Port < 0)
            return null;
        return Endpoint(uri.Host, uri.Port);
    }

    /// <summary>A host and port as <see cref="EndpointOf(Uri)"/> writes them.</summary>
    public static string Endpoint(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').ToLowerInvariant();
        return name.Contains(':', StringComparison.Ordinal) ? $"[{name}]:{port}" : $"{name}:{port}";
    }

    /// <summary>
    /// How an arrival from outside Meep is counted: the client's address, without its port, an IPv4 address mapped
    /// into IPv6 written as IPv4. Tools may show a name for it.
    /// </summary>
    public static string ClientOf(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>
    /// The host and port a template of an address fixes, as <see cref="EndpointOf(Uri)"/> writes them, when the text
    /// before its first <c>{</c> already holds the scheme, host and port, as in
    /// <c>https://api.example.com/stops/{msg.StopId}</c>; null when part of them is worked out for each message.
    /// </summary>
    public static string? EndpointOfTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var text = template.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        var brace = text.IndexOf('{', StringComparison.Ordinal);
        if (brace < 0)
            return EndpointOf(text);
        var prefix = text[..brace];
        var scheme = prefix.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return null;
        var slash = prefix.IndexOf('/', scheme + 3);
        return slash < 0 ? null : EndpointOf(prefix[..slash]);
    }
}
