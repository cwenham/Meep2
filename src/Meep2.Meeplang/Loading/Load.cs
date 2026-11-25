using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Loads a parsed pipeline: works out its constants and settings, copies in templates and Distribute's branches,
/// resolves each module line's verb and binds its parameters (<see cref="VerbResolver"/>), works out its options
/// (<see cref="OptionsBuilder"/>), connects the modules (<see cref="Wiring"/>), checks the message types that flow
/// between them (<see cref="TypeFlow"/>), and defines the pipeline it makes.
/// </summary>
internal sealed class Load(ModuleRegistry registry, PipelineSyntax syntax, Diagnostics diagnostics)
{
    private readonly Dictionary<ModuleSyntax, LoadNode> _nodes = [];
    private readonly List<LoadNode> _ordered = [];
    private readonly VerbResolver _verbs = new(registry, syntax, diagnostics);
    private readonly OptionsBuilder _options = new(syntax, diagnostics);

    // The pipeline's modules once templates are copied in.
    private IReadOnlyList<ModuleSyntax> _modules = syntax.Modules;

    /// <summary>The pipeline's settings, with its defaults block applied, once it has loaded.</summary>
    public PipelineOptions Options { get; private set; } = PipelineOptions.Default;

    public Pipeline? Run(PipelineOptions options) => Prepare(options) ? Define(Options).Builder.Build() : null;

    /// <summary>Resolves, binds, wires and checks every module. Returns false if there were errors.</summary>
    public bool Prepare(PipelineOptions options)
    {
        var constants = _options.Constants();
        options = _options.WithDefaults(options);
        _modules = TemplateExpander.Expand(syntax, diagnostics, name => registry.TryGetVerb(name, out _) || registry.IsPlugin(name));
        _modules = CopyDistributedBranches(_modules, diagnostics);
        if (diagnostics.HasErrors)
            return false;
        CreateNodes(_modules);
        CheckNames();

        var scope = Scope(constants);
        foreach (var node in _ordered)
        {
            _verbs.Bind(node, scope);
            node.Options = _options.For(node);
        }
        if (diagnostics.HasErrors)
            return false;

        var wiring = new Wiring(_nodes, _ordered, diagnostics);
        wiring.Wire(_modules);
        wiring.WireReferences();
        if (diagnostics.HasErrors)
            return false;

        OptionsBuilder.ApplyErrorPolicies(_ordered, options.ErrorPolicy);
        var types = new TypeFlow(_ordered, diagnostics);
        types.Flow();
        types.Check();
        if (diagnostics.HasErrors)
            return false;
        wiring.CheckBuffers(options.BufferSize);
        Options = options;
        return true;
    }

    /// <summary>
    /// The modules a pipeline runs, in the order it numbers them: with templates copied in and Distribute's branches
    /// copied. Mistakes are left for loading to report. Edits find modules by these numbers (Q129).
    /// </summary>
    internal static IReadOnlyList<ModuleSyntax> Running(PipelineSyntax syntax)
    {
        var ignored = new Diagnostics(syntax.Source.Path);
        return CopyDistributedBranches(TemplateExpander.Expand(syntax, ignored, _ => false), ignored);
    }

    /// <summary>
    /// The pipeline as a builder, with each module line's identity in it and what its parameters were made from, for
    /// building it or changing a running one into it.
    /// </summary>
    internal LoadedModules Define(PipelineOptions options)
    {
        var builder = new PipelineBuilder(Path.GetFileNameWithoutExtension(syntax.Source.Path) is { Length: > 0 } name ? name : "Pipeline", options);
        var infos = new Dictionary<LoadNode, ModuleInfo>();
        foreach (var node in _ordered)
            infos[node] = Add(builder, node);
        foreach (var node in _ordered)
        {
            foreach (var upstream in node.Upstream)
                builder.Connect(infos[upstream], infos[node]);
        }
        return new LoadedModules(
            builder,
            ReferenceEquals(_modules, syntax.Modules) ? syntax : syntax.WithModules(_modules),
            _ordered.ToDictionary(node => node.Syntax, node => infos[node]),
            _ordered.ToDictionary(node => node.Syntax, node => (IReadOnlyList<Type>)[.. node.Candidates.Select(c => c.Registration.ModuleType)]),
            [.. _ordered.Select(node => new LoadedModule(
                node.Syntax,
                infos[node],
                [.. node.Candidates.Select(c => c.Registration)],
                node.Options.Errors,
                [.. node.Upstream.Distinct().Select(u => u.Syntax)],
                node.Outputs.ToHashSet(),
                node.Parent?.Syntax)
            {
                Permissions = [.. node.Options.Permissions.Union(node.Candidates.SelectMany(c => c.Registration.RequiredPermissions))],
            })]);
    }

