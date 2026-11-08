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

    public override void CreateModule() => _module = (ASource)CreateInstance(Spec.Inflections[0]);

    public void Start(CancellationToken cancellationToken) => _ = Task.Run(() => RunAsync(cancellationToken));

    /// <summary>Treats the source as finished even if it's still running, because the pipeline gave up waiting.</summary>
    public void Abandon() => Finish();

    public override ValueTask DisposeModuleAsync() =>
        DisposeInstanceAsync(_module, $"{Info} couldn't be disposed");

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failuresInARow = 0;
        try
        {
            while (true)
            {
                try
                {
                    await _module!.Run(_context, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return; // stopped, as asked
                }
                catch (Exception exception)
                {
                    CountFailed();
                    _context.EmitFailure(exception);

                    // Q79: restart after a delay, until the source fails too often in a row without emitting anything.
                    failuresInARow = _context.TakeEmitted() ? 1 : failuresInARow + 1;
                    if (Policy.RestartSources is not { } restart)
                        return;
                    if (failuresInARow >= restart.FailuresInARow)
                    {
                        Pipeline.Notify($"Gave up on {Info}: it failed {failuresInARow} times in a row without emitting anything else.");
                        return;
                    }
                    if (!await RestartAsync(restart.Delay, cancellationToken).ConfigureAwait(false))
                        return;
                }
            }
        }
        finally
        {
            Finish();
        }
    }

    // Waits, then replaces the source with a fresh one. Returns false if the pipeline stopped meanwhile, or the
    // source couldn't be made again.
    private async Task<bool> RestartAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await DisposeInstanceAsync(_module, $"{Info} couldn't be disposed after it failed").ConfigureAwait(false);
        _module = null;
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            _module = (ASource)CreateInstance(Spec.Inflections[0]);
            CountRestart();
            return true;
        }
        catch (Exception exception)
        {
            Pipeline.ReportFault(Info, $"{Info} couldn't be restarted", exception);
            return false;
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
