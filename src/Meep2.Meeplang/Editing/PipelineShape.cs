using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Editing;

/// <summary>A running pipeline as a graph: its modules and the links between them (<see cref="LivePipeline.Shape"/>).</summary>
/// <param name="Syntax">The text it runs from, with the files it includes.</param>
/// <param name="Expanded">The text with its templates copied in: the modules that were built.</param>
/// <param name="Modules">Its modules, in the order the pipeline numbers them.</param>
/// <param name="Links">The links between them, grouped by the module they go to, in the order of its inputs.</param>
public sealed record PipelineShape(PipelineSyntax Syntax, PipelineSyntax Expanded, IReadOnlyList<ModuleShape> Modules, IReadOnlyList<LinkShape> Links)
{
    /// <summary>The shape of loaded modules, with each module's key.</summary>
    /// <param name="syntax">The text they were loaded from.</param>
    /// <param name="loaded">What the loader made of it.</param>
    /// <param name="keys">Each module's key, by its line as built.</param>
    internal static PipelineShape Of(PipelineSyntax syntax, LoadedModules loaded, IReadOnlyDictionary<ModuleSyntax, string> keys)
    {
        var bySyntax = loaded.Modules.ToDictionary(m => m.Syntax, (IEqualityComparer<ModuleSyntax>)ReferenceEqualityComparer.Instance);
        string? KeyOf(ModuleSyntax? module) => module is not null && keys.TryGetValue(module, out var key) ? key : null;

        var modules = loaded.Modules.Select(m => new ModuleShape(
            keys[m.Syntax],
            m.Info,
            m.Syntax,
            m.Inflections,
            m.Inflections[0].Role,
            m.Errors)
        {
            Under = KeyOf(Under(loaded.Expanded.Modules, m.Syntax)),
            CopyOf = KeyOf(m.Syntax.CopyOf),
        }).ToList();

        var links = new List<LinkShape>();
        foreach (var to in loaded.Modules)
        {
            for (var i = 0; i < to.Upstream.Count; i++)
            {
                var from = bySyntax[to.Upstream[i]];
                var types = from.Outputs.Append(typeof(ErrorMessage))
                    .Where(t => to.Errors switch
                    {
                        ErrorDelivery.Only => typeof(ErrorMessage).IsAssignableFrom(t),
                        ErrorDelivery.Exclude => !typeof(ErrorMessage).IsAssignableFrom(t),
                        _ => true,
                    })
                    .Distinct()
                    .ToList();
                links.Add(new LinkShape(keys[from.Syntax], keys[to.Syntax], from.Info, to.Info, i + 1, to.Errors, types));
            }
        }
        return new PipelineShape(syntax, loaded.Expanded, modules, links);
    }

    // The module a module is indented under, as built, or null at the left margin.
    private static ModuleSyntax? Under(IReadOnlyList<ModuleSyntax> modules, ModuleSyntax module)
    {
        foreach (var candidate in modules)
        {
            if (candidate.Children.Contains(module))
                return candidate;
            if (Under(candidate.Children, module) is { } found)
                return found;
        }
        return null;
    }
}

/// <summary>One module of a running pipeline.</summary>
/// <param name="Key">
/// A name for it that lasts while the pipeline runs: when the pipeline's text changes, a module matched with one
/// before the change (<see cref="ModuleMatcher"/>) keeps that one's key, though its number may change, and a new
/// module gets a key never used before. A restart keeps them too. The first keys are m1, m2 and so on, by number.
/// </param>
/// <param name="Info">Its identity in the pipeline.</param>
/// <param name="Syntax">Its line, as built: a copy if a template or Distribute made it.</param>
/// <param name="Inflections">The inflections it can resolve to, in the order the verb tries them.</param>
/// <param name="Role">What its verb does with messages.</param>
/// <param name="Errors">Whether it receives errors: only errors for <c>@errors</c>.</param>
public sealed record ModuleShape(string Key, ModuleInfo Info, ModuleSyntax Syntax, IReadOnlyList<ModuleRegistration> Inflections, VerbRole Role, ErrorDelivery Errors)
{
    /// <summary>The key of the module it's indented under, if any.</summary>
    public string? Under { get; init; }

    /// <summary>The key of the module it's a copy of, if Distribute copied its branch.</summary>
    public string? CopyOf { get; init; }
}

/// <summary>A link: everything one module emits goes to another.</summary>
/// <param name="From">The key of the module it comes from.</param>
/// <param name="To">The key of the module it goes to.</param>
/// <param name="FromInfo">The module it comes from.</param>
/// <param name="ToInfo">The module it goes to.</param>
/// <param name="Input">Which of the receiving module's inputs it is, counting from 1, as Zip numbers them.</param>
/// <param name="Errors">
/// The receiving module's <c>@errors</c> or <c>@no-errors</c>: whether the link carries errors too, only errors, or none.
/// </param>
/// <param name="Types">
/// The message types it can carry, as far as the loader can tell. <see cref="SDK.Messages.Message"/> stands for types
/// a module before it doesn't declare.
/// </param>
public sealed record LinkShape(string From, string To, ModuleInfo FromInfo, ModuleInfo ToInfo, int Input, ErrorDelivery Errors, IReadOnlyList<Type> Types)
{
    /// <summary>A message type's name, as <c>is</c> takes it, such as <c>Tick</c> or <c>HttpResponse</c>.</summary>
    public static string NameOf(Type type) => Compilation.ExpressionBinder.FriendlyName(type);
}
