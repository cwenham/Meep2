using System.Text;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>
/// Works out which modules of a pipeline's new text are which of its old text, without IDs in the file (Q30, Q61):
/// by name first, then those whose verb, name and parameters are unchanged under the same module, then those unchanged
/// anywhere else, then by structure (the same verb, under the same module, with the most similar parameters), then by
/// position among the modules with the same verb. A module whose verb changed is a new module.
/// </summary>
/// <remarks>
/// Structure is what keeps the commonest edit right, inserting a module mid-chain: matching by position alone would
/// shift every module after the insertion by one, and give each one's state to the wrong module.
/// </remarks>
public static class ModuleMatcher
{
    /// <summary>Matches the modules of two versions of a pipeline file.</summary>
    public static IReadOnlyList<ModuleMatch> Match(PipelineSyntax old, PipelineSyntax next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        return new ModuleMatching(old, next).Run();
    }

    /// <summary>
    /// A module's parameters: its verb, main value and named parameters, but not its name. They're written from the
    /// syntax, so a template's copy shows its parameters' values.
    /// </summary>
    public static string ParametersOf(PipelineSyntax syntax, ModuleSyntax module)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(module);
        var text = new StringBuilder();
        text.Append(module.Plugin).Append('.').Append(module.Verb).Append('.').Append(module.Inflection);
        if (module.Positional is { } positional)
            text.Append(' ').Append(ExpressionText.Of(positional));
        foreach (var argument in module.Arguments)
            text.Append(' ').Append(argument.Name).Append('=').Append(ExpressionText.Of(argument.Value));
        return text.ToString();
    }
}
