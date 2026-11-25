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

    /// <summary>
    /// Another name the verb answers to, such as <c>Authorize</c> for <c>Authorise</c>. A pipeline may write either,
    /// and tools and errors use the one written. Give it on every inflection of the verb.
    /// </summary>
    public string? Alias { get; init; }
}

/// <summary>
/// What a verb does with messages, in a word, so tools can show it without knowing every verb's name: a drawing of a
/// pipeline gives each role its own shape, for instance. Most verbs needn't say: a source's role is
/// <see cref="Source"/>, a module that routes messages to copies of a branch (<see cref="IRouteMessages"/>) is a
/// <see cref="Router"/>, and any other's is <see cref="Step"/>. Declare another with <see cref="RoleAttribute"/>.
/// </summary>
public enum VerbRole
{
    /// <summary>
    /// Anything else: takes messages in and does something with them, such as fetching, reshaping, combining or
    /// classifying them. The role of a module that handles messages, unless it routes them or declares another.
    /// </summary>
    Step = 0,

    /// <summary>A source that makes messages itself, as Interval and Read do. The role of any source that doesn't declare <see cref="Inlet"/>.</summary>
    Source,

    /// <summary>A source whose messages come in from outside the pipeline, as Listen's do from the network. Declared.</summary>
    Inlet,

    /// <summary>Passes on some of the messages it receives, unchanged, and drops the rest, as Where does. Declared.</summary>
    Filter,

    /// <summary>Sends messages out of the pipeline, as Send, Save and WriteLine do. Declared.</summary>
    Outlet,

    /// <summary>
    /// Receives another module's messages without being indented under it, as Tap does, so a drawing can show it as
    /// a wire from that module. Declared.
    /// </summary>
    Tap,

    /// <summary>
    /// Spreads messages over copies of the branch beneath it, as Distribute does. The role of a module that
    /// implements <see cref="IRouteMessages"/>, and only of one that does.
    /// </summary>
    Router,
}

/// <summary>
/// Declares a verb's <see cref="VerbRole"/>, when what the class already says doesn't give it: <c>[Role(VerbRole.Inlet)]</c>
/// on a source that receives messages from outside, <c>[Role(VerbRole.Outlet)]</c> on a module that sends messages out
/// of the pipeline, or <c>[Role(VerbRole.Filter)]</c> on a module that passes on only some of its messages. Put it beside <see cref="VerbAttribute"/>, on every inflection of the verb, since a verb's
/// inflections must agree on their role.
/// </summary>
/// <remarks>
/// <see cref="VerbRole.Source"/> and <see cref="VerbRole.Inlet"/> are for sources only, and the other roles for
/// modules that handle messages. <see cref="VerbRole.Filter"/> also needs <see cref="EmitsInputAttribute"/>, since
/// a filter passes on what it receives, and <see cref="VerbRole.Router"/> needs <see cref="IRouteMessages"/>.
/// </remarks>
/// <param name="role">The verb's role.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RoleAttribute(VerbRole role) : Attribute
{
    /// <summary>The verb's role.</summary>
    public VerbRole Role { get; } = role;
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
/// Declares that an inflection's <see cref="IChosenByParameters{TParameters}.WhyNot"/> looks at each message, not only
/// at the parameters, so it's asked for every message rather than once when the pipeline loads. Verify uses it: its
/// inflections take a credential, a certificate or a permission, whether the message is one or <c>from=</c> reads one
/// from it, so neither the message's type nor the parameters alone can choose.
/// </summary>
/// <remarks>
/// The inflections of such a verb usually all take <see cref="Messages.Message"/>, and between them take every
/// message, so that one of them always accepts it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ChosenForEachMessageAttribute : Attribute;

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
