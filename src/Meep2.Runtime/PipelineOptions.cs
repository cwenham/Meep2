using Meep2.SDK.Messages;

namespace Meep2.Runtime;

/// <summary>Settings for a whole pipeline.</summary>
public sealed record PipelineOptions
{
    /// <summary>The system defaults.</summary>
    public static PipelineOptions Default { get; } = new();

    /// <summary>
    /// How many messages can wait for each module before the oldest is dropped, unless a module sets its own.
    /// Provisional until benchmarks suggest a better default.
    /// </summary>
    public int BufferSize { get; init; } = 1024;

    /// <summary>
    /// After the pipeline is asked to stop, how long messages already in it get to finish. After that, modules are
    /// cancelled and anything still waiting is abandoned. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Called with each error that leaves the pipeline unhandled. By default they're written to standard error.
    /// The error policy (log, stop a branch, stop the pipeline) will build on this.
    /// </summary>
    public Action<ErrorMessage>? UnhandledError { get; init; }
}
