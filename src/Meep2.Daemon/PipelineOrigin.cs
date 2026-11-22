namespace Meep2.Daemon;

/// <summary>Where a pipeline comes from (goal 7).</summary>
public enum PipelineOrigin
{
    /// <summary>A file on this machine.</summary>
    File,

    /// <summary>Standard input: <c>meep run -</c>.</summary>
    Stdin,

    /// <summary>An https:// address, or http:// for one on this machine.</summary>
    Url,

    /// <summary>A file in a Git repository: <c>git+https://host/owner/repo.git#path/file.meep@ref</c>.</summary>
    Git,

    /// <summary>Text sent to the daemon over its socket.</summary>
    Text,
}
