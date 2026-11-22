using Meep2.Runtime;

namespace Meep2.Daemon;

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

/// <summary>A pipeline the daemon runs, or ran recently.</summary>
/// <param name="Name">Its name: the file's name without <c>.meep</c>.</param>
/// <param name="Path">The file it came from.</param>
/// <param name="State">One of <see cref="PipelineStates"/>: starting, running, restarting, stopping, finished, failed or stopped.</param>
/// <param name="Started">When the daemon started it.</param>
/// <param name="Warnings">Warnings from loading it.</param>
public sealed record PipelineSummary(string Name, string Path, string State, DateTimeOffset Started, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The version of its graph (<see cref="PipelineGraph.Version"/>), so a client can tell from the list when to ask
    /// for the graph again. Null until it has loaded.
    /// </summary>
    public int? GraphVersion { get; init; }

    /// <summary>When it ended, if it has.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Why it ended, or why it's restarting.</summary>
    public string? Reason { get; init; }

    /// <summary>How many times it has been restarted.</summary>
    public int Restarts { get; init; }

    /// <summary>
    /// Whether the daemon is watching its file, and the files it includes, so saving a change to one reloads it
    /// without asking (started with <c>watch</c>). False once it has ended.
    /// </summary>
    public bool Watching { get; init; }
}

/// <summary>A pipeline the daemon runs, or ran recently, with its modules and their counts, final ones if it has ended.</summary>
public sealed record PipelineDetail(string Name, string Path, string State, DateTimeOffset Started, IReadOnlyList<ModuleDetail> Modules)
{
    /// <summary>
    /// The version of its graph (<see cref="PipelineGraph.Version"/>), so a client that draws it can tell from its
    /// counts when to ask for the graph again. Null until it has loaded.
    /// </summary>
    public int? GraphVersion { get; init; }

    /// <summary>
    /// How many messages, and how many errors, each link of its graph has carried since it started, keyed as the graph's links are
    /// (<see cref="GraphLink.Key"/>), so a client can draw what goes along each wire. Grouped by the module each goes
    /// to, in the order of its inputs. Null until it has loaded.
    /// </summary>
    public IReadOnlyList<LinkDetail>? Links { get; init; }

    /// <summary>The count for one of its links, by the link's key, or null if it has no such link.</summary>
    public long? Carried(string link) => Links?.FirstOrDefault(l => l.Key == link)?.Carried;

    /// <summary>When it ended, if it has.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Why it ended, or why it's restarting.</summary>
    public string? Reason { get; init; }

    /// <summary>How many times it has been restarted.</summary>
    public int Restarts { get; init; }

    /// <summary>
    /// Whether the daemon is watching its file, and the files it includes, so saving a change to one reloads it
    /// without asking (started with <c>watch</c>). False once it has ended.
    /// </summary>
    public bool Watching { get; init; }
}

/// <summary>One module of a running pipeline.</summary>
/// <param name="Number">Its position in the pipeline, counting from 1.</param>
/// <param name="Module">How it's shown: its name, or its verb and number.</param>
/// <param name="Verb">Its verb.</param>
/// <param name="Name">The name it was given with <c>as</c>, if any.</param>
/// <param name="Counts">Its counts since the pipeline started.</param>
public sealed record ModuleDetail(int Number, string Module, string Verb, string? Name, ModuleStats Counts);

/// <summary>One link of a running pipeline, with its counts.</summary>
/// <param name="Key">The link's key, <c>from&gt;to</c>, as the graph has it (<see cref="GraphLink.Key"/>).</param>
/// <param name="Carried">
/// Messages that have gone along it since the pipeline started, errors included: each one the module at its end took
/// in or stopped (dropped, expired or refused it), or let pass by to the modules under it. An error that passes a
/// module with <c>@no-errors</c> is counted on the link into it, though its port is <c>no-errors</c>, so a particle
/// can be drawn arriving at the module and leaving it. Other messages that stop at a module with <c>@errors</c>
/// aren't counted, so a link on the <c>errors</c> port counts only errors. It counts up, like the modules' counts,
/// so a client takes differences, and missing an update loses nothing. A reload or edit that keeps both modules
/// keeps the counts; a new link starts from zero, and so does every link when the pipeline restarts. Where a module
/// filters, as Where does, the links after it count what it passed on, which is its <c>passed</c>.
/// </param>
/// <param name="Errors">How many of <paramref name="Carried"/> were errors, so a client can draw them apart.</param>
public sealed record LinkDetail(string Key, long Carried, long Errors);
