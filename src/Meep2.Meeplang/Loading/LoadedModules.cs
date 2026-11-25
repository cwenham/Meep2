using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>A loaded pipeline as a builder, with the module each module line became and the classes that make it.</summary>
/// <param name="Builder">The pipeline, ready to build, or to change a running one into.</param>
/// <param name="Expanded">The file with its templates copied in: the modules that were built.</param>
/// <param name="Infos">Each module line's identity in the pipeline, by the module lines of <paramref name="Expanded"/>.</param>
/// <param name="Types">The classes each module line's inflections are made from.</param>
/// <param name="Modules">What the loader worked out about each module, in the order the pipeline numbers them.</param>
internal sealed record LoadedModules(
    PipelineBuilder Builder,
    PipelineSyntax Expanded,
    IReadOnlyDictionary<ModuleSyntax, ModuleInfo> Infos,
    IReadOnlyDictionary<ModuleSyntax, IReadOnlyList<Type>> Types,
    IReadOnlyList<LoadedModule> Modules);

/// <summary>What the loader worked out about one module, for describing the pipeline as a graph.</summary>
/// <param name="Syntax">Its line, as built: a copy if a template or Distribute made it.</param>
/// <param name="Info">Its identity in the pipeline.</param>
/// <param name="Inflections">The inflections it can resolve to, in the order the verb tries them.</param>
/// <param name="Errors">Whether it receives errors: only errors for <c>@errors</c>.</param>
/// <param name="Upstream">The modules that feed it, in the order that numbers its inputs, without repeats.</param>
/// <param name="Outputs">
/// The message types it can emit or pass on, as far as the loader can tell. <see cref="Message"/> stands for a type
/// nobody declared. A source's leave out errors, though any module can emit them.
/// </param>
/// <param name="Parent">The module it's indented under, if any.</param>
internal sealed record LoadedModule(
    ModuleSyntax Syntax,
    ModuleInfo Info,
    IReadOnlyList<ModuleRegistration> Inflections,
    ErrorDelivery Errors,
    IReadOnlyList<ModuleSyntax> Upstream,
    IReadOnlySet<Type> Outputs,
    ModuleSyntax? Parent)
{
    /// <summary>
    /// The permissions a message needs to reach it: its <c>@permission</c> modifiers, then what its inflections require
    /// (<see cref="ModuleRegistration.RequiredPermissions"/>). Empty for a module that isn't gated.
    /// </summary>
    public IReadOnlyList<SDK.Security.PermissionName> Permissions { get; init; } = [];
}
