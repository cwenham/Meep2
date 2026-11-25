using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

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
    /// Called with each error that nothing handled, when the error policy says to log it. By default they're written
    /// to standard error. Errors a module answered, such as a Send replying to a request, come here too, with
    /// <see cref="ErrorMessage.AnsweredBy"/> set, unless a module after it handled them (Q100).
    /// </summary>
    public Action<ErrorMessage>? UnhandledError { get; init; }

    /// <summary>
    /// Called with notices about the pipeline itself, such as a branch the error policy stopped or a source it gave
    /// up on. By default they're written to standard error.
    /// </summary>
    public Action<string>? Notice { get; init; }

    /// <summary>What happens when things go wrong. Meeplang's <c>on-error</c> default.</summary>
    public ErrorPolicy ErrorPolicy { get; init; } = ErrorPolicy.Default;

    /// <summary>
    /// How long messages from sources last before they expire, unless the source sets their expiry itself or has
    /// <see cref="NodeOptions.Expires"/>. Messages made from them inherit it. Null means they don't expire.
    /// Meeplang's <c>expires</c> default.
    /// </summary>
    public TimeSpan? Expires { get; init; }

    /// <summary>
    /// How long a host that lists its pipelines, such as the daemon, keeps listing this one after it has finished,
    /// with its final counts and why it stopped (Q111). Zero drops it at once. Meeplang's <c>keep-finished</c> default.
    /// </summary>
    public TimeSpan KeepFinished
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(KeepFinished), value, "It can't be negative.");
    } = TimeSpan.FromHours(24);

    /// <summary>
    /// The lasting ID of the Meep host running the pipeline, such as the daemon's, which its modules give other Meep
    /// hosts as part of their origin (<see cref="Meep2.SDK.Modules.ModuleContext.Origin"/>). Null for none.
    /// </summary>
    public string? HostId { get; init; }

    /// <summary>
    /// Where <c>Verify</c> gets the trust store it checks against: this machine's, kept current, in the daemon and
    /// <c>meep run</c>. Null for none, and then nothing verifies, saying why.
    /// </summary>
    public TrustStoreSource? Trust { get; init; }

    /// <summary>
    /// The pipeline's own <c>#permission</c> directives, which narrow the trust store's definitions for its gates and
    /// its <c>Verify.Permission</c>s (Q257): a narrower scope, only some of the authorities, or more requirements. One
    /// that would widen a definition makes the gates that need it refuse every message, saying why.
    /// </summary>
    public IReadOnlyList<Permission> Permissions { get; init; } = [];
}
