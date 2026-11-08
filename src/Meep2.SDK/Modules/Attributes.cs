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
}

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
