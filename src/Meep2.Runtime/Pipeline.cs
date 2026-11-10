using System.Collections.Concurrent;
using Meep2.Runtime.Internal;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>
/// A graph of modules that messages flow through. Make one with <see cref="PipelineBuilder"/>, then call
/// <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each module runs on its own task and handles one message at a time, in order, so branches run in parallel.
/// Emitting never waits: when a module's buffer is full, its oldest message is dropped.
/// </para>
/// <para>The pipeline halts once every source has finished and every message has left it.</para>
/// <para>
/// A module's verb can route between several inflections. A message no inflection takes becomes an error (Q54).
/// Expired messages stop at the next module unless it accepts them, and errors follow the
/// <see cref="ErrorPolicy"/>.
/// </para>
/// </remarks>
public sealed class Pipeline : IAsyncDisposable
{
    private const int NotStarted = 0;
    private const int Running = 1;
    private const int Finished = 2;

    // Stops a message going round forever in a loop of modules that all pass it on without taking it in.
    private const int MaxForwardDepth = 256;

    // The pipelines running in this process, for OpenTelemetry's metrics.
    private static readonly ConcurrentDictionary<Pipeline, byte> s_running = new();

    // The modules and how they're connected. A change to the running pipeline replaces these whole (goal 4).
    private volatile Node[] _nodes = [];
    private volatile InflectionNode[] _inflections = [];
    private volatile SourceNode[] _sources = [];
    private volatile Dictionary<ModuleInfo, Node> _nodesByModule = [];
    private volatile HashSet<ModuleInfo> _sourceInfos = [];
    private volatile SourceWindow? _sourceWindow;
    private volatile IReadOnlyList<ModuleInfo> _modules = [];
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly InFlightCounter _inFlight = new();
    private long _kept;
    private int _flushedAtEnd;
    private readonly CancellationTokenSource _stopSources = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource _halted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private int _stopping;
    private volatile bool _aborting;
    private string? _stoppedBecause;

    internal Pipeline(string name, PipelineOptions options, NodeSpec[] specs, (ModuleInfo From, ModuleInfo To)[] connections)
    {
        if (options.DrainTimeout < TimeSpan.Zero && options.DrainTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), options.DrainTimeout, "DrainTimeout can't be negative, except Timeout.InfiniteTimeSpan.");

        Name = name;
        Options = options;
        AbortToken = _abort.Token;
        StoppingToken = _stopSources.Token;

