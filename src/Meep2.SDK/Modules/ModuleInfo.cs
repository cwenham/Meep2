namespace Meep2.SDK.Modules;

/// <summary>
/// A module's place in a pipeline: its number, its verb, and the name it was given, if any.
/// Every message records the module that emitted it, which is how <c>fam</c> finds ancestors.
/// </summary>
public sealed class ModuleInfo
{
    internal ModuleInfo(int number, string verb, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        Number = number;
        Verb = verb;
        Name = name;
    }

    /// <summary>The module's position in its pipeline, counting from 1.</summary>
    public int Number { get; }

    /// <summary>The verb the module is an instance of, such as <c>Fetch</c>.</summary>
    public string Verb { get; }

    /// <summary>The name given with <c>as Name</c>, or null.</summary>
    public string? Name { get; }

    /// <summary>
    /// Whether <paramref name="name"/> refers to this module: its own name if it has one, otherwise its verb.
    /// Names are case-sensitive.
    /// </summary>
    public bool IsCalled(string name) => string.Equals(Name ?? Verb, name, StringComparison.Ordinal);

    /// <summary>The module's name, or its verb and number if it has no name.</summary>
    public override string ToString() => Name ?? $"{Verb} #{Number}";
}
