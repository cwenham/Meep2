namespace Meep2.Daemon;

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
    /// The permissions a message needs to reach it, so a drawing can put a lock on it: its <c>@permission</c>s, then
    /// what its verb always requires. For a source, what the pipeline must hold for it to start. Null if it isn't gated.
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }

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
