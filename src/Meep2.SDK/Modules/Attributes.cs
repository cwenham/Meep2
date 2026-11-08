namespace Meep2.SDK.Modules;

/// <summary>
/// Names the Meeplang verb a module class provides, such as <c>Fetch</c>.
/// </summary>
/// <remarks>
/// The module class needs a public constructor that takes its parameters record, or none if it has no parameters.
/// Until verbs can have several inflections, each verb has one class.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class VerbAttribute(string name) : Attribute
{
    /// <summary>The verb's name in Meeplang.</summary>
    public string Name { get; } = name;
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
