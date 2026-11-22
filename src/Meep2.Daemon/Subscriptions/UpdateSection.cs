namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// The sections a subscription asks for and an update carries, each named in lower case in plans and updates
/// (<see cref="SubscriptionPlan.NameOf"/>): the host's, and for each pipeline its totals and, for each module or link
/// at a rate of its own, the rest.
/// </summary>
internal enum UpdateSection
{
    /// <summary>The pipelines and their states.</summary>
    List,

    /// <summary>Totals for the host, and where its Listens and Sends are.</summary>
    Host,

    /// <summary>CPU, memory and garbage collection, from .NET's runtime metrics.</summary>
    Runtime,

    /// <summary>A pipeline's totals.</summary>
    Totals,

    /// <summary>Each module's counts.</summary>
    Modules,

    /// <summary>Each link's counts.</summary>
    Links,

    /// <summary>Each module's handling-time histogram.</summary>
    Timing,

    /// <summary>Bytes in and out, for modules that talk to the network.</summary>
    Bytes,

    /// <summary>A module's counts by where its messages come from.</summary>
    Arrivals,

    /// <summary>A module's counts by where its messages go.</summary>
    Destinations,
}
