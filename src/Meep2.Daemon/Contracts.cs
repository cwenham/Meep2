using System.Text.Json.Serialization;
using Meep2.Runtime;

namespace Meep2.Daemon;

/// <summary>What the daemon says about itself, and what the asking client may do.</summary>
public sealed record DaemonInfo(string Version, int ProcessId, string Socket, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Pipelines)
{
    /// <summary>The daemon's lasting ID, made the first time it ran and kept in <c>daemon/id</c> in Meep's folder.</summary>
    public string? Id { get; init; }

    /// <summary>The machine's names: its own name, its host name, and that name in full as DNS knows it.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>The machine's addresses on the networks it's connected to, without loopback and link-local ones.</summary>
    public IReadOnlyList<string>? Addresses { get; init; }

    /// <summary>Where its pipelines' Listens are bound now.</summary>
    public IReadOnlyList<ListenInfo>? Listening { get; init; }

    /// <summary>Where its pipelines' Fetches and Sends send, as their addresses say, for matching them with Listens before anything runs.</summary>
    public IReadOnlyList<SendInfo>? Sending { get; init; }

    /// <summary>The daemon's clock as it answered, in microseconds since 1970 (UTC), for a client to estimate the offset between its clock and the daemon's.</summary>
    public long Clock { get; init; }
}

/// <summary>Where a Listen is bound.</summary>
/// <param name="Pipeline">Its pipeline.</param>
/// <param name="Module">Its key in the pipeline's graph.</param>
/// <param name="Label">How it's shown: its name, or its verb and number.</param>
/// <param name="Address">Where it's bound, as in <c>http://0.0.0.0:8080/hooks/</c> or <c>tcp://127.0.0.1:9000</c>.</param>
public sealed record ListenInfo(string Pipeline, string Module, string Label, string Address);

/// <summary>Where a Fetch or Send sends, as its address says (prompt 7's declared destinations).</summary>
/// <param name="Pipeline">Its pipeline.</param>
/// <param name="Module">Its key in the pipeline's graph.</param>
/// <param name="Label">How it's shown: its name, or its verb and number.</param>
/// <param name="Address">
/// The address, as in <c>http://studio.local:8080/hooks/</c>; for one worked out for each message, its expression as
/// written, as in <c>"https://api.example.com/stops/{msg.StopId}"</c>.
/// </param>
public sealed record SendInfo(string Pipeline, string Module, string Label, string Address)
{
    /// <summary>Whether the address is worked out for each message.</summary>
    public bool Computed { get; init; }

    /// <summary>
    /// The host and port it reaches, as destinations are counted (<c>api.example.com:443</c>), when the address fixes
    /// them, even if the rest is worked out for each message; null when the host or port is worked out too.
    /// </summary>
    public string? Endpoint { get; init; }
}

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

/// <summary>
/// A pipeline as a graph, from <c>GET /pipelines/{name}/graph</c>: its modules, where each is written, and the links
/// between them.
/// </summary>
/// <param name="Name">The pipeline's name.</param>
/// <param name="Path">The file it came from.</param>
/// <param name="Version">
/// Counts up from 1 each time the graph changes, when the pipeline is reloaded or edited: anything in it, its spans
/// included, so a comment added above a module changes it too. The same version, while the daemon runs the
/// pipeline, means the same graph. It starts at 1 again when the pipeline is started again.
/// </param>
/// <param name="Modules">Its modules, in the order the pipeline numbers them.</param>
/// <param name="Links">The links between them, grouped by the module they go to, in the order of its inputs.</param>
public sealed record PipelineGraph(string Name, string Path, int Version, IReadOnlyList<GraphModule> Modules, IReadOnlyList<GraphLink> Links);

/// <summary>One module of a pipeline's graph.</summary>
/// <param name="Key">
/// A name for the module that lasts while the daemon runs the pipeline, though its number may change. A reload or
/// an edit matches the new text's modules with the old (by name, then by place and parameters), and a module matched
/// with one keeps its key, and its state; a new module gets a key the pipeline hasn't used before, and a removed
/// module's key isn't used again. A restart keeps them all. The first keys are m1, m2 and so on, by number, so a
/// pipeline started again from the same file has the same keys.
/// </param>
/// <param name="Number">Its position in the pipeline, counting from 1, which the rest of the API takes.</param>
/// <param name="Module">How it's shown: its name, or its verb and number.</param>
/// <param name="Verb">Its verb.</param>
/// <param name="Role">What its verb does with messages: source, inlet, filter, outlet or step.</param>
/// <param name="Inflections">The inflections it can resolve to, in the order the verb tries them.</param>
/// <param name="Parameters">Its parameters as written, the value after the verb first.</param>
/// <param name="Modifiers">Its <c>@</c> modifiers as written.</param>
/// <param name="Span">
/// Where it's written: from its <c>+</c> or verb to the end of its last parameter line, without the modules under it.
/// For a module a template made, that's the line in the template; for a copy Distribute made, its original's line.
/// </param>
public sealed record GraphModule(
    string Key,
    int Number,
    string Module,
    string Verb,
    string Role,
    IReadOnlyList<string> Inflections,
    IReadOnlyList<GraphParameter> Parameters,
    IReadOnlyList<GraphModifier> Modifiers,
    GraphSpan Span)
{
    /// <summary>The name it was given with <c>as</c>, if any. A template use's name is given to its first module.</summary>
    public string? Name { get; init; }

    /// <summary>The plugin its verb comes from, by the name the pipeline gives it, or null for a verb that came with Meep.</summary>
    public string? Plugin { get; init; }

    /// <summary>The key of the module it's indented under, or null at the left margin.</summary>
    public string? Under { get; init; }

    /// <summary>Whether its line starts with <c>+</c>, so it feeds the next unmarked line at the same indentation.</summary>
    public bool Feeder { get; init; }

    /// <summary>The key of the module it's a copy of, if Distribute copied its branch.</summary>
    public string? CopyOf { get; init; }

    /// <summary>
    /// The template uses that made it, if a template did: the use of the template it's written in first, then the
    /// use that one is in, out to a line of the pipeline's own.
    /// </summary>
    public IReadOnlyList<GraphTemplateUse>? Templates { get; init; }

    /// <summary>The <c>#include</c> line that brought in the file it's written in, if that isn't the pipeline's own file.</summary>
    public GraphInclude? Include { get; init; }
}

