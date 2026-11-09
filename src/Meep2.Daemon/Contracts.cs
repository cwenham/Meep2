using System.Text.Json.Serialization;
using Meep2.Runtime;

namespace Meep2.Daemon;

/// <summary>What the daemon says about itself, and what the asking client may do.</summary>
public sealed record DaemonInfo(string Version, int ProcessId, string Socket, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Pipelines);

/// <summary>Asks the daemon to start a pipeline file.</summary>
/// <param name="Path">The file's full path, as the daemon sees it.</param>
/// <param name="DeveloperMode">Loads unsigned plugins from this machine, as <c>meep run --dev</c> does.</param>
public sealed record StartRequest(string Path, bool DeveloperMode = false);

/// <summary>A pipeline the daemon runs, or ran recently.</summary>
/// <param name="Name">Its name: the file's name without <c>.meep</c>.</param>
/// <param name="Path">The file it came from.</param>
/// <param name="State">One of <see cref="PipelineStates"/>: starting, running, restarting, stopping, finished, failed or stopped.</param>
/// <param name="Started">When the daemon started it.</param>
/// <param name="Warnings">Warnings from loading it.</param>
public sealed record PipelineSummary(string Name, string Path, string State, DateTimeOffset Started, IReadOnlyList<string> Warnings)
{
    /// <summary>When it ended, if it has.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Why it ended, or why it's restarting.</summary>
    public string? Reason { get; init; }

    /// <summary>How many times it has been restarted.</summary>
    public int Restarts { get; init; }
}

/// <summary>A pipeline the daemon runs, or ran recently, with its modules and their counts, final ones if it has ended.</summary>
public sealed record PipelineDetail(string Name, string Path, string State, DateTimeOffset Started, IReadOnlyList<ModuleDetail> Modules)
{
    /// <summary>When it ended, if it has.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Why it ended, or why it's restarting.</summary>
    public string? Reason { get; init; }

    /// <summary>How many times it has been restarted.</summary>
    public int Restarts { get; init; }
}

/// <summary>One module of a running pipeline.</summary>
/// <param name="Number">Its position in the pipeline, counting from 1.</param>
/// <param name="Module">How it's shown: its name, or its verb and number.</param>
/// <param name="Verb">Its verb.</param>
/// <param name="Name">The name it was given with <c>as</c>, if any.</param>
/// <param name="Counts">Its counts since the pipeline started.</param>
public sealed record ModuleDetail(int Number, string Module, string Verb, string? Name, ModuleStats Counts);

/// <summary>Why a request failed.</summary>
/// <param name="Error">What went wrong.</param>
/// <param name="Details">More, such as a pipeline file's errors, line by line.</param>
public sealed record Problem(string Error, IReadOnlyList<string>? Details = null);

/// <summary>A message the daemon sent into a pipeline.</summary>
public sealed record Injected(Guid Id, string Type, string Module);

[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(PipelineSummary))]
[JsonSerializable(typeof(IReadOnlyList<PipelineSummary>))]
[JsonSerializable(typeof(PipelineDetail))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(Injected))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DaemonJson : JsonSerializerContext;

/// <summary>A request the daemon refused, or a call to it that failed. The message says why, for people.</summary>
public sealed class DaemonException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DaemonException(int status, string message, IReadOnlyList<string>? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Details = details ?? [];
    }

    /// <summary>The HTTP status the daemon answered with, or 0 if it couldn't be reached.</summary>
    public int Status { get; }

    /// <summary>More, such as a pipeline file's errors.</summary>
    public IReadOnlyList<string> Details { get; }
}
