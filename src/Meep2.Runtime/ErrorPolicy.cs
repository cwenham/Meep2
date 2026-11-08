namespace Meep2.Runtime;

/// <summary>
/// What happens when things go wrong: Meeplang's <c>on-error</c>. A pipeline has one
/// (<see cref="PipelineOptions.ErrorPolicy"/>), and a module can have its own for itself and the branch below it
/// (<see cref="NodeOptions.ErrorPolicy"/>).
/// </summary>
/// <remarks>
/// The defaults log errors nothing handles, stop a module and its branch after it throws 5 times within a second,
/// stop the pipeline when 10% of the messages from its sources end in unhandled errors, and restart a source that
/// throws after 100 ms, giving up after 3 failures in a row. In Meeplang:
/// <c>on-error = log, stop-branch after 5 in 1s, stop-pipeline at 10%, restart after 100ms up to 3 times</c>.
/// </remarks>
public sealed record ErrorPolicy
{
    /// <summary>The system defaults.</summary>
    public static ErrorPolicy Default { get; } = new();

    /// <summary>
    /// Whether errors that nothing handles are logged, through <see cref="PipelineOptions.UnhandledError"/>.
    /// Meeplang's <c>log</c> or <c>ignore</c>. Ignored errors don't count towards <see cref="StopPipelineAtPercent"/>.
    /// </summary>
    public bool LogUnhandled { get; init; } = true;

    /// <summary>
    /// Stops a module, and so the branch below it, once it has thrown this often. Messages that reach it afterwards
    /// are dropped. Null never stops it. Meeplang's <c>stop-branch after 5 in 1s</c> or <c>stop-branch never</c>.
    /// </summary>
    public FailureLimit? StopBranch { get; init; } = new(5, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Stops the pipeline once this percentage of the messages from its sources have ended in errors that nothing
    /// handled. It counts the last <see cref="StopPipelineWindow"/> messages, once at least
    /// <see cref="StopPipelineMinimum"/> have been sent. Null never stops it. Meeplang's <c>stop-pipeline at 10%</c>
    /// or <c>stop-pipeline never</c>. Only the pipeline's own policy uses this.
    /// </summary>
    public double? StopPipelineAtPercent
    {
        get;
        init => field = value is null or (> 0 and <= 100)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(StopPipelineAtPercent), value, "The percentage has to be more than 0 and at most 100.");
    } = 10;

    /// <summary>How a source that throws is restarted (Q79). Null never restarts it. Meeplang's <c>restart after 100ms up to 3 times</c> or <c>restart never</c>.</summary>
    public SourceRestart? RestartSources { get; init; } = new(TimeSpan.FromMilliseconds(100), 3);

    /// <summary>How many of the most recent messages from sources <see cref="StopPipelineAtPercent"/> counts.</summary>
    public const int StopPipelineWindow = 100;

    /// <summary>How many messages sources must have sent before <see cref="StopPipelineAtPercent"/> can stop the pipeline.</summary>
    public const int StopPipelineMinimum = 20;
}

/// <summary>A number of failures within a stretch of time.</summary>
public sealed record FailureLimit
{
    /// <summary>Makes the limit.</summary>
    /// <param name="failures">How many failures, at least 1.</param>
    /// <param name="within">Within how long, more than zero.</param>
    public FailureLimit(int failures, TimeSpan within)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(within, TimeSpan.Zero);
        Failures = failures;
        Within = within;
    }

    /// <summary>How many failures.</summary>
    public int Failures { get; }

    /// <summary>Within how long.</summary>
    public TimeSpan Within { get; }
}

/// <summary>How a source that throws is restarted.</summary>
public sealed record SourceRestart
{
    /// <summary>Makes the setting.</summary>
    /// <param name="delay">How long to wait before restarting the source.</param>
    /// <param name="failuresInARow">
    /// How many times the source can fail in a row before the runtime gives up on it, at least 1. A message it emits,
    /// other than an error, starts the count again.
    /// </param>
    public SourceRestart(TimeSpan delay, int failuresInARow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresInARow, 1);
        Delay = delay;
        FailuresInARow = failuresInARow;
    }

    /// <summary>How long to wait before restarting the source.</summary>
    public TimeSpan Delay { get; }

    /// <summary>How many times the source can fail in a row before the runtime gives up on it.</summary>
    public int FailuresInARow { get; }
}
