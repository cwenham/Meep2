using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>The context for a source. Sources can emit from any thread until they finish.</summary>
internal sealed class SourceContext(SourceNode node) : ModuleContext
{
    private volatile bool _closed;
    private volatile bool _emitted;

    public override ModuleInfo Module => node.Info;

    public override CancellationToken Stopping => node.Pipeline.StoppingToken;

    public override string PipelineName => node.Pipeline.Name;

    public override System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared => node.Pipeline.Shared;

    public override void CountBytes(long received, long sent) => node.CountBytes(received, sent);

    public override void Listening(string? address) => node.ListeningAt = address;

    public override string? HostId => node.Pipeline.Options.HostId;

    public override string? Origin => node.Origin;

    public override void ContinueTrace(Message message, string traceParent)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(traceParent);
        if (Telemetry.Tracing)
            Telemetry.Continue(message, traceParent);
    }

    public override Tally Arrival(string from) => node.ArrivalFrom(from);

    public override Tally Destination(string endpoint) => node.DestinationAt(endpoint);

    /// <summary>Called when the source finishes. Emitting after that is a mistake, and it's no longer listening.</summary>
    public void Close()
    {
        _closed = true;
        node.ListeningAt = null;
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
