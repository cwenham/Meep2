namespace Meep2.Meeplang.Loading;

/// <summary>One inflection of a verb, as <see cref="VerbCatalogue"/> describes it.</summary>
/// <param name="Verb">The verb, such as <c>Fetch</c>.</param>
/// <param name="Inflection">The inflection, such as <c>HTTP</c>, or the class's name for a verb with one.</param>
/// <param name="Role">What the verb does with messages, as the graph gives it: <c>source</c>, <c>inlet</c>, <c>filter</c>, <c>outlet</c>, <c>tap</c>, <c>router</c> or <c>step</c>.</param>
/// <param name="Kind">How it handles messages: <c>source</c>, <c>passthrough</c> (passes on what it takes, off the thread that delivered it) or <c>inflection</c>.</param>
/// <param name="Parameters">Its parameters, the one written straight after the verb first.</param>
/// <param name="Takes">The message types it takes, by the names <c>is</c> takes; empty for a source.</param>
/// <param name="Emits">The message types it declares it emits, besides errors, which any module can emit.</param>
public sealed record VerbEntry(
    string Verb,
    string Inflection,
    string Role,
    string Kind,
    IReadOnlyList<ParameterEntry> Parameters,
    IReadOnlyList<string> Takes,
    IReadOnlyList<EmittedType> Emits)
{
    /// <summary>The plugin it comes from, by the name the pipeline gives it, or null for one that came with Meep.</summary>
    public string? Plugin { get; init; }

    /// <summary>Whether the role was declared with <c>[Role]</c>, rather than worked out from the class.</summary>
    public bool RoleDeclared { get; init; }

    /// <summary>Whether it passes on the messages it takes, so it emits whatever it's given (<c>[EmitsInput]</c>).</summary>
    public bool EmitsInput { get; init; }

    /// <summary>Whether it can take new parameters while it runs, rather than being replaced.</summary>
    public bool Reconfigurable { get; init; }

    /// <summary>What it does, from its class's documentation, if that was shipped beside it.</summary>
    public string? Description { get; init; }

    /// <summary>The class that makes it, with its namespace.</summary>
    public string? Class { get; init; }

    /// <summary>Where the class is written, from its assembly's debugging symbols, if they were shipped beside it.</summary>
    public SourceLocation? Source { get; init; }
}

/// <summary>One of an inflection's parameters.</summary>
/// <param name="Name">Its name as written in Meeplang, in kebab case, such as <c>max-size</c>.</param>
/// <param name="Type">
/// What it takes, in Meeplang's words: <c>text</c>, <c>number</c>, <c>duration</c>, <c>bool</c>, <c>time</c>,
/// <c>book</c>, <c>module</c>, <c>choice</c>, <c>path</c>, <c>bytes</c>, <c>any</c>, or
/// <c>list of …</c> and <c>map of …</c>.
/// </param>
/// <param name="Positional">Whether it's written straight after the verb, without its name.</param>
/// <param name="Required">Whether every use must give it.</param>
public sealed record ParameterEntry(string Name, string Type, bool Positional, bool Required)
{
    /// <summary>Whether it can be an expression worked out for each message, such as <c>"{msg.Url}"</c>.</summary>
    public bool PerMessage { get; init; }

    /// <summary>Its value when a use doesn't give it, as Meeplang would write it, if it has one.</summary>
    public string? Default { get; init; }

    /// <summary>For a choice, the words it takes.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>What it's for, from its documentation, if that was shipped beside the class.</summary>
    public string? Description { get; init; }
}

/// <summary>A message type an inflection declares it emits.</summary>
/// <param name="Type">Its name, as <c>is</c> takes it.</param>
/// <param name="When">The <c>bool</c> parameter that turns it on, if it's only emitted when one is.</param>
public sealed record EmittedType(string Type, string? When = null);

/// <summary>Where a class is written.</summary>
/// <param name="File">The file, as it was when the assembly was built: a path on the machine that built it.</param>
/// <param name="Line">The line its declaration starts on, counting from 1, or the first line of its first method's code if the file can't be read here.</param>
/// <param name="Url">Where the file can be read, from the assembly's SourceLink, if it has one, such as a raw GitHub address.</param>
public sealed record SourceLocation(string File, int Line, string? Url = null);
