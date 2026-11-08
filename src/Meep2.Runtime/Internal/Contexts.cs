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
    private bool _passedOn;

    public override ModuleInfo Module => node.Info;

    /// <summary>Whether the module emitted the message it was given, passing it on unchanged.</summary>
    public bool PassedOn => _passedOn;

    public void Begin(Message message)
    {
        _current = message;
        _passedOn = false;
    }

    public void End() => _current = null;

    public override void Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = _current ?? throw NotHandling();
        if (ReferenceEquals(message, current))
        {
            // Passing the message on unchanged. A passthrough module's messages have already gone on.
            _passedOn = true;
            if (!node.IsPassthrough)
                node.Pipeline.Deliver(node.Downstream, message);
            return;
        }
        node.Pipeline.Emit(node, message, defaultParent: current);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = _current ?? throw NotHandling();
        return node.Pipeline.EmitTracked(node, message, defaultParent: current);
    }

    /// <summary>Emits an error in place of the message the module failed on.</summary>
    public void EmitFailure(Message message, Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception, Parents = new Parents(message) };
        node.Pipeline.Emit(node, error, defaultParent: message);
    }

    private InvalidOperationException NotHandling() =>
        new($"{node.Info} tried to emit while it wasn't handling a message. Modules can only emit from HandleAsync.");
}

/// <summary>The context for a source. Sources can emit from any thread until they finish.</summary>
internal sealed class SourceContext(SourceNode node) : ModuleContext
{
    private volatile bool _closed;

    public override ModuleInfo Module => node.Info;

    /// <summary>Called when the source finishes. Emitting after that is a mistake.</summary>
    public void Close() => _closed = true;

    public override void Emit(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        node.Pipeline.Emit(node, message, defaultParent: null);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        return node.Pipeline.EmitTracked(node, message, defaultParent: null);
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
            node.Pipeline.Emit(node, error, defaultParent: null);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException($"{node.Info} has finished, so it can't emit any more messages.");
    }
}
