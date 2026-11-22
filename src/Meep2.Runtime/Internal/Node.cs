using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Shared;

namespace Meep2.Runtime.Internal;

/// <summary>One module's place in a running pipeline: its instances, its downstream modules and its counts.</summary>
internal abstract class Node(Pipeline pipeline, NodeSpec spec)
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
    private long _bytesReceived;
    private long _bytesSent;
    private volatile string? _listening;
    private readonly Lock _tapping = new();
    private volatile DebugTap[]? _taps;

    private NodeSpec _spec = spec;

    public Pipeline Pipeline { get; } = pipeline;

    /// <summary>The message the module is handling now and the span tracing it, if tracing is on (OpenTelemetry).</summary>
    public (Message Message, System.Diagnostics.Activity Span)? Handling { get; set; }

    /// <summary>How the module was added. A change to the running pipeline can replace it (goal 4).</summary>
    public NodeSpec Spec
    {
        get => _spec;
        protected set
        {
            _spec = value;
            Label = value.Info.ToString();
        }
    }

    public ModuleInfo Info => Spec.Info;

    /// <summary>
    /// The key tools know the module by, such as <c>m3</c>, which lasts while its number changes, or null if nothing has
    /// given it one (<see cref="Pipeline.NameModules"/>). Put on its telemetry as <c>meep.module.key</c>.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>How long the module takes to handle each message (<see cref="HandlingTimes"/>).</summary>
    public TimingRecorder Timing { get; } = new();

    /// <summary>Bytes the module has read from the network and written to it (<see cref="ModuleContext.CountBytes"/>).</summary>
    public ModuleTraffic Traffic => new(Interlocked.Read(ref _bytesReceived), Interlocked.Read(ref _bytesSent));

    /// <summary>Where the module says it's listening, or null (<see cref="ModuleContext.Listening"/>).</summary>
    public string? ListeningAt
    {
        get => _listening;
        set => _listening = value;
    }

    public void CountBytes(long received, long sent)
    {
        if (received > 0)
            Interlocked.Add(ref _bytesReceived, received);
        if (sent > 0)
            Interlocked.Add(ref _bytesSent, sent);
    }

    private PeerTable? _arrivals;
    private PeerTable? _destinations;
    private volatile OriginMade? _origin;

    /// <summary>Counts by where messages arrive from, once the module has counted any (<see cref="ModuleContext.Arrival"/>).</summary>
    public PeerTable? Arrivals => Volatile.Read(ref _arrivals);

    /// <summary>Counts by where messages go, once the module has counted any (<see cref="ModuleContext.Destination"/>).</summary>
    public PeerTable? Destinations => Volatile.Read(ref _destinations);

    public Tally ArrivalFrom(string from) =>
        (Volatile.Read(ref _arrivals) ?? Made(ref _arrivals, PeerTable.ForArrivals())).For(from);

    public Tally DestinationAt(string endpoint) =>
        (Volatile.Read(ref _destinations) ?? Made(ref _destinations, PeerTable.ForDestinations())).For(endpoint);

    private static PeerTable Made(ref PeerTable? field, PeerTable made) =>
        Interlocked.CompareExchange(ref field, made, null) ?? made;

    /// <summary>
    /// Where messages this module sends come from, for another Meep host (<see cref="ModuleContext.Origin"/>), made
    /// once for each host ID and key it has.
    /// </summary>
    public string? Origin
    {
        get
        {
            var hostId = Pipeline.Options.HostId;
            var key = Key;
            if (hostId is null || key is null)
                return null;
            if (_origin is { } made && ReferenceEquals(made.HostId, hostId) && ReferenceEquals(made.Key, key))
                return made.Origin;
            var origin = Peers.Origin(hostId, Pipeline.Name, key);
            _origin = new OriginMade(hostId, key, origin);
            return origin;
        }
    }

    private sealed record OriginMade(string HostId, string Key, string Origin);

    /// <summary>The module's instances now: one for a source, one for each inflection otherwise.</summary>
    public abstract IEnumerable<AModule> Instances { get; }

    /// <summary>The module as people see it, as in "Fetch #2", kept so metrics don't make it again for each message.</summary>
    public string Label { get; private set; } = spec.Info.ToString();

    /// <summary>Whether a change to the running pipeline has taken the module out.</summary>
    public bool IsRemoved { get; protected set; }

    public NodeOptions Options => Spec.Options;

    /// <summary>The error policy for this module: its own, or the pipeline's.</summary>
    public ErrorPolicy Policy => Options.ErrorPolicy ?? Pipeline.Options.ErrorPolicy;

    private volatile Fanout _links = Fanout.None;

    /// <summary>The modules this one feeds.</summary>
    public InflectionNode[] Downstream => _links.Targets;

    /// <summary>The modules this one feeds, with which of each one's inputs it is. Replaced whole, so a reader sees one or the other.</summary>
    public Fanout Links
    {
        get => _links;
        set => _links = value;
    }

    /// <summary>Makes the module's instances. Called when the pipeline starts.</summary>
    public abstract void CreateModule();

    /// <summary>Disposes the module's instances when the pipeline finishes.</summary>
    public abstract ValueTask DisposeModuleAsync();

    /// <summary>
    /// Takes a new spec for the same module, as when its line was edited: new options, identity and factories, and if
    /// <paramref name="reconfigure"/> is set, new parameters for its running instances, which keep their state if they
    /// take them (<see cref="IReconfigurable{TParameters}"/>) and are replaced otherwise.
    /// </summary>
    /// <returns>Whether every instance kept its state.</returns>
    public abstract Task<bool> UpdateAsync(NodeSpec next, bool reconfigure);

    /// <summary>Offers an instance new parameters, returning whether it took them. A hook that throws counts as no.</summary>
    protected bool TryReconfigure(AModule module, InflectionSpec old, InflectionSpec next)
    {
        if (next.Reconfigure is not { } reconfigure)
            return false;
        try
        {
            return reconfigure(module, old.Parameters, next.Parameters);
        }
        catch (Exception exception)
        {
            Pipeline.ReportFault(next.Info, $"{Describe(next.Info)} couldn't take its new parameters, so it's replaced", exception);
            return false;
        }
    }

    public ModuleStats GetStats() => new(
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
        Queued);

    /// <summary>How many messages are waiting for the module now.</summary>
    public virtual long Queued => 0;

    /// <summary>
    /// Shows a message the module emitted or passed on to any taps on it. With none, this is one check of a field,
    /// so a pipeline nobody is watching doesn't slow down.
    /// </summary>
    public void Tapped(Message message)
    {
        if (_taps is { } taps)
        {
            foreach (var tap in taps)
                tap.Offer(message);
        }
    }

    public void AddTap(DebugTap tap)
    {
        lock (_tapping)
            _taps = [.. _taps ?? [], tap];
    }

    public void RemoveTap(DebugTap tap)
    {
        lock (_tapping)
        {
            var rest = (_taps ?? []).Where(t => t != tap).ToArray();
            _taps = rest.Length == 0 ? null : rest;
        }
    }

    /// <summary>Ends every tap on the module, as when the pipeline finishes.</summary>
    public void CloseTaps()
    {
        DebugTap[]? taps;
        lock (_tapping)
        {
            taps = _taps;
            _taps = null;
        }
        foreach (var tap in taps ?? [])
            tap.Complete();
    }

    public void CountReceived() => Interlocked.Increment(ref _received);

    public void CountHandled() => Interlocked.Increment(ref _handled);

    public void CountEmitted() => Interlocked.Increment(ref _emitted);

    public void CountPassed() => Interlocked.Increment(ref _passed);

    public void CountDropped() => Interlocked.Increment(ref _dropped);

    public void CountFiltered() => Interlocked.Increment(ref _filtered);

    public void CountFailed() => Interlocked.Increment(ref _failed);

    public void CountRestart() => Interlocked.Increment(ref _restarts);

    public void CountExpired() => Interlocked.Increment(ref _expired);

    public void CountRefused() => Interlocked.Increment(ref _refused);

    protected static AModule CreateInstance(InflectionSpec inflection)
    {
        AModule? module;
        try
        {
            module = inflection.Create();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Couldn't create {Describe(inflection.Info)}: {exception.Message}", exception);
        }
        return module ?? throw new InvalidOperationException($"Couldn't create {Describe(inflection.Info)}: its factory returned null.");
    }

    protected async ValueTask DisposeInstanceAsync(AModule? module, string problem)
    {
        try
        {
            switch (module)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            Pipeline.ReportFault(Info, problem, exception);
        }
    }

    /// <summary>A module, with its inflection if it's one of several: "Fetch #2 (HTTP)".</summary>
    protected static string Describe(ModuleInfo info) =>
        info.Inflection is { } inflection && inflection != info.Verb ? $"{info} ({inflection})" : info.ToString();

    /// <summary>A duration as Meeplang writes it: 250ms, 5s, 2m.</summary>
    internal static string Describe(TimeSpan duration) => DurationText.Write(duration);
}
