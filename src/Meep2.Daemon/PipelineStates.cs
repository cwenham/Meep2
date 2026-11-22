namespace Meep2.Daemon;

/// <summary>The states a hosted pipeline can be in.</summary>
public static class PipelineStates
{
    /// <summary>Being loaded.</summary>
    public const string Starting = "starting";

    /// <summary>Running.</summary>
    public const string Running = "running";

    /// <summary>Failed, and waiting to start again, as its error policy says.</summary>
    public const string Restarting = "restarting";

    /// <summary>Asked to stop, and letting its messages finish.</summary>
    public const string Stopping = "stopping";

    /// <summary>Ended because its sources finished.</summary>
    public const string Finished = "finished";

    /// <summary>Ended because its error policy stopped it, or it failed, with no restarts left.</summary>
    public const string Failed = "failed";

    /// <summary>Ended because someone asked it to stop.</summary>
    public const string Stopped = "stopped";
}
