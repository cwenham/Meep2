using System.Net.Sockets;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Modules;

/// <summary>
/// Listen's TCP inflection: a source that accepts TCP connections and emits each message that arrives on one as a
/// <see cref="TcpMessage"/> carrying a lease on its connection. In Meeplang: <c>Listen "tcp://0.0.0.0:7000"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each line is a message, unless <c>end</c> gives the text that ends one, such as <c>end="\r\n.\r\n"</c>, or
/// <c>length</c> gives the size of every message, for a protocol whose messages are all the same length (Q97). Either
/// way, the connection stays open for more.
/// </para>
/// <para>
/// With <c>connections=true</c>, Listen also emits <see cref="TcpConnected"/> when a client connects and
/// <see cref="TcpDisconnected"/> when the connection ends.
/// </para>
/// <para>
/// A connection stays open while the client keeps it open, and after that until every message from it has left the
/// pipeline. <c>Send final=true</c> closes it sooner. A message longer than <c>max-size</c> closes the connection.
/// </para>
/// </remarks>
[Verb("Listen", Inflection = "TCP")]
[ChecksPermission(MeepPermissions.NetworkListen, "its host and port")]
[Role(VerbRole.Inlet)]
[Emits(typeof(TcpMessage))]
[Emits(typeof(TcpConnected), When = nameof(TcpListenParameters.Connections))]
[Emits(typeof(TcpDisconnected), When = nameof(TcpListenParameters.Connections))]
public sealed class TcpListen(TcpListenParameters parameters) : ASource, IChosenByParameters<TcpListenParameters>, IReconfigurable<TcpListenParameters>
{
    private volatile TcpListenParameters _parameters = parameters;

    /// <summary>
    /// Takes new parameters while it listens, for the connections that come after (Q121). A new address needs a Listen
    /// made afresh.
    /// </summary>
    public bool ParametersChanged(TcpListenParameters old, TcpListenParameters next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Url != old.Url)
            return false;
        _parameters = next;
        return true;
    }

    /// <summary>Takes tcp:// addresses with a port, and one way of telling where a message ends.</summary>
    public static string? WhyNot(TcpListenParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Endpoints.TryTcp(parameters.Url, out _, out _))
            return $"needs a tcp:// address with a port, not {parameters.Url}";
        if (parameters.End is not null && parameters.Length is not null)
            return "takes end or length to tell where a message ends, not both";
        return null;
    }

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var start = _parameters;
        if (!Endpoints.TryTcp(start.Url, out var host, out var port))
            throw new InvalidOperationException($"Listen.TCP needs a tcp:// address with a port, not {start.Url}");
        var reach = context.DemandEndpoint(MeepPermissions.NetworkListen, null, host, port);
        var listener = new TcpListener(Listens.Within(await Endpoints.ResolveAsync(host, cancellationToken).ConfigureAwait(false), host, reach), port);
        listener.Start();
        context.Listening($"tcp://{listener.LocalEndpoint}");
        var bound = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        MeepHosts.Bound(bound, context.HostId);
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                connections.RemoveAll(task => task.IsCompleted);
                var current = _parameters;
                connections.Add(Task.Run(() => ServeAsync(client, current, Framing.For(current), context, cancellationToken), CancellationToken.None));
            }
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
        finally
        {
            MeepHosts.Unbound(bound, context.HostId);
            listener.Stop();
        }
    }

    // Emits each message from one connection, then waits until the connection's lease has closed.
    private static async Task ServeAsync(TcpClient client, TcpListenParameters parameters, Framing framing, ModuleContext context, CancellationToken cancellationToken)
    {
        var lease = new TcpConnectionLease(client, framing.Ending, context);
        var emitted = false;
        var reason = "The client closed the connection.";
        using (lease.Hold())
        {
            if (parameters.Connections)
            {
                context.Emit(new TcpConnected { RemoteAddress = lease.Remote, Connection = lease.Number }, lease);
                emitted = true;
            }
            try
            {
                await foreach (var body in framing.ReadAsync(lease.Stream, parameters.MaxSize, cancellationToken).ConfigureAwait(false))
                {
                    lease.From.Count(received: 1);
                    context.Emit(new TcpMessage(Encoding.UTF8.GetString(body)) { Body = body, RemoteAddress = lease.Remote, Connection = lease.Number }, lease);
                    emitted = true;
                }
            }
            catch (FramingException exception)
            {
                lease.From.Count(rejected: 1); // a message too long
                reason = $"{lease.Remote} {exception.Message}.";
                context.EmitError(reason);
                await lease.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                reason = lease.IsClosed ? "The connection was closed from this end."
                    : cancellationToken.IsCancellationRequested ? "The pipeline stopped."
                    : $"The connection failed: {exception.Message}";
            }

            if (parameters.Connections)
                context.Emit(new TcpDisconnected { RemoteAddress = lease.Remote, Connection = lease.Number, Reason = reason }, lease);
        }
        if (!emitted)
            await lease.CloseAsync().ConfigureAwait(false);
        await lease.Closed.ConfigureAwait(false);
    }
}

/// <summary>Parameters for <see cref="TcpListen"/>.</summary>
public sealed record TcpListenParameters : ConnectionParameters
{
    /// <summary>The address to listen at, such as <c>tcp://0.0.0.0:7000</c>. Host 0.0.0.0 listens on every network.</summary>
    [Positional]
    public required string Url { get; init; }

    /// <summary>
    /// The text that ends each message, such as <c>"\r\n.\r\n"</c>. It isn't part of the message, and the connection
    /// stays open for more. Without it, or <see cref="Length"/>, each line is a message.
    /// </summary>
    public string? End
    {
        get;
        init => field = value is null || value.Length > 0 ? value : throw new ArgumentException("The end of a message needs at least one character.", nameof(End));
    }

    /// <summary>The size of every message, in bytes, for a protocol whose messages are all the same length.</summary>
    public int? Length
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Length), "A message's length must be at least 1 byte.");
    }
}
