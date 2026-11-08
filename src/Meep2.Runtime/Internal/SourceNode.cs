using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>A source (fountainhead), running on its own task until it finishes or is stopped.</summary>
internal sealed class SourceNode : Node
{
    private readonly SourceContext _context;
    private ASource? _module;
    private int _finished;

    public SourceNode(Pipeline pipeline, NodeSpec spec)
        : base(pipeline, spec) => _context = new SourceContext(this);

    public override void CreateModule() => _module = (ASource)CreateInstance();

    public void Start(CancellationToken cancellationToken) => _ = Task.Run(() => RunAsync(cancellationToken));

    /// <summary>Treats the source as finished even if it's still running, because the pipeline gave up waiting.</summary>
    public void Abandon() => Finish();

    public override ValueTask DisposeModuleAsync() =>
        DisposeInstanceAsync(_module, $"{Info} couldn't be disposed");

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _module!.Run(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped, as asked.
        }
        catch (Exception exception)
        {
            // Until the error policy arrives, a source that throws reports the error and counts as finished.
            CountFailed();
            _context.EmitFailure(exception);
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        _context.Close();
        Pipeline.ReleaseHold();
    }
}
