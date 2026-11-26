using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

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
        if (Tracing.IsOn)
            Tracing.Continue(message, traceParent);
    }

    /// <summary>Called when the source finishes. Emitting after that is a mistake, and it's no longer listening.</summary>
    public void Close()
    {
        _closed = true;
        Node.PeerCounts.ListeningAt = null;
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

    // What the running instance checked the pipeline's own credential for, to check again when that might change (Q319).
    private readonly Lock _checking = new();
    private List<(string Permission, PermissionTarget? Target)> _checked = [];

    internal override string? WhyNotHeld(string permission, Message? message, PermissionTarget? target)
    {
        var why = base.WhyNotHeld(permission, message, target);
        if (message is null && why is null)
        {
            lock (_checking)
                _checked.Add((permission, target));
        }
        return why;
    }

    /// <summary>Forgets what the last instance checked, as a new one starts.</summary>
    public void ForgetChecks()
    {
        lock (_checking)
            _checked = [];
    }

    /// <summary>The first thing the running instance checked that the pipeline's own credential no longer holds, or null.</summary>
    public PermissionDeniedException? FirstNoLongerHeld()
    {
        List<(string Permission, PermissionTarget? Target)> checks;
        lock (_checking)
            checks = [.. _checked];
        foreach (var (permission, target) in checks)
        {
            if (base.WhyNotHeld(permission, null, target) is { } why)
                return new PermissionDeniedException(PermissionName.Parse(permission), why);
        }
        return null;
    }

    /// <summary>Emits the refusal of something the source was about to do, before it finishes.</summary>
    public void EmitDenial(PermissionDenied denial) => Node.Pipeline.Emit(Node, Node.Info, denial, defaultParent: null);

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException($"{Node.Info} has finished, so it can't emit any more messages.");
    }
}
