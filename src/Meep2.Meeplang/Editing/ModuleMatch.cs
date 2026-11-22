using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>A module of a pipeline's old text and of its new text that are the same module, or one that's only in one of them.</summary>
/// <param name="Old">The module in the old text, or null if it's new.</param>
/// <param name="Next">The module in the new text, or null if it was removed.</param>
/// <param name="By">How they were matched.</param>
public sealed record ModuleMatch(ModuleSyntax? Old, ModuleSyntax? Next, MatchedBy By)
{
    /// <summary>Whether its parameters' text differs between the two.</summary>
    public bool ParametersChanged { get; init; }

    /// <summary>Whether it's under a different module now, or has moved to or from the left margin.</summary>
    public bool Moved { get; init; }
}
