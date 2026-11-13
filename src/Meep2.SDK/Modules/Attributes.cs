namespace Meep2.SDK.Modules;

/// <summary>
/// Names the Meeplang verb a module class is an inflection of, such as <c>Fetch</c>.
/// </summary>
/// <remarks>
/// <para>The module class needs a public constructor that takes its parameters record, or none if it has no parameters.</para>
/// <para>
/// A verb can have several inflections, each a class of its own: <c>Fetch</c> has <c>HTTP</c> and <c>File</c>.
/// The runtime gives each message to the inflection with the most specific input type, and parameters can choose
/// between inflections too (see <see cref="IChosenByParameters{TParameters}"/>). In Meeplang, <c>Fetch.HTTP</c>
/// names one, as does the inflection's name alone when no other verb has an inflection of that name.
/// </para>
/// <para>A verb's inflections must agree on whether they're sources, and on whether they're passthrough modules.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class VerbAttribute(string name) : Attribute
{
    /// <summary>The verb's name in Meeplang.</summary>
    public string Name { get; } = name;

    /// <summary>The inflection's name, such as <c>HTTP</c>. Defaults to the class name.</summary>
    public string? Inflection { get; init; }
}

/// <summary>
/// Marks the parameter written straight after the verb, without a name: the URL in <c>Fetch "https://…"</c>.
/// A parameters record can have at most one.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PositionalAttribute : Attribute;

/// <summary>
/// Declares a message type a module emits, so the loader can check expressions against it.
/// Errors don't need declaring: any module can emit them.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EmitsAttribute(Type messageType) : Attribute
{
    /// <summary>The message type.</summary>
    public Type MessageType { get; } = messageType;

    /// <summary>
    /// The name of a <c>bool</c> parameter that turns these messages on, as <c>connections=true</c> does for
    /// Listen.TCP's connection messages. Without it, the module may always emit them.
    /// </summary>
    public string? When { get; init; }
}

/// <summary>
/// Marks a parameter worked out only for errors, such as <c>error-reply</c> on a Send that replies. When a parameters record has
/// one, its other parameters worked out per message are only for messages that aren't errors. So an expression
/// written for one kind doesn't keep the other kind out of the module, as it otherwise would (Q69).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ForErrorsAttribute : Attribute;

/// <summary>
/// Declares that a module passes on messages it receives, as a filter does, so its output has the same types as
/// its input.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EmitsInputAttribute : Attribute;

/// <summary>
/// A parameter that names another module in the pipeline, as <c>Tap Schedule</c> does. The loader connects the
/// named module to this one, so everything it emits arrives here.
/// </summary>
/// <param name="Name">The module's name.</param>
public sealed record ModuleReference(string Name);

/// <summary>
/// Adds a book to the library that Enumerate reads from, as in <c>Enumerate "Countries"</c>. Put it on a public static
/// property or field whose type is an <see cref="IReadOnlyList{T}"/> of strings, one item to each entry.
/// </summary>
/// <remarks>
/// A plugin's books join the others, and can also be reached by the plugin's name, as in <c>"Transit.Stops"</c>. The
/// list is read when a pipeline names the book, not when the plugin loads, so a large book can load itself lazily.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class LibraryBookAttribute(string name) : Attribute
{
    /// <summary>The book's name, such as <c>Countries</c>.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// A parameter that names a book from the library (<see cref="LibraryBookAttribute"/>), as Enumerate's does. The
/// loader finds the book when the pipeline loads, so a misspelt name is reported then.
/// </summary>
/// <param name="Name">The book's name as the library spells it, which a pipeline can write in any case.</param>
/// <param name="Items">The book's items, never empty.</param>
public sealed record Book(string Name, IReadOnlyList<string> Items);
