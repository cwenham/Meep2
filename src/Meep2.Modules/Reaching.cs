using System.Net;
using System.Net.Sockets;
using Meep2.SDK.Security;

namespace Meep2.Modules;

/// <summary>What the Listens share about <c>meep.network.listen:public</c> and <c>:private</c> (Q313).</summary>
internal static class Listens
{
    /// <summary>
    /// <paramref name="address"/>, the address a Listen will bind to, if <paramref name="reach"/> allows it. Every network
    /// (<c>0.0.0.0</c>, <c>[::]</c>) is in neither reach, so only a permission held by name or unscoped allows it.
    /// </summary>
    /// <exception cref="PermissionDeniedException">The address isn't one the reach allows.</exception>
    public static IPAddress Within(IPAddress address, string host, NetworkReach reach)
    {
        if (reach == NetworkReach.Any || (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && reach.Allows(address)))
            return address;
        throw new PermissionDeniedException(new PermissionName(MeepPermissions.NetworkListen),
            $"{host} is {address}, and the pipeline may listen only on {reach.Describe()} addresses (meep.network.listen:{reach.Describe()}).");
    }
}

/// <summary>
/// Connects only to addresses a <see cref="NetworkReach"/> allows (Q313): for a message that may send to
/// <c>meep.network.send:public</c> or <c>:private</c> addresses but not to the host by name. The name is resolved
/// when connecting, and only addresses of the right kind are tried, so what the check saw is what's connected to.
/// </summary>
internal static class Reaching
{
    /// <summary>A socket connected to <paramref name="host"/> at an address <paramref name="reach"/> allows.</summary>
    /// <exception cref="PermissionDeniedException">None of the host's addresses is one it allows.</exception>
    public static async ValueTask<Socket> ConnectAsync(string host, int port, NetworkReach reach, CancellationToken cancellationToken)
    {
        var name = host.Trim('[', ']');
        IPAddress[] addresses = IPAddress.TryParse(name, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
        var allowed = addresses.Where(address => reach.Allows(address)).ToArray();
        if (allowed.Length == 0)
        {
            throw new PermissionDeniedException(new PermissionName(MeepPermissions.NetworkSend),
                $"{name} is at {string.Join(", ", addresses.Select(a => a.ToString()))}, and the message may send only to {reach.Describe()} addresses there (meep.network.send:{reach.Describe()}).");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, port, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An HTTP handler for connections limited to <paramref name="reach"/>, or one with no limit for
    /// <see cref="NetworkReach.Any"/>. Each reach has a pool of its own, so a connection made without a limit is never
    /// reused for a request that has one.
    /// </summary>
    public static SocketsHttpHandler Handler(NetworkReach reach)
    {
        var handler = new SocketsHttpHandler();
        if (reach != NetworkReach.Any)
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
                new NetworkStream(await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, reach, cancellationToken).ConfigureAwait(false), ownsSocket: true);
        }
        return handler;
    }

    // Invokers for WebSockets limited to one reach, kept for the process, as a WebSocket may use its invoker for as long
    // as it's open.
    private static readonly HttpMessageInvoker s_public = new(Handler(NetworkReach.Public));
    private static readonly HttpMessageInvoker s_private = new(Handler(NetworkReach.Private));

    /// <summary>The invoker for a WebSocket's handshake limited to <paramref name="reach"/>, which mustn't be <see cref="NetworkReach.Any"/>.</summary>
    public static HttpMessageInvoker Invoker(NetworkReach reach) => reach == NetworkReach.Public ? s_public : s_private;

    /// <summary>The refusal inside <paramref name="exception"/>, as HTTP and WebSocket clients wrap what a connection callback throws, or null.</summary>
    public static PermissionDeniedException? Refusal(Exception exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is PermissionDeniedException denied)
                return denied;
        }
        return null;
    }
}