        var nodes = new Node[specs.Length];
        for (var i = 0; i < specs.Length; i++)
            nodes[i] = NewNode(specs[i], options);
        Wire(nodes, specs, connections);
        Install(nodes, specs, options, kept: null);
    }

    private Node NewNode(NodeSpec spec, PipelineOptions options) =>
        spec.IsSource ? new SourceNode(this, spec) : new InflectionNode(this, spec, spec.Options.BufferSize ?? options.BufferSize);

    // Points each module at the ones it feeds. Specs are what the modules are to be: a kept module takes its new
    // spec a little later, between messages.
    private static void Wire(Node[] nodes, NodeSpec[] specs, (ModuleInfo From, ModuleInfo To)[] connections)
    {
        var byInfo = new Dictionary<ModuleInfo, Node>();
        for (var i = 0; i < nodes.Length; i++)
            byInfo[specs[i].Info] = nodes[i];

        // A module's inputs are numbered by the order of the modules feeding it, as the connections list them.
        var inputs = new Dictionary<Node, Dictionary<Node, int>>();
        var downstream = new Dictionary<Node, List<InflectionNode>>();
        foreach (var (from, to) in connections)
        {
            var upstream = byInfo[from];
            var target = byInfo[to];
            if (!downstream.TryGetValue(upstream, out var targets))
                downstream[upstream] = targets = [];
            targets.Add((InflectionNode)target);
            if (!inputs.TryGetValue(target, out var numbers))
                inputs[target] = numbers = [];
            numbers.TryAdd(upstream, numbers.Count + 1);
        }
        foreach (var node in nodes)
        {
            node.Links = downstream.TryGetValue(node, out var targets)
                ? new Fanout([.. targets], [.. targets.Select(target => inputs[target][node])])
                : Fanout.None;
        }
    }

    // Makes these the pipeline's modules. Kept modules can still be found by their old identities, which messages
    // they emitted before the change record.
    private void Install(Node[] nodes, NodeSpec[] specs, PipelineOptions options, IEnumerable<(ModuleInfo Old, Node Node)>? kept)
    {
        var byModule = new Dictionary<ModuleInfo, Node>(nodes.Length);
        foreach (var (old, node) in kept ?? [])
        {
            byModule[old] = node;
            foreach (var inflection in node.Spec.Inflections)
                byModule.TryAdd(inflection.Info, node);
        }
        for (var i = 0; i < nodes.Length; i++)
        {
            byModule[specs[i].Info] = nodes[i];
            foreach (var inflection in specs[i].Inflections)
                byModule[inflection.Info] = nodes[i];
        }
        _nodesByModule = byModule;
        _sourceInfos = [.. specs.Where(spec => spec.IsSource).Select(spec => spec.Info)];
        _sourceWindow = options.ErrorPolicy.StopPipelineAtPercent is null ? null : _sourceWindow ?? new SourceWindow(ErrorPolicy.StopPipelineWindow);
        _inflections = [.. nodes.OfType<InflectionNode>()];
        _sources = [.. nodes.OfType<SourceNode>()];
        _modules = [.. specs.Select(spec => spec.Info)];
        _nodes = nodes;
    }

    /// <summary>The pipelines running in this process now.</summary>
    internal static ICollection<Pipeline> AllRunning => s_running.Keys;

    internal IReadOnlyList<Node> Nodes => _nodes;

    /// <summary>The pipeline's modules, in the order they were added, or as the last change to it has them.</summary>
    public IReadOnlyList<ModuleInfo> Modules => _modules;

    /// <summary>Whether the pipeline has started and hasn't finished.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == Running;

    /// <summary>Completes when the pipeline has finished running and its modules are disposed.</summary>
    public Task Completion => _finished.Task;

    /// <summary>The pipeline's name.</summary>
    public string Name { get; }

    /// <summary>Settings for the whole pipeline. A change to the running pipeline can replace them.</summary>
    public PipelineOptions Options { get; private set; }

    /// <summary>Cancelled when the pipeline gives up waiting for messages to finish. Modules receive it.</summary>
    internal CancellationToken AbortToken { get; }

    /// <summary>Cancelled when the pipeline is asked to stop.</summary>
    internal CancellationToken StoppingToken { get; }

    internal bool IsAborting => _aborting;

    /// <summary>
    /// Why the error policy stopped the pipeline, such as "12 of the last 100 messages from its sources ended in
    /// unhandled errors", or null if it didn't.
    /// </summary>
    public string? StoppedBecause => Volatile.Read(ref _stoppedBecause);

    /// <summary>Counts for one module since the pipeline started.</summary>
    public ModuleStats GetStats(ModuleInfo module) => NodeOf(module).GetStats();

    /// <summary>
    /// Opens a debug tap on a module: a copy of each message it emits or passes on, until the tap is disposed or the
    /// pipeline finishes (Q64). Taps never hold the pipeline up: see <see cref="Runtime.DebugTap"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pipeline has finished.</exception>
    public DebugTap Tap(ModuleInfo module, DebugTapOptions? options = null)
    {
        var node = NodeOf(module);
        if (Volatile.Read(ref _state) == Finished)
            throw new InvalidOperationException($"{Name} has finished, so there's nothing to tap.");
        var tap = new DebugTap(node, options ?? new DebugTapOptions());
        node.AddTap(tap);
        if (Volatile.Read(ref _state) == Finished)
            tap.Complete(); // it finished while the tap was being added
        return tap;
    }

    /// <summary>
    /// Sends a new message into a running pipeline, as a test (Q72). It goes to a module as if it came from upstream,
    /// or, for a source, on downstream as if the source had emitted it. It's recorded as emitted by <see cref="Injector"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pipeline isn't running.</exception>
    /// <exception cref="ArgumentException">The message has been emitted before.</exception>
    public void Inject(ModuleInfo module, Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var node = NodeOf(module);
        if (!IsRunning || Volatile.Read(ref _stopping) != 0)
            throw new InvalidOperationException($"{Name} isn't running, so it can't take a message.");
        if (message.IsStamped)
            throw new ArgumentException("Only a new message can be injected, not one that has been emitted before.", nameof(message));

        List<TrackingScope>? created = null;
        Stamper.StampInjected(message, this, ref created);
        if (Telemetry.Tracing)
            Telemetry.Injected(this, message);
        if (node is InflectionNode target)
        {
            DeliverTo(target, message, depth: 0, input: 0);
        }
        else
        {
            node.Tapped(message);
            Deliver(node, message);
        }
        ScopeSet.ReleaseAll(created);
    }

    /// <summary>What injected messages record as emitting them.</summary>
    public static ModuleInfo Injector { get; } = new(0, "Inject", "Inject");

    /// <summary>
    /// Changes the running pipeline into the one <paramref name="change"/> describes, without stopping it (goal 4).
    /// Modules it keeps carry on with their buffers, counts and taps. Those whose parameters changed are offered the
    /// new ones between two messages, and keep their state if they take them
    /// (<see cref="IReconfigurable{TParameters}"/>); otherwise they're replaced by new instances. New modules start,
    /// and modules left out stop taking messages, finish those they have, and are disposed.
    /// </summary>
    /// <remarks>
    /// A kept module is replaced as a whole, with fresh counts, if its inflections or its buffer's size changed. A
    /// message passing between two modules at the moment their connection changes may be abandoned.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The pipeline isn't running, or a new module couldn't be made. Nothing was changed.</exception>
    /// <exception cref="ArgumentException">The change keeps a module as something it can't be, such as a source as a module that handles messages.</exception>
    public async Task<PipelineChangeReport> ApplyAsync(PipelineChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning || Volatile.Read(ref _stopping) != 0)
                throw new InvalidOperationException($"{Name} isn't running, so it can't be changed.");
            _inFlight.Add(); // it can't halt while it changes
            try
            {
                return await ApplyCoreAsync(change).ConfigureAwait(false);
            }
            finally
            {
                ReleaseHold();
            }
        }
        finally
        {
            _changing.Release();
        }
    }

    private async Task<PipelineChangeReport> ApplyCoreAsync(PipelineChange change)
    {
        var (specs, connections, options) = (change.Next.Specs, change.Next.Connections, change.Next.Options);
        var before = _nodesByModule;
        var nodes = new Node[specs.Length];
        var created = new List<Node>();
        var updates = new List<(Node Node, ModuleInfo Old, NodeSpec Spec, bool Reconfigure)>();
        var kept = new HashSet<Node>();
        var added = new List<ModuleInfo>();
        var replaced = new List<ModuleInfo>();
        var replacedOld = new HashSet<Node>();
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            Node? node = null;
            if (change.KeptAs(spec.Info) is var (running, reconfigure) && before.TryGetValue(running, out var old) && _nodes.Contains(old))
            {
                if (old.Spec.IsSource != spec.IsSource)
                    throw new ArgumentException($"{running} can't become {spec.Info}: only one of them is a source.", nameof(change));
                if (!kept.Add(old))
                    throw new ArgumentException($"{running} is kept as more than one module.", nameof(change));
                if (SameShape(old, spec, options))
                {
                    node = old;
                    updates.Add((old, running, spec, reconfigure));
                }
                else
                {
                    kept.Remove(old); // it goes, and a new one takes its place
                    replaced.Add(spec.Info);
                    replacedOld.Add(old);
                }
            }
            if (node is null)
            {
                node = NewNode(spec, options);
                created.Add(node);
                if (!replaced.Contains(spec.Info))
                    added.Add(spec.Info);
            }
            nodes[i] = node;
        }

        // The new modules' instances are made first, so a factory that throws leaves the pipeline as it was.
        try
        {
            foreach (var node in created)
                node.CreateModule();
        }
        catch
        {
            foreach (var node in created)
                await node.DisposeModuleAsync().ConfigureAwait(false);
            throw;
        }
        var removed = _nodes.Where(node => !kept.Contains(node)).ToList();

        // New modules are ready before anything feeds them, then everything is connected as the change says.
        foreach (var node in created.OfType<InflectionNode>())
            _ = node.Start();
        var removedLinks = removed.ToDictionary(node => node, node => node.Links);
        Wire(nodes, specs, connections);
        foreach (var (node, links) in removedLinks)
        {
            // They finish where they were going.
            var still = Enumerable.Range(0, links.Targets.Length).Where(i => !kept.Contains(links.Targets[i]) || nodes.Contains(links.Targets[i])).ToList();
            node.Links = new Fanout([.. still.Select(i => links.Targets[i])], [.. still.Select(i => links.Inputs[i])]);
        }
        Options = options;
        Install(nodes, specs, options, updates.Select(u => (u.Old, u.Node)));

        // Kept modules take their new specs, between messages.
        var keptState = await Task.WhenAll(updates.Select(u => u.Node.UpdateAsync(u.Spec, u.Reconfigure))).ConfigureAwait(false);
        var unchanged = new List<ModuleInfo>();
        var reconfigured = new List<ModuleInfo>();
        for (var i = 0; i < updates.Count; i++)
        {
            var info = updates[i].Spec.Info;
            if (!updates[i].Reconfigure)
                unchanged.Add(info);
            else if (keptState[i])
                reconfigured.Add(info);
            else
                replaced.Add(info);
        }

        foreach (var source in created.OfType<SourceNode>())
        {
            _inFlight.Add(); // held until the source finishes
            source.Start(_stopSources.Token);
        }
        await RemoveAsync(removed).ConfigureAwait(false);
        return new PipelineChangeReport(added, [.. removed.Where(node => !replacedOld.Contains(node)).Select(node => node.Spec.Info)], reconfigured, replaced, unchanged);
    }

    // Whether a kept module can carry on in place: the same inflections, and the same size of buffer.
    private bool SameShape(Node old, NodeSpec spec, PipelineOptions options)
    {
        if (!old.Spec.Inflections.Select(i => i.Info.Inflection).SequenceEqual(spec.Inflections.Select(i => i.Info.Inflection)))
            return false;
        return old is not InflectionNode inflection || inflection.Capacity == (spec.Options.BufferSize ?? options.BufferSize);
    }

    // Takes modules out: sources first, then the rest, each after the removed modules that feed it, so what's in
    // their buffers can finish on its way.
    private static async Task RemoveAsync(List<Node> removed)
    {
        await Task.WhenAll(removed.OfType<SourceNode>().Select(source => source.RemoveAsync())).ConfigureAwait(false);
        var waiting = removed.OfType<InflectionNode>().ToHashSet();
        while (waiting.Count > 0)
        {
            var ready = waiting.Where(node => !waiting.Any(other => other != node && other.Downstream.Contains(node))).ToList();
            if (ready.Count == 0)
                ready = [.. waiting]; // they feed each other round in a loop
            await Task.WhenAll(ready.Select(node => node.RemoveAsync())).ConfigureAwait(false);
            waiting.ExceptWith(ready);
        }
    }

    private Node NodeOf(ModuleInfo module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return _nodesByModule.TryGetValue(module, out var node)
            ? node
            : throw new ArgumentException($"{module} isn't part of this pipeline.", nameof(module));
    }

    /// <summary>
    /// Runs the pipeline until it halts: every source has finished and every message has left the pipeline.
    /// </summary>
    /// <param name="cancellationToken">
    /// Asks the pipeline to stop, as <see cref="Stop"/> does. Sources stop, and messages already in the pipeline
    /// get <see cref="PipelineOptions.DrainTimeout"/> to finish.
    /// </param>
    /// <exception cref="InvalidOperationException">The pipeline has already run, or a module couldn't be created.</exception>
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, Running, NotStarted) != NotStarted)
            throw new InvalidOperationException("A pipeline can only run once. Build a new one to run it again.");
        return RunCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Asks the pipeline to stop: sources are cancelled, and messages already in the pipeline get
    /// <see cref="PipelineOptions.DrainTimeout"/> to finish before modules are cancelled and the rest abandoned.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0 || Volatile.Read(ref _state) == Finished)
            return;
        try
        {
            _stopSources.Cancel();
            _abort.CancelAfter(Options.DrainTimeout);
        }
        catch (ObjectDisposedException)
        {
            // The pipeline finished in the meantime.
        }
    }

    /// <summary>Stops the pipeline without waiting for messages to finish, and disposes its modules.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, Finished, NotStarted) == NotStarted)
        {
            _abort.Dispose();
            return;
        }

        if (!_finished.Task.IsCompleted)
        {
            Stop();
            try
            {
                _abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pipeline finished in the meantime.
            }
        }
        await _finished.Task.ConfigureAwait(false);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var node in _nodes)
                node.CreateModule();
            s_running.TryAdd(this, 0);

            // The pipeline's traces start with its own sources, not inside the span of whatever started it, such as
            // the Host module. The modules' tasks, started below, inherit this.
            System.Diagnostics.Activity.Current = null;

            // Held while starting up, so the pipeline can't halt before every source is running.
            _inFlight.Add();
            foreach (var node in _inflections)
                _ = node.Start();
            foreach (var source in _sources)
            {
                _inFlight.Add(); // held until the source finishes
                source.Start(_stopSources.Token);
            }

            using (_abort.Token.Register(static state => ((Pipeline)state!).Abort(), this))
            using (cancellationToken.Register(static state => ((Pipeline)state!).Stop(), this))
            {
                ReleaseHold();
                await _halted.Task.ConfigureAwait(false);
            }

            // A change being applied as the pipeline halted finishes first.
            await _changing.WaitAsync().ConfigureAwait(false);
            _changing.Release();
            await Task.WhenAll(_inflections.Select(node => node.Loop)).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _state, Finished);
            s_running.TryRemove(this, out _);
            foreach (var node in _nodes)
                node.CloseTaps();
            foreach (var node in _nodes)
                await node.DisposeModuleAsync().ConfigureAwait(false);
            _abort.Dispose();
            _finished.TrySetResult();
        }
    }

    // Gives up waiting: cancels modules, and abandons sources and messages that haven't finished.
    private void Abort()
    {
        if (Volatile.Read(ref _state) != Running)
            return;
        _aborting = true;
        _stopSources.Cancel();
        foreach (var source in _sources)
            source.Abandon();
        foreach (var node in _inflections)
        {
            node.AbandonKept();
            node.Complete();
        }
    }

    private void OnHalted()
    {
        foreach (var node in _inflections)
            node.Complete();
        _halted.TrySetResult();
    }

    /// <summary>Stamps a message as emitted by <paramref name="from"/> and sends it downstream.</summary>
    /// <param name="emitter">The module, or its inflection, that the message records as emitting it.</param>
    /// <param name="defaultParent">The parent for a new message that doesn't name its own.</param>
    internal void Emit(Node from, ModuleInfo emitter, Message message, Message? defaultParent)
    {
        List<TrackingScope>? created = null;
        Stamper.Stamp(message, emitter, from, defaultParent, this, ref created);
        from.CountEmitted();
        if (from is SourceNode && message is not ErrorMessage)
            _sourceWindow?.Add(message.Id);
        if (Telemetry.Tracing)
            Telemetry.Emitted(from, emitter, message);
        from.Tapped(message);
        Deliver(from, message);
        ScopeSet.ReleaseAll(created);
    }

    /// <summary>Emits a new message carrying a lease, which stays open while the message or its descendants are in flight.</summary>
    /// <param name="holdUntilDone">
    /// Where a module that handles messages keeps the leases it made, so it holds them until it has finished handling
    /// its message (Q123). A source, which runs for as long as it likes, lets go of its hold at the lease's first emit.
    /// </param>
    internal void EmitLeased(Node from, ModuleInfo emitter, Message message, Lease lease, Message? defaultParent, List<Lease>? holdUntilDone = null)
    {
        if (message.IsStamped)
            throw new ArgumentException("Only a new message can carry a lease, not one that has been emitted before.", nameof(message));

        List<TrackingScope>? created = null;
        Stamper.Stamp(message, emitter, from, defaultParent, this, ref created);
        if (!lease.IsAttached)
        {
            // A failure while the lease closes is reported as coming from the module that first emitted it.
            var maker = from.Info;
            lease.Attach((closing, exception) => ReportFault(maker, $"Couldn't close lease {closing.Number}", exception));
        }
        message.Tracking = ScopeSet.Append(message.Tracking, lease.Scope);
        from.CountEmitted();
        if (from is SourceNode && message is not ErrorMessage)
            _sourceWindow?.Add(message.Id);
        if (Telemetry.Tracing)
            Telemetry.Emitted(from, emitter, message);
        from.Tapped(message);
        Deliver(from, message);
        ScopeSet.ReleaseAll(created);

        // The module that made the lease held it until now, so it couldn't close before it was emitted. One that
        // handles messages keeps holding it until it has finished with the message it's on.
        if (lease.TakeMakersHold())
        {
            if (holdUntilDone is not null)
                holdUntilDone.Add(lease);
            else
                lease.Scope.Release();
        }
    }

    /// <summary>Emits a new message, and reports when it and every message made from it have finished.</summary>
    internal Task<MessageOutcome> EmitTracked(Node from, ModuleInfo emitter, Message message, Message? defaultParent)
    {
        if (message.IsStamped)
            throw new ArgumentException("Only a new message can be tracked, not one that has been emitted before.", nameof(message));

        List<TrackingScope>? created = null;
        Stamper.Stamp(message, emitter, from, defaultParent, this, ref created);
        var outcome = new OutcomeScope();
        message.Tracking = ScopeSet.Append(message.Tracking, outcome);
        from.CountEmitted();
        if (from is SourceNode && message is not ErrorMessage)
            _sourceWindow?.Add(message.Id);
        if (Telemetry.Tracing)
            Telemetry.Emitted(from, emitter, message);
        from.Tapped(message);
        Deliver(from, message);

        // Settle any new error first, so an error that went nowhere is counted in the outcome.
        ScopeSet.ReleaseAll(created);
        outcome.Release();
        return outcome.Task;
    }

    /// <summary>Sends a message to each module <paramref name="from"/> feeds, or past one when it doesn't take it.</summary>
    internal void Deliver(Node from, Message message)
    {
        var links = from.Links;
        for (var i = 0; i < links.Targets.Length; i++)
            DeliverTo(links.Targets[i], message, depth: 0, links.Inputs[i]);
    }

    private void DeliverTo(InflectionNode target, Message message, int depth, int input)
    {
        if (depth > MaxForwardDepth)
        {
            target.CountFiltered();
            return;
        }
        if (target.IsStopped)
        {
            // The error policy stopped this module's branch. An error dropped here is reported as unhandled.
            target.CountDropped();
            ScopeSet.Flag(message.Tracking, MessageOutcome.Dropped);
            return;
        }
        if (target.Router is { } router)
        {
            // Distribute: the message goes on down one copy of the branch below, chosen here, on this thread.
            target.CountReceived();
            target.Tapped(message);
            Route(target, router, message, depth);
            return;
        }

        var isError = message is ErrorMessage;
        var wanted = target.Options.Errors switch
        {
            ErrorDelivery.Only => isError,
            ErrorDelivery.Exclude => !isError,
            _ => true,
        };

        if (wanted && target.Accepts(message))
        {
            if (!target.Handles(message))
            {
                // The module could take it, but the pipeline says its expressions don't fit this type: skip it.
                Forward(target, message, depth);
            }
            else if (target.HasExpiredFor(message))
            {
                // Expired messages stop at the next module that would take them (Q12).
                target.CountExpired();
                ScopeSet.Flag(message.Tracking, MessageOutcome.Expired);
            }
            else
            {
                target.Enqueue(message, input);
                if (target.IsPassthrough)
                {
                    target.Tapped(message);
                    Forward(target, message, depth);
                }
            }
        }
        else if (isError || target.Options.Bypass)
        {
            // Errors always look further downstream for a module that takes them; other messages only with bypass.
            Forward(target, message, depth);
        }
        else if (!wanted)
        {
            target.CountFiltered();
        }
        else
        {
            // None of the module's inflections takes this type: that's an error, not a silent drop (Q54).
            target.CountRefused();
            var error = new ErrorMessage { Error = $"{target.Info} doesn't take {message.GetType().Name} messages." };
            Emit(target, target.Info, error, defaultParent: message);
        }
    }

    private void Forward(InflectionNode from, Message message, int depth)
    {
        var links = from.Links;
        for (var i = 0; i < links.Targets.Length; i++)
            DeliverTo(links.Targets[i], message, depth + 1, links.Inputs[i]);
    }

    // The copies a router feeds are its downstream modules in order, each copy's in a run: copy n is the n-th run.
    private void Route(InflectionNode from, IRouteMessages router, Message message, int depth)
    {
        var links = from.Links;
        var lanes = router.Lanes;
        if (lanes < 1 || links.Targets.Length % lanes != 0)
        {
            Forward(from, message, depth);
            return;
        }
        int lane;
        try
        {
            lane = router.LaneOf(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lane = message.Id.GetHashCode();
        }
        lane = (int)((uint)lane % (uint)lanes);
        var size = links.Targets.Length / lanes;
        for (var i = lane * size; i < (lane + 1) * size; i++)
            DeliverTo(links.Targets[i], message, depth + 1, links.Inputs[i]);
    }

    /// <summary>Keeps a message a module has finished handling, with its leases open (<see cref="ModuleContext.Keep"/>).</summary>
    internal void Keep(Message message)
    {
        ScopeSet.AddRef(message.Tracking);
        Interlocked.Increment(ref _kept);
    }

    /// <summary>Stops keeping a message, which finishes unless something made from it is still in flight.</summary>
    internal void LetGo(Message message, MessageOutcome outcome)
    {
        ScopeSet.Release(message.Tracking, outcome);
        Interlocked.Decrement(ref _kept);
    }

    /// <summary>Holds the pipeline open, unless it has halted. Returns whether it did; release with <see cref="ReleaseHold"/>.</summary>
    internal bool TryHold() => _inFlight.TryAdd();

    /// <summary>Counts a message into a buffer or handler.</summary>
    internal void AddRef(Message message)
    {
        _inFlight.Add();
        ScopeSet.AddRef(message.Tracking);
    }

    /// <summary>Counts a message out of a buffer or handler.</summary>
    internal void Release(Message message, MessageOutcome outcome)
    {
        ScopeSet.Release(message.Tracking, outcome);
        ReleaseHold();
    }

    /// <summary>
    /// Releases one count, such as a source's or the startup hold, and halts at zero. Before halting, modules that keep
    /// messages are flushed once, so a part-filled batch still goes out; what they still keep after that is let go.
    /// </summary>
    internal void ReleaseHold()
    {
        if (!_inFlight.Release())
            return;
        if (Volatile.Read(ref _kept) > 0 && Interlocked.Exchange(ref _flushedAtEnd, 1) == 0 && FlushKept())
            return;
        if (!_inFlight.TryHalt())
            return; // something started meanwhile
        foreach (var node in _inflections)
            node.AbandonKept();
        OnHalted();
    }

    // Flushes every module that keeps messages, holding the pipeline open until each has. False if none keeps any.
    private bool FlushKept()
    {
        var flushing = false;
        foreach (var node in _inflections)
        {
            if (node.KeptCount == 0 || !_inFlight.TryAdd())
                continue;
            flushing = true;
            _ = node.FlushAsync(finishing: true).ContinueWith(_ => ReleaseHold(), TaskScheduler.Default);
        }
        return flushing;
    }

    /// <summary>
    /// Deals with an error that nothing handled, following the error policy of the module that emitted it: logs it
    /// unless the policy ignores it, and counts it towards stopping the pipeline. An error a module answered, such as
    /// Respond, is logged but not counted (Q100).
    /// </summary>
    internal void ReportUnhandled(ErrorMessage error)
    {
        var answered = error.AnsweredBy is not null;
        if (!answered)
            ScopeSet.Flag(error.Tracking, MessageOutcome.UnhandledError);
        var policy = error.EmittedBy is { } emitter && _nodesByModule.TryGetValue(emitter, out var node)
            ? node.Policy
            : Options.ErrorPolicy;
        if (!policy.LogUnhandled)
            return;
        try
        {
            (Options.UnhandledError ?? WriteToStandardError)(error);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The pipeline's unhandled-error callback failed: {exception}");
        }
        if (!answered)
            CountTowardsStopping(error);
    }

    /// <summary>Passes a notice about the pipeline itself to the notice callback.</summary>
    internal void Notify(string notice)
    {
        try
        {
            (Options.Notice ?? WriteNoticeToStandardError)(notice);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The pipeline's notice callback failed: {exception}");
        }
    }

    // stop-pipeline at N%: marks the source messages an unhandled error came from, and stops the pipeline once too
    // many of the recent ones have failed.
    private void CountTowardsStopping(ErrorMessage error)
    {
        if (_sourceWindow is null || Options.ErrorPolicy.StopPipelineAtPercent is not { } percent)
            return;
        var (failed, count) = (0, 0);
        foreach (var source in SourceMessagesBehind(error))
            (failed, count) = _sourceWindow.MarkFailed(source.Id);
        if (count >= ErrorPolicy.StopPipelineMinimum && failed * 100.0 >= percent * count)
        {
            StopForPolicy(
                $"{failed} of the last {count} messages from its sources ended in errors nothing handled, " +
                $"which reaches its limit of {percent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%");
        }
    }

    // The messages from sources that a message was made from, found by walking up its ancestors.
    private IEnumerable<Message> SourceMessagesBehind(Message message)
    {
        var seen = new HashSet<Message>(ReferenceEqualityComparer.Instance);
        var waiting = new Queue<Message>();
        waiting.Enqueue(message);
        while (waiting.TryDequeue(out var current))
        {
            if (!seen.Add(current))
                continue;
            if (current is not ErrorMessage && current.EmittedBy is { } emitter && _sourceInfos.Contains(emitter))
            {
                yield return current;
                continue;
            }
            foreach (var parent in current.Parents)
                waiting.Enqueue(parent);
        }
    }

    /// <summary>Stops the pipeline because of its error policy, recording why.</summary>
    internal void StopForPolicy(string reason)
    {
        if (Interlocked.CompareExchange(ref _stoppedBecause, reason, null) is not null)
            return;
        Notify($"Stopping the pipeline, because {reason}.");
        Stop();
    }

    /// <summary>Reports a problem with a module itself, rather than with a message.</summary>
    internal void ReportFault(ModuleInfo module, string problem, Exception exception)
    {
        var error = new ErrorMessage { Error = $"{problem}: {exception.Message}", Exception = exception };
        error.EmittedBy = module;
        ReportUnhandled(error);
    }

    private static void WriteToStandardError(ErrorMessage error) =>
        Console.Error.WriteLine(error.AnsweredBy is { } answerer
            ? $"Error from {error.EmittedBy?.ToString() ?? "the pipeline"}, answered by {answerer}: {error.Error}"
            : $"Unhandled error from {error.EmittedBy?.ToString() ?? "the pipeline"}: {error.Error}");

    private static void WriteNoticeToStandardError(string notice) => Console.Error.WriteLine(notice);
}
