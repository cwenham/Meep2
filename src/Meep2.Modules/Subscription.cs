using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// One Send's use of a shared connection: where it emits what the server sends, and the lease those messages carry,
/// so a Send without an address can reply through it.
/// </summary>
internal sealed class Subscription
{
    private readonly IDisposable _hold;

    public Subscription(Outlet outlet, bool connections)
    {
        Outlet = outlet;
        Connections = connections;
        Lease = new ClientLease(this);
        _hold = Lease.Hold();
    }

    public Outlet Outlet { get; }

    public bool Connections { get; }

    public ClientLease Lease { get; }

    public SharedClient Client { get; set; } = null!;

    public void Deliver(Message message) => Outlet.Emit(message, Lease);

    /// <summary>Lets go of the lease, which closes once messages carrying it have left the pipeline.</summary>
    public void Release() => _hold.Dispose();
}
