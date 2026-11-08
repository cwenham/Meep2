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
    private readonly InFlightCounter _inFlight = new();
    private readonly CancellationTokenSource _stopSources = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource _halted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private int _stopping;
    private volatile bool _aborting;

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
        }

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
    /// <param name="defaultParent">The parent for a new message that doesn't name its own.</param>
    internal void Emit(Node from, Message message, Message? defaultParent)
    {
        List<TrackingScope>? created = null;
        Stamper.Stamp(message, from.Info, defaultParent, this, ref created);
        from.CountEmitted();
        Deliver(from.Downstream, message);
        ScopeSet.ReleaseAll(created);
    }

    /// <summary>Emits a new message, and reports when it and every message made from it have finished.</summary>
    internal Task<MessageOutcome> EmitTracked(Node from, Message message, Message? defaultParent)
    {
        if (message.IsStamped)
            throw new ArgumentException("Only a new message can be tracked, not one that has been emitted before.", nameof(message));

        List<TrackingScope>? created = null;
        Stamper.Stamp(message, from.Info, defaultParent, this, ref created);
        var outcome = new OutcomeScope();
        message.Tracking = ScopeSet.Append(message.Tracking, outcome);
        from.CountEmitted();
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

        var isError = message is ErrorMessage;
        var wanted = target.Options.Errors switch
        {
            ErrorDelivery.Only => isError,
            ErrorDelivery.Exclude => !isError,
            _ => true,
        };

        if (wanted && target.Accepts(message))
        {
            if (target.Handles(message))
            {
                target.Enqueue(message);
                if (target.IsPassthrough)
                    Forward(target, message, depth);
            }
            else
            {
                // The module could take it, but the pipeline says its expressions don't fit this type: skip it.
                Forward(target, message, depth);
            }
        }
        else if (isError || target.Options.Bypass)
        {
            // Errors always look further downstream for a module that takes them; other messages only with bypass.
            Forward(target, message, depth);
        }
        else
        {
            target.CountFiltered();
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

    /// <summary>Passes an error that left the pipeline unhandled to the error callback.</summary>
    internal void ReportUnhandled(ErrorMessage error)
    {
        ScopeSet.Flag(error.Tracking, MessageOutcome.UnhandledError);
        try
        {
            (Options.UnhandledError ?? WriteToStandardError)(error);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The pipeline's unhandled-error callback failed: {exception}");
        }
    }

    /// <summary>Reports a problem with a module itself, rather than with a message.</summary>
    internal void ReportFault(ModuleInfo module, string problem, Exception exception)
    {
        var error = new ErrorMessage { Error = $"{problem}: {exception.Message}", Exception = exception };
        error.EmittedBy = module;
        ReportUnhandled(error);
    }

    private static void WriteToStandardError(ErrorMessage error) =>
        Console.Error.WriteLine($"Unhandled error from {error.EmittedBy?.ToString() ?? "the pipeline"}: {error.Error}");
}
