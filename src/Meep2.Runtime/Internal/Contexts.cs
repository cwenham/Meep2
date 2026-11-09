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

    // While handling a message, the inflection doing the work, so the messages it emits record it.
    public override ModuleInfo Module => _emitter ?? node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    /// <summary>Whether the module emitted the message it was given, passing it on unchanged.</summary>
    public bool PassedOn => _passedOn;

    public void Begin(Message message, ModuleInfo emitter)
    {
        _current = message;
        _emitter = emitter;
        _passedOn = false;
    }

    public void End()
    {
        _current = null;
        _emitter = null;
    }

    public override void Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = _current ?? throw NotHandling();
        if (ReferenceEquals(message, current))
        {
            // Passing the message on unchanged. A passthrough module's messages have already gone on.
            _passedOn = true;
            if (!node.IsPassthrough)
            {
                node.Tapped(message);
                node.Pipeline.Deliver(node.Downstream, message);
            }
            return;
        }
        node.Pipeline.Emit(node, Module, message, defaultParent: current);
    }

    public override void Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        var current = _current ?? throw NotHandling();
        node.Pipeline.EmitLeased(node, Module, message, lease, defaultParent: current);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = _current ?? throw NotHandling();
        return node.Pipeline.EmitTracked(node, Module, message, defaultParent: current);
    }

    /// <summary>Emits an error in place of the message the module failed on.</summary>
    public void EmitFailure(Message message, Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception, Parents = new Parents(message) };
        node.Pipeline.Emit(node, Module, error, defaultParent: message);
    }

    private InvalidOperationException NotHandling() =>
        new($"{node.Info} tried to emit while it wasn't handling a message. Modules can only emit from HandleAsync.");
}

/// <summary>The context for a source. Sources can emit from any thread until they finish.</summary>
internal sealed class SourceContext(SourceNode node) : ModuleContext
{
    private volatile bool _closed;
    private volatile bool _emitted;

    public override ModuleInfo Module => node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

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
