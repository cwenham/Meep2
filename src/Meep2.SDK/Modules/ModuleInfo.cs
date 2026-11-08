namespace Meep2.SDK.Modules;

/// <summary>
/// A module's place in a pipeline: its number, its verb, its inflection, and the name it was given, if any.
/// Every message records the module that emitted it, which is how <c>fam</c> finds ancestors.
/// </summary>
/// <remarks>
/// A module whose verb routes between several inflections has one of these for each inflection, all with the same
/// number, verb and name, so a message records which inflection made it.
/// </remarks>
public sealed class ModuleInfo
{
    internal ModuleInfo(int number, string verb, string? name, string? inflection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        Number = number;
        Verb = verb;
        Name = name;
        Inflection = inflection;
    }

    /// <summary>The module's position in its pipeline, counting from 1.</summary>
    public int Number { get; }

    /// <summary>The verb the module is an instance of, such as <c>Fetch</c>.</summary>
    public string Verb { get; }

    /// <summary>The name given with <c>as Name</c>, or null.</summary>
    public string? Name { get; }

    /// <summary>The inflection that did the work, such as <c>HTTP</c> for a <c>Fetch</c>, or null if the module has only one and it has no name of its own.</summary>
    public string? Inflection { get; }

    /// <summary>
    /// Whether <paramref name="name"/> refers to this module: its own name if it has one, otherwise its verb or its
    /// inflection (Q74). Names are case-sensitive.
    /// </summary>
    public bool IsCalled(string name) =>
        Name is not null
            ? string.Equals(Name, name, StringComparison.Ordinal)
            : string.Equals(Verb, name, StringComparison.Ordinal) || string.Equals(Inflection, name, StringComparison.Ordinal);

    /// <summary>The module's name, or its verb and number if it has no name.</summary>
    public override string ToString() => Name ?? $"{Verb} #{Number}";
}
