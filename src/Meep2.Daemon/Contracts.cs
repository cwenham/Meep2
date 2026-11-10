using System.Text.Json.Serialization;
using Meep2.Runtime;

namespace Meep2.Daemon;

/// <summary>What the daemon says about itself, and what the asking client may do.</summary>
public sealed record DaemonInfo(string Version, int ProcessId, string Socket, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Pipelines);

/// <summary>Asks the daemon to start a pipeline.</summary>
/// <param name="Path">
/// Its address: a file's full path as the daemon sees it, an https:// URL, or <c>git+https://…#file.meep@ref</c>
/// (goal 7). Ignored when <see cref="Text"/> is given.
/// </param>
/// <param name="DeveloperMode">Loads unsigned plugins from this machine, as <c>meep run --dev</c> does.</param>
/// <param name="Watch">Reloads the pipeline whenever the file changes (goal 4).</param>
public sealed record StartRequest(string Path, bool DeveloperMode = false, bool Watch = false)
{
    /// <summary>The pipeline's text itself, sent over the socket rather than read from an address, as <c>meep start -</c> sends standard input.</summary>
    public string? Text { get; init; }

    /// <summary>The name for a pipeline sent as <see cref="Text"/>; it has no file to be named after.</summary>
    public string? Name { get; init; }
}

/// <summary>One edit to a running pipeline's text, as the API takes it (goal 4).</summary>
/// <param name="Module">The module: its name, its number, or a verb only one module has. Empty for edits to the defaults block, constants and plugins.</param>
/// <param name="Kind">
/// set, unset, set-modifier, remove-modifier, rename, add, insert, remove or remove-branch for a module; set-default,
/// unset-default, set-const, unset-const, add-plugin or remove-plugin for the rest of the file (Q125).
/// </param>
/// <param name="Name">The parameter or modifier, for those edits.</param>
/// <param name="Value">The value as Meeplang text, the new name, or the new module's line.</param>
public sealed record EditItem(string? Module, string Kind, string? Name = null, string? Value = null);

/// <summary>Edits to make to a running pipeline, and where to commit its new text.</summary>
/// <param name="Edits">The edits, made one after another.</param>
/// <param name="Message">The commit message, or null for one that lists the edits.</param>
/// <param name="Branch">The branch to commit to, <c>pending</c> unless given (Q32).</param>
/// <param name="Save">Whether to commit the new text at all.</param>
public sealed record EditRequest(IReadOnlyList<EditItem> Edits, string? Message = null, string? Branch = null, bool Save = true);

/// <summary>A commit that write-back made.</summary>
public sealed record SavedCommit(string Branch, string Sha, string Repository, string Path)
{
    /// <summary>Why it isn't signed with your SSH key, if it isn't (Q126).</summary>
    public string? Unsigned { get; init; }
}

/// <summary>What a reload or an edit did to a running pipeline.</summary>
/// <param name="Applied">Whether the pipeline changed into the new text. If not, the diagnostics say why, and it runs as it was.</param>
/// <param name="Added">Modules added.</param>
/// <param name="Removed">Modules taken out.</param>
/// <param name="Reconfigured">Modules that took new parameters and kept their state.</param>
/// <param name="Replaced">Modules made again with new instances.</param>
/// <param name="Unchanged">Modules that carried on as they were.</param>
/// <param name="Diagnostics">Problems with the new text, and warnings.</param>
public sealed record ChangeResult(
    bool Applied,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Reconfigured,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The commit write-back made, if it made one.</summary>
    public SavedCommit? Saved { get; init; }

    /// <summary>Why the new text wasn't committed, if it wasn't.</summary>
    public string? NotSaved { get; init; }

    /// <summary>The file the new text was written to, for a file that isn't in a Git repository (Q124).</summary>
    public string? WrittenTo { get; init; }

    /// <summary>The pipeline's text now, after an edit.</summary>
    public string? Text { get; init; }

    /// <summary>Whether the pipeline's own settings, under <c>defaults</c>, changed.</summary>
    public bool SettingsChanged { get; init; }
}

/// <summary>The text a running pipeline runs from.</summary>
/// <param name="Path">The file it came from.</param>
/// <param name="Text">Its text now: the file as loaded or last reloaded, with any edits made through the API since.</param>
/// <param name="Edited">Whether edits made through the API make it differ from the file as loaded.</param>
public sealed record SourceText(string Path, string Text, bool Edited);

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
[JsonSerializable(typeof(EditRequest))]
[JsonSerializable(typeof(ChangeResult))]
[JsonSerializable(typeof(SourceText))]
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
