using Meep2.Meeplang.Syntax;
using Meep2.Runtime;

namespace Meep2.Meeplang.Loading;

/// <summary>One module while a pipeline is being loaded.</summary>
internal sealed class LoadNode(ModuleSyntax syntax, IReadOnlyList<ModuleRegistration> registrations)
{
    public ModuleSyntax Syntax { get; } = syntax;

    /// <summary>The inflections the module's line could mean, before its parameters are filled in.</summary>
    public IReadOnlyList<ModuleRegistration> Registrations { get; } = registrations;

    /// <summary>The inflections its parameters fit, in the order the verb tries them.</summary>
    public List<Candidate> Candidates { get; } = [];

    /// <summary>What every inflection of the module has in common: whether it's a source, or a passthrough module.</summary>
    public ModuleRegistration Registration => Registrations[0];

    public NodeOptions Options { get; set; } = NodeOptions.Default;

    /// <summary>The module this one is indented under, whose error policy it inherits.</summary>
    public LoadNode? Parent { get; set; }

    /// <summary>The parts of the error policy its <c>@on-error</c> sets.</summary>
    public ErrorPolicyParts? OnError { get; set; }

    public List<LoadNode> Upstream { get; } = [];

    public HashSet<Type> Inputs { get; } = [];

    public HashSet<Type> Outputs { get; } = [];

    /// <summary>Modules named by its parameters, such as Tap's.</summary>
    public IReadOnlyList<(string Name, SourceSpan Span)> References => Candidates.Count > 0 ? Candidates[0].Parameters.References : [];

    /// <summary>Whether it gets messages without being indented under anything: a source, or a Tap.</summary>
    public bool HasOwnInput => Registration.IsSource || References.Count > 0;

    public string Label => Syntax.Name ?? Syntax.Verb;
}
