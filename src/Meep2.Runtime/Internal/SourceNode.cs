using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>A source (fountainhead), running on its own task until it finishes or is stopped.</summary>
internal sealed class SourceNode : Node
{
    private readonly SourceContext _context;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _changing = new();
    private ASource? _module;
    private CancellationTokenSource? _instance;
    private NodeSpec? _restartWith;
    private int _finished;

    public SourceNode(Pipeline pipeline, NodeSpec spec)
        : base(pipeline, spec) => _context = new SourceContext(this);

    public override void CreateModule() => _module = (ASource)CreateInstance(Spec.Inflections[0]);

    public void Start(CancellationToken cancellationToken) => _ = Task.Run(() => RunAsync(cancellationToken));

    /// <summary>Treats the source as finished even if it's still running, because the pipeline gave up waiting.</summary>
    public void Abandon() => Finish();

    public override ValueTask DisposeModuleAsync() =>
        DisposeInstanceAsync(_module, $"{Info} couldn't be disposed");

    /// <summary>Takes the source out of the running pipeline: it's stopped, and disposed once it has.</summary>
    public async Task RemoveAsync()
    {
        lock (_changing)
        {
            IsRemoved = true;
            _restartWith = null;
            _instance?.Cancel();
        }
        await _ended.Task.ConfigureAwait(false);
        CloseTaps();
        await DisposeModuleAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A source that takes new parameters is asked while it runs. One that doesn't is stopped, and started again
    /// with them.
    /// </remarks>
    public override Task<bool> UpdateAsync(NodeSpec next, bool reconfigure)
    {
        lock (_changing)
        {
            var old = Spec.Inflections[0];
            var running = _module;
            if (!reconfigure || (running is not null && TryReconfigure(running, old, next.Inflections[0])))
            {
                Spec = next;
                return Task.FromResult(true);
            }
            if (_finished != 0 || running is null)
            {
                Spec = next; // it isn't running, so the new parameters wait for a restart that won't come
                return Task.FromResult(false);
            }
            _restartWith = next;
            _instance?.Cancel();
            return Task.FromResult(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failuresInARow = 0;
        try
        {
            while (true)
            {
                CancellationToken token;
                lock (_changing)
                {
                    if (IsRemoved)
                        return;
                    _instance?.Dispose();
                    _instance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    token = _instance.Token;
                }
                try
                {
                    await _module!.Run(_context, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested || cancellationToken.IsCancellationRequested || IsRemoved)
                        return; // it finished, or stopped as asked
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested || IsRemoved)
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
                    continue;
                }

                // A change stopped this instance to give the source new parameters.
                if (!await SwapAsync().ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            Finish();
        }
    }

    // A change stopped the running instance to give it new parameters: replace it with one made with them.
    private async Task<bool> SwapAsync()
    {
        NodeSpec next;
        lock (_changing)
        {
            if (_restartWith is null)
                return false;
            next = _restartWith;
            _restartWith = null;
        }
        var old = _module;
        try
        {
            _module = (ASource)CreateInstance(next.Inflections[0]);
            Spec = next;
        }
        catch (Exception exception)
        {
            // The old instance can't be resumed once stopped, so the source ends.
            Pipeline.ReportFault(next.Info, $"{next.Info} couldn't be made with its new parameters, so it has stopped", exception);
            return false;
        }
        await DisposeInstanceAsync(old, $"{Info} couldn't be disposed after its parameters changed").ConfigureAwait(false);
        return true;
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
        lock (_changing)
        {
            _instance?.Dispose();
            _instance = null;
        }
        _context.Close();
        _ended.TrySetResult();
        Pipeline.ReleaseHold();
    }
}
