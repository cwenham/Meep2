using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>The verbs a text can use, or null with the reasons when its plugins can't be loaded.</summary>
public delegate Task<(ModuleRegistry? Registry, IReadOnlyList<Diagnostic> Problems)> RegistryLookup(PipelineSyntax syntax, CancellationToken cancellationToken);