/// <summary>One of a module's parameters, as written.</summary>
/// <param name="Name">Its name, or null for the value written straight after the verb.</param>
/// <param name="Text">Its value as written, such as <c>msg.minutesAway &lt; 30</c> or <c>"{msg.Payload}"</c>, quotes and all.</param>
/// <param name="Span">Where it's written: <c>name=value</c> whole, or the value alone for the one after the verb.</param>
public sealed record GraphParameter(string? Name, string Text, GraphSpan Span)
{
    /// <summary>
    /// For a module a template made, its value with the template's parameters put in, if that differs from
    /// <see cref="Text"/>, as in <c>"12"</c> where the template says <c>Stop</c>.
    /// </summary>
    public string? Value { get; init; }
}

/// <summary>One of a module's <c>@</c> modifiers, as written.</summary>
/// <param name="Name">Its name, without the <c>@</c>, such as <c>errors</c> or <c>buffer</c>.</param>
/// <param name="Value">Its value as written, if it has one.</param>
/// <param name="Span">Where it's written, from the <c>@</c> to the end of its value.</param>
public sealed record GraphModifier(string Name, string? Value, GraphSpan Span);

/// <summary>A line that uses a template.</summary>
/// <param name="Template">The template's name.</param>
/// <param name="Span">Where the line is written, as for a module.</param>
public sealed record GraphTemplateUse(string Template, GraphSpan Span);

/// <summary>An <c>#include</c> line.</summary>
/// <param name="Path">The file as the line names it.</param>
/// <param name="Span">Where the line is written.</param>
public sealed record GraphInclude(string Path, GraphSpan Span);

/// <summary>
/// A link between two modules: everything one emits goes to the other. Errors go along every link, unless the receiving
/// module has <c>@no-errors</c>, and only errors go to a module with <c>@errors</c>.
/// </summary>
/// <param name="Key">
/// The link's own key, <c>from&gt;to</c>, from the modules' keys, so it lasts as they do. The stats count what each
/// link carries by this key (<see cref="PipelineDetail.Links"/>).
/// </param>
/// <param name="From">The key of the module it comes from.</param>
/// <param name="To">The key of the module it goes to.</param>
/// <param name="Input">Which of the receiving module's inputs it is, counting from 1, as Zip numbers them.</param>
/// <param name="Port">
/// <c>main</c> for a link that carries messages and errors, <c>errors</c> for one that carries only errors, to a module
/// with <c>@errors</c> (an <c>@errors</c> module indented under another, or a <c>Tap</c> with <c>@errors</c>), or
/// <c>no-errors</c> for one that carries no errors, to a module with <c>@no-errors</c>.
/// </param>
/// <param name="Types">The message types it can carry, as far as the loader can tell, by the names <c>is</c> takes.</param>
public sealed record GraphLink(string Key, string From, string To, int Input, string Port, IReadOnlyList<string> Types)
{
    /// <summary>Whether it can also carry types the loader can't name, because a module before it doesn't declare what it emits.</summary>
    public bool Undeclared { get; init; }
}

/// <summary>
/// Where something is written. Lines and columns count from 1, and columns in UTF-16 code units, with a tab as one.
/// The end is just after the last character.
/// </summary>
/// <param name="File">The file's full path, or the pipeline's address if it didn't come from a file.</param>
public sealed record GraphSpan(string File, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>
/// A <c>graph</c> event in a pipeline's stats stream: its graph has a new version, so a client that draws it should ask
/// for the graph again if <see cref="Version"/> isn't the one it has. A restart that leaves the graph as it was sends
/// none: it shows as <see cref="PipelineDetail.Restarts"/> counting up, with the counts starting again.
/// </summary>
/// <param name="Version">The graph's version now.</param>
/// <param name="Reason">What changed it: <c>reload</c>, <c>edit</c> or <c>restart</c>.</param>
public sealed record GraphNotice(int Version, string Reason);

/// <summary>Why a request failed.</summary>
/// <param name="Error">What went wrong.</param>
/// <param name="Details">More, such as a pipeline file's errors, line by line.</param>
public sealed record Problem(string Error, IReadOnlyList<string>? Details = null);

/// <summary>A message the daemon sent into a pipeline.</summary>
public sealed record Injected(Guid Id, string Type, string Module);

[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(ListenInfo))]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(PipelineSummary))]
[JsonSerializable(typeof(IReadOnlyList<PipelineSummary>))]
[JsonSerializable(typeof(PipelineDetail))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(Injected))]
[JsonSerializable(typeof(EditRequest))]
[JsonSerializable(typeof(ChangeResult))]
[JsonSerializable(typeof(SourceText))]
[JsonSerializable(typeof(PipelineGraph))]
[JsonSerializable(typeof(GraphNotice))]
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
