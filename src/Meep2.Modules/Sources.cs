using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Emits a <see cref="Tick"/> straight away, then again every period, until stopped or until it has emitted
/// <c>count</c> ticks. In Meeplang: <c>Interval 1m</c> or <c>Interval 500ms count=3</c>.
/// </summary>
[Verb("Interval")]
[Emits(typeof(Tick))]
public sealed class Interval(IntervalParameters parameters) : ASource, IReconfigurable<IntervalParameters>
{
    private volatile IntervalParameters _parameters = parameters;
    private volatile PeriodicTimer? _timer;

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_parameters.Period);
        _timer = timer;
        for (long count = 1; ; count++)
        {
            var current = _parameters;
            context.Emit(new Tick(count) { Payload = current.Payload });
            if (count >= current.Count)
                return;
            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// Takes a new period, count and payload while it runs, carrying on its count (Q121). A new count it has already
    /// reached ends it at the next tick.
    /// </summary>
    public bool ParametersChanged(IntervalParameters old, IntervalParameters next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        _parameters = next;
        if (next.Period != old.Period && _timer is { } timer)
        {
            try
            {
                timer.Period = next.Period;
            }
            catch (ObjectDisposedException)
            {
                // It has finished meanwhile.
            }
        }
        return true;
    }
}

/// <summary>Parameters for <see cref="Interval"/>.</summary>
public sealed record IntervalParameters
{
    /// <summary>How long between ticks.</summary>
    [Positional]
    public required TimeSpan Period
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Period), "The period must be longer than zero.");
    }

    /// <summary>How many ticks to emit before finishing. Without it, Interval runs until stopped.</summary>
    public long? Count
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Count), "The count must be at least 1.");
    }

    /// <summary>Text to put in each tick, for telling intervals apart.</summary>
    public string? Payload { get; init; }
}

/// <summary>
/// Reads a text file once and emits its contents as <see cref="FileText"/>. In Meeplang: <c>Read "~/notes.txt"</c>.
/// A leading <c>~</c> means your home folder; other relative paths start from the current folder.
/// </summary>
[Verb("Read")]
[Emits(typeof(FileText))]
public sealed class Read(ReadParameters parameters) : ASource
{
    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var path = Paths.Expand(parameters.Path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        context.Emit(new FileText(path, text));
    }
}

/// <summary>Parameters for <see cref="Read"/>.</summary>
public sealed record ReadParameters
{
    /// <summary>The file to read.</summary>
    [Positional]
    public required string Path { get; init; }
}

/// <summary>Resolves paths written in pipelines.</summary>
internal static class Paths
{
    /// <summary>Turns a leading <c>~</c> into the home folder, and makes the path absolute.</summary>
    public static string Expand(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
        return Path.GetFullPath(path);
    }
}
