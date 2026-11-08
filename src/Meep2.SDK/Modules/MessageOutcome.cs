namespace Meep2.SDK.Modules;

/// <summary>How a tracked message and the messages made from it fared.</summary>
[Flags]
public enum MessageOutcome
{
    /// <summary>Everything left the pipeline normally.</summary>
    Completed = 0,

    /// <summary>At least one was dropped from a full buffer.</summary>
    Dropped = 1,

    /// <summary>At least one error was left unhandled.</summary>
    UnhandledError = 2,

    /// <summary>The pipeline stopped before they were finished.</summary>
    Abandoned = 4,
}
