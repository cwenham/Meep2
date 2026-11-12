using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// The context for a module that handles messages. One instance is reused for every message, which works because
/// the module handles one message at a time.
/// </summary>
internal sealed class InflectionContext(InflectionNode node) : ModuleContext
{
    private Message? _current;
    private ModuleInfo? _emitter;
    private bool _passedOn;
    private int _input;
    private bool _flushing;
    private bool _finishing;

    // Messages the module keeps after handling them (Keep), until it emits one made from them or lets them go.
    private readonly HashSet<Message> _kept = new(ReferenceEqualityComparer.Instance);

    // Leases this module made while handling the message, which it holds until it has finished with it (Q123), so
    // a lease that goes out on several messages can't close between them.
    private List<Lease>? _made;

    // While handling a message, the inflection doing the work, so the messages it emits record it.
    public override ModuleInfo Module => _emitter ?? node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    public override string PipelineName => node.Pipeline.Name;

    public override System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared => node.Pipeline.Shared;

    public override Outlet Outlet => new NodeOutlet(node, Module);

    /// <summary>Whether the module emitted the message it was given, passing it on unchanged.</summary>
    public bool PassedOn => _passedOn;

    public override int Input => _input;

    public override bool Finishing => _flushing && _finishing;

    public override int BufferSize => node.Capacity;

    public int KeptCount
    {
        get
        {
            lock (_kept)
                return _kept.Count;
        }
    }

    public void Begin(Message message, ModuleInfo emitter, int input)
    {
        _current = message;
        _emitter = emitter;
        _passedOn = false;
        _input = input;
    }

    /// <summary>The module is flushing what it keeps: it can emit with no message being handled.</summary>
    /// <param name="finishing">Whether nothing else is left for the pipeline to do.</param>
    public void BeginFlush(bool finishing)
    {
        _flushing = true;
        _finishing = finishing;
        _input = 0;
    }

    public void EndFlush()
    {
        _flushing = false;
        _finishing = false;
        ReleaseMadeLeases();
    }

    public override void Keep(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsStamped)
            throw new ArgumentException("Only a message the module has received can be kept.", nameof(message));
        lock (_kept)
        {
            if (!_kept.Add(message))
                return;
        }
        node.Pipeline.Keep(message);
    }

    public override void LetGo(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool removed;
        lock (_kept)
            removed = _kept.Count > 0 && _kept.Remove(message);
        if (removed)
            node.Pipeline.LetGo(message, MessageOutcome.Completed);
    }

    public override void FlushAfter(TimeSpan delay) => node.FlushAfter(delay);

    /// <summary>Lets go of everything kept, as the pipeline halts with nothing left to make of it.</summary>
    public void AbandonKept()
    {
        Message[] kept;
        lock (_kept)
        {
            kept = [.. _kept];
            _kept.Clear();
        }
        foreach (var message in kept)
            node.Pipeline.LetGo(message, MessageOutcome.Abandoned);
    }

    // A message made from kept messages carries them on, so the module stops keeping them once it's emitted.
    private void LetGoOfParents(Message message)
    {
        if (message.Parents.Count == 0)
            return;
        List<Message>? released = null;
        lock (_kept)
        {
            if (_kept.Count == 0)
                return;
            foreach (var parent in message.Parents)
            {
                if (_kept.Remove(parent))
                    (released ??= []).Add(parent);
            }
        }
        foreach (var parent in released ?? [])
            node.Pipeline.LetGo(parent, MessageOutcome.Completed);
    }

    public void End()
    {
        _current = null;
        _emitter = null;
        ReleaseMadeLeases();
    }

    private void ReleaseMadeLeases()
    {
        if (_made is { Count: > 0 } made)
        {
            foreach (var lease in made)
                lease.Scope.Release();
            made.Clear();
        }
    }

    public override void Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = Current();
        if (current is not null && ReferenceEquals(message, current))
        {
            // Passing the message on unchanged. A passthrough module's messages have already gone on.
            _passedOn = true;
            if (!node.IsPassthrough)
            {
                node.Tapped(message);
                node.Pipeline.Deliver(node, message);
            }
            return;
        }
        node.Pipeline.Emit(node, Module, message, defaultParent: current);
        LetGoOfParents(message);
        LetGo(message); // a message it kept, passed on as it is
    }

    public override void Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        var current = Current();
        node.Pipeline.EmitLeased(node, Module, message, lease, defaultParent: current, holdUntilDone: _made ??= []);
        LetGoOfParents(message);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = Current();
        var outcome = node.Pipeline.EmitTracked(node, Module, message, defaultParent: current);
        LetGoOfParents(message);
        return outcome;
    }

    // The message being handled; null while flushing, when there isn't one.
    private Message? Current() => _current ?? (_flushing ? null : throw NotHandling());

    /// <summary>Emits an error in place of the message the module failed on.</summary>
    public void EmitFailure(Message message, Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception, Parents = new Parents(message) };
        node.Pipeline.Emit(node, Module, error, defaultParent: message);
    }

    private InvalidOperationException NotHandling() =>
        new($"{node.Info} tried to emit while it wasn't handling a message. Modules can only emit from HandleAsync and FlushAsync.");
}

/// <summary>The context for a source. Sources can emit from any thread until they finish.</summary>
internal sealed class SourceContext(SourceNode node) : ModuleContext
{
    private volatile bool _closed;
    private volatile bool _emitted;

    public override ModuleInfo Module => node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    public override string PipelineName => node.Pipeline.Name;

    public override System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared => node.Pipeline.Shared;

    /// <summary>Called when the source finishes. Emitting after that is a mistake.</summary>
    public void Close() => _closed = true;

    /// <summary>Whether the source has emitted a message, other than an error, since this was last asked (Q79).</summary>
    public bool TakeEmitted()
    {
        var emitted = _emitted;
        _emitted = false;
        return emitted;
    }

    public override void Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        if (message is not ErrorMessage)
            _emitted = true;
        node.Pipeline.Emit(node, node.Info, message, defaultParent: null);
    }

    public override void Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        ThrowIfClosed();
        if (message is not ErrorMessage)
            _emitted = true;
        node.Pipeline.EmitLeased(node, node.Info, message, lease, defaultParent: null);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        if (message is not ErrorMessage)
            _emitted = true;
        return node.Pipeline.EmitTracked(node, node.Info, message, defaultParent: null);
    }

    /// <summary>Emits an error for a source that threw, or reports it directly if the source has finished.</summary>
    public void EmitFailure(Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception };
        if (_closed)
        {
            error.EmittedBy = node.Info;
            node.Pipeline.ReportUnhandled(error);
        }
        else
        {
            node.Pipeline.Emit(node, node.Info, error, defaultParent: null);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException($"{node.Info} has finished, so it can't emit any more messages.");
    }
}

/// <summary>
/// Emits for a module that handles messages at any time, from any thread (<see cref="ModuleContext.Outlet"/>). It
/// holds the pipeline open while it emits, and does nothing once the pipeline has halted.
/// </summary>
internal sealed class NodeOutlet(InflectionNode node, ModuleInfo emitter) : Outlet
{
    public override bool Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!node.Pipeline.TryHold())
            return false;
        try
        {
            node.Pipeline.Emit(node, emitter, message, defaultParent: null);
        }
        finally
        {
            node.Pipeline.ReleaseHold();
        }
        return true;
    }

    public override bool Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        if (!node.Pipeline.TryHold())
            return false;
        try
        {
            node.Pipeline.EmitLeased(node, emitter, message, lease, defaultParent: null);
        }
        finally
        {
            node.Pipeline.ReleaseHold();
        }
        return true;
    }
}
