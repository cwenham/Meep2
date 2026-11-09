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

    private readonly Node[] _nodes;
    private readonly InflectionNode[] _inflections;
    private readonly SourceNode[] _sources;
    private readonly Dictionary<ModuleInfo, Node> _nodesByModule;
    private readonly HashSet<ModuleInfo> _sourceInfos;
    private readonly SourceWindow? _sourceWindow;
    private readonly InFlightCounter _inFlight = new();
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

        _nodesByModule = new Dictionary<ModuleInfo, Node>(specs.Length);
        var nodes = new Node[specs.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            nodes[i] = spec.IsSource
                ? new SourceNode(this, spec)
                : new InflectionNode(this, spec, spec.Options.BufferSize ?? options.BufferSize);
            _nodesByModule.Add(spec.Info, nodes[i]);
            foreach (var inflection in spec.Inflections)
                _nodesByModule.TryAdd(inflection.Info, nodes[i]);
        }
        _sourceInfos = [.. specs.Where(spec => spec.IsSource).Select(spec => spec.Info)];
        if (options.ErrorPolicy.StopPipelineAtPercent is not null)
            _sourceWindow = new SourceWindow(ErrorPolicy.StopPipelineWindow);

        var downstream = new Dictionary<Node, List<InflectionNode>>();
        foreach (var (from, to) in connections)
        {
            var upstream = _nodesByModule[from];
            if (!downstream.TryGetValue(upstream, out var targets))
                downstream[upstream] = targets = [];
            targets.Add((InflectionNode)_nodesByModule[to]);
        }
        foreach (var (node, targets) in downstream)
            node.Downstream = [.. targets];

        _nodes = nodes;
        _inflections = [.. nodes.OfType<InflectionNode>()];
        _sources = [.. nodes.OfType<SourceNode>()];
    }

    /// <summary>The pipeline's name.</summary>
    public string Name { get; }

    /// <summary>Settings for the whole pipeline.</summary>
    public PipelineOptions Options { get; }

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
    public ModuleStats GetStats(ModuleInfo module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return _nodesByModule.TryGetValue(module, out var node)
            ? node.GetStats()
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

            // Held while starting up, so the pipeline can't halt before every source is running.
            _inFlight.Add();
            var loops = new Task[_inflections.Length];
            for (var i = 0; i < _inflections.Length; i++)
                loops[i] = _inflections[i].Start();
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

            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _state, Finished);
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
            node.Complete();
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
        Deliver(from.Downstream, message);
        ScopeSet.ReleaseAll(created);
    }

    /// <summary>Emits a new message carrying a lease, which stays open while the message or its descendants are in flight.</summary>
    internal void EmitLeased(Node from, ModuleInfo emitter, Message message, Lease lease, Message? defaultParent)
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
        Deliver(from.Downstream, message);
        ScopeSet.ReleaseAll(created);

        // The module that made the lease held it until now, so it couldn't close before it was emitted.
        if (lease.TakeMakersHold())
            lease.Scope.Release();
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
        Deliver(from.Downstream, message);

        // Settle any new error first, so an error that went nowhere is counted in the outcome.
        ScopeSet.ReleaseAll(created);
        outcome.Release();
        return outcome.Task;
    }

    /// <summary>Sends a message to each target, or past it when the target doesn't take it.</summary>
    internal void Deliver(InflectionNode[] targets, Message message)
    {
        foreach (var target in targets)
            DeliverTo(target, message, depth: 0);
    }

    private void DeliverTo(InflectionNode target, Message message, int depth)
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
                target.Enqueue(message);
                if (target.IsPassthrough)
                    Forward(target, message, depth);
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
        foreach (var next in from.Downstream)
            DeliverTo(next, message, depth + 1);
    }

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

    /// <summary>Releases one count, such as a source's or the startup hold, and halts at zero.</summary>
    internal void ReleaseHold()
    {
        if (_inFlight.Release())
            OnHalted();
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
