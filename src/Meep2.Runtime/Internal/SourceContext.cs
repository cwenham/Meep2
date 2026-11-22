using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>The context for a source. Sources can emit from any thread until they finish.</summary>
internal sealed class SourceContext(SourceNode node) : NodeContext<SourceNode>(node)
{
    private volatile bool _closed;
    private volatile bool _emitted;

    public override void ContinueTrace(Message message, string traceParent)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(traceParent);
        if (Telemetry.Tracing)
            Telemetry.Continue(message, traceParent);
    }

    /// <summary>Called when the source finishes. Emitting after that is a mistake, and it's no longer listening.</summary>
    public void Close()
    {
        _closed = true;
        Node.ListeningAt = null;
    }

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
        Node.Pipeline.Emit(Node, Node.Info, message, defaultParent: null);
    }

    public override void Emit(Message message, Lease lease)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        ThrowIfClosed();
        if (message is not ErrorMessage)
            _emitted = true;
        Node.Pipeline.EmitLeased(Node, Node.Info, message, lease, defaultParent: null);
    }

    public override Task<MessageOutcome> EmitTracked(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfClosed();
        if (message is not ErrorMessage)
            _emitted = true;
        return Node.Pipeline.EmitTracked(Node, Node.Info, message, defaultParent: null);
    }

    /// <summary>Emits an error for a source that threw, or reports it directly if the source has finished.</summary>
    public void EmitFailure(Exception exception)
    {
        var error = new ErrorMessage { Error = exception.Message, Exception = exception };
        if (_closed)
        {
            error.EmittedBy = Node.Info;
            Node.Pipeline.ReportUnhandled(error);
        }
        else
        {
            Node.Pipeline.Emit(Node, Node.Info, error, defaultParent: null);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException($"{Node.Info} has finished, so it can't emit any more messages.");
    }
}
