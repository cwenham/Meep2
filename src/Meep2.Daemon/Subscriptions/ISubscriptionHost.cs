namespace Meep2.Daemon.Subscriptions;

/// <summary>What a subscription reads from the daemon.</summary>
internal interface ISubscriptionHost
{
    HostInfo Identity { get; }

    /// <summary>The pipelines listed now.</summary>
    IReadOnlyCollection<HostedPipeline> Listed { get; }

    RuntimeMetrics Runtime { get; }

    /// <summary>Completes at the next change worth telling straight away: a pipeline's graph changing, or the list.</summary>
    Task Changed { get; }
}
