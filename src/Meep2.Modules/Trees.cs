using System.Text.Json;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Modules;

/// <summary>What Learn, Classify and Forget share.</summary>
internal static class Trees
{
    public static string CheckName(string name, string verb) =>
        !string.IsNullOrWhiteSpace(name) && name != BayesTree.SharedPrefix
            ? name
            : throw new ArgumentException($"{verb} needs the name of a classification tree.", nameof(name));

    /// <summary>The file a tree is kept in: <paramref name="file"/>, or its place in <c>~/.meep/trees</c>.</summary>
    public static string PathOf(string name, string? file, ModuleContext context) =>
        file is not null ? Paths.Expand(file) : BayesTree.PathFor(context.PipelineName, name);

    /// <summary>
    /// Refuses <paramref name="message"/> unless it may read the tree's file, when it's one <c>file=</c> names, and
    /// write it, when the module saves it (<c>meep.files.read</c>, <c>meep.files.write</c>). A tree in its place in
    /// <c>~/.meep/trees</c> needs neither: it's Meep's own (Q314, Q321).
    /// </summary>
    public static void Demand(string name, string? file, bool saves, Message message, ModuleContext context)
    {
        if (file is null)
            return;
        var path = PathOf(name, file, context);
        if (file is not null)
            context.DemandFile(MeepPermissions.FilesRead, message, path);
        if (saves)
            context.DemandFile(MeepPermissions.FilesWrite, message, path);
    }

    /// <summary>
    /// The module's tree, held from the first time it's needed until the module is disposed, or null after emitting an
    /// error. <paramref name="saves"/> says whether the module saves the tree, so it's saved when the last lets go.
    /// </summary>
    public static BayesTree? Open(ref BayesTree.TreeHold? hold, string name, string? file, bool saves, ModuleContext context)
    {
        if (hold is not null)
            return hold.Tree;
        var path = PathOf(name, file, context);
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
