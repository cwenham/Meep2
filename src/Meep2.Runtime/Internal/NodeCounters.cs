using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// One module's counts: messages received, handled, emitted and so on, and bytes read from the network and written to
/// it. Each is counted with one atomic add, so modules on many threads can count at once.
/// </summary>
internal sealed class NodeCounters
{
    private long _received;
    private long _handled;
    private long _emitted;
    private long _passed;
    private long _dropped;
    private long _filtered;
    private long _failed;
    private long _restarts;
    private long _expired;
    private long _refused;
    private long _denied;
    private long _bytesReceived;
    private long _bytesSent;

    /// <summary>Bytes the module has read from the network and written to it (<see cref="ModuleContext.CountBytes"/>).</summary>
    public ModuleTraffic Traffic => new(Interlocked.Read(ref _bytesReceived), Interlocked.Read(ref _bytesSent));

    /// <summary>The counts now, with how many messages are <paramref name="queued"/> for the module.</summary>
    public ModuleStats Read(long queued) => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _handled),
        Interlocked.Read(ref _emitted),
        Interlocked.Read(ref _passed),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _filtered),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _restarts),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _refused),
        queued,
        Interlocked.Read(ref _denied));

    public void CountBytes(long received, long sent)
    {
        if (received > 0)
            Interlocked.Add(ref _bytesReceived, received);
        if (sent > 0)
            Interlocked.Add(ref _bytesSent, sent);
    }

    public void Received() => Interlocked.Increment(ref _received);

    public void Handled() => Interlocked.Increment(ref _handled);

    public void Emitted() => Interlocked.Increment(ref _emitted);

    public void Passed() => Interlocked.Increment(ref _passed);

    public void Dropped() => Interlocked.Increment(ref _dropped);

    public void Filtered() => Interlocked.Increment(ref _filtered);

    public void Failed() => Interlocked.Increment(ref _failed);

    public void Restarted() => Interlocked.Increment(ref _restarts);

    public void Expired() => Interlocked.Increment(ref _expired);

    public void Refused() => Interlocked.Increment(ref _refused);

    public void Denied() => Interlocked.Increment(ref _denied);
}
