using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// The context for a module that handles messages. One instance is reused for every message, which works because
/// the module handles one message at a time.
/// </summary>
internal sealed class InflectionContext(InflectionNode node) : NodeContext<InflectionNode>(node)
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
    public override ModuleInfo Module => _emitter ?? Node.Info;

    public override Outlet Outlet => new NodeOutlet(Node, Module);

    public override string? TraceParent => Telemetry.Tracing ? Telemetry.TraceParent(Node, _current) : null;

    /// <summary>Whether the module emitted the message it was given, passing it on unchanged.</summary>
    public bool PassedOn => _passedOn;

    public override int Input => _input;

    public override bool Finishing => _flushing && _finishing;

    public override int BufferSize => Node.Capacity;

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
        Node.Pipeline.Keep(message);
    }

    public override void LetGo(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool removed;
        lock (_kept)
            removed = _kept.Count > 0 && _kept.Remove(message);
        if (removed)
            Node.Pipeline.LetGo(message, MessageOutcome.Completed);
    }

    public override void FlushAfter(TimeSpan delay) => Node.FlushAfter(delay);

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
            Node.Pipeline.LetGo(message, MessageOutcome.Abandoned);
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
            Node.Pipeline.LetGo(parent, MessageOutcome.Completed);
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
            if (!Node.IsPassthrough)
            {
                Node.Counters.Passed();
                Node.Taps.Offer(message);
                Node.Pipeline.Deliver(Node, message);
            }
            return;
        }
        Node.Pipeline.Emit(Node, Module, message, defaultParent: current);
        LetGoOfParents(message);
        LetGo(message); // a message it kept, passed on as it is
    }

    public override void Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        var current = Current();
        Node.Pipeline.EmitLeased(Node, Module, message, lease, defaultParent: current, holdUntilDone: _made ??= []);
        LetGoOfParents(message);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = Current();
        var outcome = Node.Pipeline.EmitTracked(Node, Module, message, defaultParent: current);
        LetGoOfParents(message);
        return outcome;
    }

    // The message being handled; null while flushing, when there isn't one.
    private Message? Current() => _current ?? (_flushing ? null : throw NotHandling());

    /// <summary>Emits an error in place of the message the module failed on.</summary>
    public void EmitFailure(Message message, Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception, Parents = new Parents(message) };
        Node.Pipeline.Emit(Node, Module, error, defaultParent: message);
    }

    private InvalidOperationException NotHandling() =>
        new($"{Node.Info} tried to emit while it wasn't handling a message. Modules can only emit from HandleAsync and FlushAsync.");
}
