namespace Meep2.Meeplang.Editing;

/// <summary>How a module of the new text was found to be one of the old text.</summary>
public enum MatchedBy
{
    /// <summary>It wasn't: the module is new, or was removed.</summary>
    Nothing,

    /// <summary>The same name, given with <c>as</c>.</summary>
    Name,

    /// <summary>The same verb, under the same module, with the parameters most like it.</summary>
    Structure,

    /// <summary>The same verb, in the same order among the modules with that verb that were left.</summary>
    Position,
}
