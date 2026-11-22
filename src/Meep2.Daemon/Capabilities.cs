namespace Meep2.Daemon;

/// <summary>
/// What a client of the daemon may do (Q40, Q63). The user running the daemon may do everything. A token can give a
/// client less, such as a dashboard that should only observe.
/// </summary>
[Flags]
public enum Capabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>List pipelines, watch their modules' counts, and tap the messages passing a module.</summary>
    Observe = 1,

    /// <summary>Send test messages into a running pipeline (Q72).</summary>
    Inject = 2,

    /// <summary>Start and stop pipelines, and stop the daemon.</summary>
    Control = 4,

    /// <summary>Everything.</summary>
    All = Observe | Inject | Control,
}
