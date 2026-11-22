using Meep2.Runtime;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// The parts of an error policy a file sets, such as <c>log, stop-branch after 5 in 1s</c>. Parts it leaves out
/// come from the level above: a branch's from the module above it, the pipeline's from the system defaults.
/// </summary>
internal sealed class ErrorPolicyParts
{
    public bool? Log { get; set; }

    public bool SetsStopBranch { get; set; }

    public FailureLimit? StopBranch { get; set; }

    public bool SetsStopPipeline { get; set; }

    public double? StopPipelineAtPercent { get; set; }

    public bool SetsRestart { get; set; }

    public SourceRestart? Restart { get; set; }

    public bool SetsRestartPipeline { get; set; }

    public PipelineRestart? RestartPipeline { get; set; }

    public ErrorPolicy ApplyTo(ErrorPolicy basis) => basis with
    {
        LogUnhandled = Log ?? basis.LogUnhandled,
        StopBranch = SetsStopBranch ? StopBranch : basis.StopBranch,
        StopPipelineAtPercent = SetsStopPipeline ? StopPipelineAtPercent : basis.StopPipelineAtPercent,
        RestartSources = SetsRestart ? Restart : basis.RestartSources,
        RestartPipeline = SetsRestartPipeline ? RestartPipeline : basis.RestartPipeline,
    };
}
