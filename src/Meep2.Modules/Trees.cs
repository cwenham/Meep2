using System.Text.Json;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>What Learn, Classify and Forget share.</summary>
internal static class Trees
{
    public static string CheckName(string name, string verb) =>
        !string.IsNullOrWhiteSpace(name) && name != BayesTree.SharedPrefix
            ? name
            : throw new ArgumentException($"{verb} needs the name of a classification tree.", nameof(name));

    /// <summary>
    /// The module's tree, held from the first time it's needed until the module is disposed, or null after emitting an
    /// error. <paramref name="saves"/> says whether the module saves the tree, so it's saved when the last lets go.
    /// </summary>
    public static BayesTree? Open(ref BayesTree.TreeHold? hold, string name, string? file, bool saves, ModuleContext context)
    {
        if (hold is not null)
            return hold.Tree;
        var path = file is not null ? Paths.Expand(file) : BayesTree.PathFor(context.PipelineName, name);
        try
        {
            return (hold = BayesTree.Hold(path, saves)).Tree;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.EmitError($"Couldn't load tree \"{name}\" from {path}: {exception.Message}", exception);
            return null;
        }
    }
}