    // What expressions in the modules' parameters can name: the constants, fam's modules, the message types, and
    // the modules' labels.
    private CompileScope Scope(Dictionary<string, ConstantValue> constants) => new(
        constants,
        FamTarget,
        name => registry.TryGetMessageType(name, out var type) ? type : null,
        [.. _ordered.Select(n => n.Label).Distinct()],
        name => registry.SharedBy(name) is { } plugins
            ? VerbResolver.SharedMessage(name, plugins)
            : $"There's no message type called {name}." + Suggestions.For(name, registry.MessageTypeNames));

    private void CreateNodes(IReadOnlyList<ModuleSyntax> modules)
    {
        foreach (var module in modules)
        {
            if (_verbs.Resolve(module) is { } registrations)
            {
                var node = new LoadNode(module, registrations);
                _nodes[module] = node;
                _ordered.Add(node);
            }
            CreateNodes(module.Children);
        }
    }

    private void CheckNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _ordered)
        {
            // Distribute's copies share their original's name.
            if (node.Syntax.Name is { } name && !seen.Add(name) && node.Syntax.CopyOf is null)
            {
                var hint = node.Syntax.ExpandedFrom is { Name: null } use
                    ? $" It's named inside the template {use.Verb}: give each use of it a name, as in {use.Verb} as First, and the names inside start with that name (First_{name})."
                    : "";
                diagnostics.Error($"There's already a module called {name}.{hint}", node.Syntax.NameSpan ?? node.Syntax.VerbSpan);
            }
        }
    }

    // A module in the builder: a source with its setup, or a verb with each inflection it could be.
    private static ModuleInfo Add(PipelineBuilder builder, LoadNode node)
    {
        if (node.Registration.IsSource)
        {
            var source = node.Candidates[0];
            return builder.AddSource(
                source.Registration.Verb,
                new SourceSetup(() => (ASource)source.Registration.Create(source.Parameters.Value))
                {
                    Parameters = source.Parameters.Value,
                    Reconfigure = source.Registration.Reconfigure,
                },
                node.Syntax.Name,
                node.Options,
                source.Registration.Inflection);
        }
        var inflections = node.Candidates
            .Select(c => new VerbInflection(c.Registration.Inflection, () => (AInflection)c.Registration.Create(c.Parameters.Value), c.WhyNot)
            {
                Parameters = c.Parameters.Value,
                Reconfigure = c.Registration.Reconfigure,
            })
            .ToList();
        return builder.AddVerb(node.Registration.Verb, inflections, node.Syntax.Name, node.Options);
    }

    // Distribute N copies the branch indented under it N times, side by side, before anything else sees it (Q53).
    private static IReadOnlyList<ModuleSyntax> CopyDistributedBranches(IReadOnlyList<ModuleSyntax> modules, Diagnostics diagnostics)
    {
        static bool IsDistribute(ModuleSyntax m) => m.Verb == "Distribute" && m.Plugin is null;
        static bool Any(IEnumerable<ModuleSyntax> list) => list.Any(m => IsDistribute(m) || Any(m.Children));
        if (!Any(modules))
            return modules;
        return Copy(modules);

        List<ModuleSyntax> Copy(IReadOnlyList<ModuleSyntax> list)
        {
            var copied = new List<ModuleSyntax>();
            foreach (var module in list)
            {
                IEnumerable<ModuleSyntax> children = Copy(module.Children);
                if (IsDistribute(module) && module.Children.Count > 0)
                {
                    if (module.Positional is null || !OptionsBuilder.TryConstant(module.Positional, diagnostics, out var value) || OptionsBuilder.PositiveWholeNumber(value) is not (>= 2 and var copies))
                    {
                        diagnostics.Error("Distribute needs a whole number of copies, at least 2, written in the file, as in Distribute 4, so the loader can make them.", module.Positional?.Span ?? module.VerbSpan);
                        copies = 1;
                    }
                    var originals = children.ToList();
                    children = Enumerable.Range(0, copies).SelectMany(lane => lane == 0 ? originals : originals.Select(DeepCopy));
                }
                copied.Add(module.Copy(e => e, children));
            }
            return copied;
        }

        static ModuleSyntax DeepCopy(ModuleSyntax module)
        {
            var copy = module.Copy(e => e, module.Children.Select(DeepCopy));
            copy.CopyOf = module.CopyOf ?? module;
            return copy;
        }
    }

    // What fam.Name finds: a module with that name, or failing that, modules with that verb or inflection and no name
    // of their own (Q74).
    private FamTarget? FamTarget(string name)
    {
        var matches = _ordered
            .Where(n => n.Syntax.Name == name)
            .Select(n => n.Registrations)
            .ToList();
        if (matches.Count == 0)
        {
            matches = _ordered
                .Where(n => n.Syntax.Name is null)
                .Select(n => n.Syntax.Verb == name || n.Registration.Verb == name
                    ? n.Registrations
                    : [.. n.Registrations.Where(r => r.Inflection == name)])
                .Where(registrations => registrations.Count > 0)
                .ToList();
        }
        if (matches.Count == 0)
            return null;
        var emitted = matches.SelectMany(registrations => registrations).SelectMany(r => r.Emits).Distinct().ToList();
        return new FamTarget(emitted.Count == 1 ? emitted[0] : null);
    }
}
