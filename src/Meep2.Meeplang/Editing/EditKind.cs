namespace Meep2.Meeplang.Editing;

/// <summary>What an edit does to a module.</summary>
public enum EditKind
{
    /// <summary>Sets a parameter's value, adding the parameter if it isn't there. With no parameter, sets the main value written after the verb.</summary>
    Set,

    /// <summary>Removes a parameter, or with no parameter, the main value.</summary>
    Unset,

    /// <summary>Sets an <c>@</c> modifier, with or without a value.</summary>
    SetModifier,

    /// <summary>Removes an <c>@</c> modifier.</summary>
    RemoveModifier,

    /// <summary>Gives the module a name with <c>as</c>, or with no value, takes its name away.</summary>
    Rename,

    /// <summary>Adds a module as a new branch under this one, after any it has.</summary>
    Add,

    /// <summary>Inserts a module between this one and the modules under it, which move under the new one.</summary>
    Insert,

    /// <summary>
    /// Removes the module, with a comment written right above it. The modules under it move up to take its place.
    /// </summary>
    Remove,

    /// <summary>Removes the module and everything under it, with a comment written right above it.</summary>
    RemoveBranch,

    /// <summary>Sets a setting under <c>defaults</c>, adding the block if there isn't one (Q125).</summary>
    SetDefault,

    /// <summary>Removes a setting from <c>defaults</c>, and the block if it was the last.</summary>
    UnsetDefault,

    /// <summary>Sets a constant's value, declaring it if it isn't there.</summary>
    SetConstant,

    /// <summary>Removes a constant.</summary>
    UnsetConstant,

    /// <summary>Adds a <c>#plugin</c> line. The value is what comes after <c>#plugin</c>.</summary>
    AddPlugin,

    /// <summary>Removes a <c>#plugin</c> line, named by its <c>as</c> name, its address or its file's name.</summary>
    RemovePlugin,
}
