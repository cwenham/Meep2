using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>The outcome of loading a pipeline: the pipeline if it loaded, and any problems found.</summary>
/// <param name="Pipeline">The pipeline, ready to run, or null if there were errors.</param>
/// <param name="Diagnostics">Errors and warnings from parsing and loading.</param>
public sealed record LoadResult(Pipeline? Pipeline, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether the pipeline loaded.</summary>
    public bool Succeeded => Pipeline is not null;
}

/// <summary>
/// Turns a parsed Meeplang file into a pipeline: finds each verb's module, fills in parameters, connects modules,
/// and checks expressions against the message types that can reach them.
/// </summary>
public sealed class PipelineLoader(ModuleRegistry registry)
{
    /// <summary>Loads a parsed file. Problems are reported in the result, not thrown.</summary>
    /// <param name="syntax">The parsed file.</param>
    /// <param name="options">Settings for the pipeline; the file's <c>defaults</c> block overrides them.</param>
    public LoadResult Load(PipelineSyntax syntax, PipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var diagnostics = new Diagnostics(syntax.Source.Path);
        foreach (var diagnostic in syntax.Diagnostics)
            diagnostics.Add(diagnostic);
        if (diagnostics.HasErrors)
            return new LoadResult(null, diagnostics.Items);

        var load = new Load(registry, syntax, diagnostics);
        var pipeline = load.Run(options ?? PipelineOptions.Default);
        var sorted = diagnostics.Items.OrderBy(d => d.Span.Line).ThenBy(d => d.Span.Column).ToList();
        return new LoadResult(diagnostics.HasErrors ? null : pipeline, sorted);
    }
}

/// <summary>One module while a pipeline is being loaded.</summary>
internal sealed class LoadNode(ModuleSyntax syntax, ModuleRegistration registration)
{
    public ModuleSyntax Syntax { get; } = syntax;

    public ModuleRegistration Registration { get; } = registration;

    public BoundParameters Parameters { get; set; } = new();

    public NodeOptions Options { get; set; } = NodeOptions.Default;

    public List<LoadNode> Upstream { get; } = [];

    public HashSet<Type> Inputs { get; } = [];

    public HashSet<Type> Outputs { get; } = [];

    /// <summary>Whether it gets messages without being indented under anything: a source, or a Tap.</summary>
    public bool HasOwnInput => Registration.IsSource || Parameters.References.Count > 0;

    public string Label => Syntax.Name ?? Syntax.Verb;
}

internal sealed class Load(ModuleRegistry registry, PipelineSyntax syntax, Diagnostics diagnostics)
{
    // typeof(Message) in a type set means "some message type nobody declared".
    private static readonly Type s_unknown = typeof(Message);

    private readonly Dictionary<ModuleSyntax, LoadNode> _nodes = [];
    private readonly List<LoadNode> _ordered = [];

    public Pipeline? Run(PipelineOptions options)
    {
        var constants = EvaluateConstants();
        options = ApplyDefaults(options);
        CreateNodes(syntax.Modules);
        CheckNames();

        var scope = new CompileScope(
            constants,
            FamTarget,
            name => registry.TryGetMessageType(name, out var type) ? type : null,
            [.. _ordered.Select(n => n.Label).Distinct()]);
        var binder = new ParameterBinder(scope, diagnostics, syntax);
        foreach (var node in _ordered)
        {
            node.Parameters = binder.Bind(node.Syntax, node.Registration);
            node.Options = BuildOptions(node);
        }
        if (diagnostics.HasErrors)
            return null;

        Wire(parent: null, syntax.Modules);
        WireReferences();
        if (diagnostics.HasErrors)
            return null;

        FlowTypes();
        CheckTypes();
        if (diagnostics.HasErrors)
            return null;

        return Build(options);
    }

    private Dictionary<string, ConstantValue> EvaluateConstants()
    {
        var constants = new Dictionary<string, ConstantValue>(StringComparer.Ordinal);
        var scope = new CompileScope(constants, _ => null, _ => null, []);
        foreach (var constant in syntax.Constants)
        {
            if (constant.Name is "msg" or "fam")
            {
                diagnostics.Error($"{constant.Name} is reserved, so it can't be a constant's name.", constant.NameSpan);
                continue;
            }
            if (constants.ContainsKey(constant.Name))
            {
                diagnostics.Error($"There's already a constant called {constant.Name}.", constant.NameSpan);
                continue;
            }
            try
            {
                constants[constant.Name] = ExpressionCompiler.Evaluate(constant.Value, scope);
            }
            catch (BindException exception)
            {
                diagnostics.Error(exception.Message, exception.Span);
            }
        }
        return constants;
    }

    private PipelineOptions ApplyDefaults(PipelineOptions options)
    {
        foreach (var setting in syntax.Defaults)
        {
            switch (setting.Name)
            {
                case "buffer":
                    if (PositiveWholeNumber(Constant(setting.Value)) is { } size)
                        options = options with { BufferSize = size };
                    else
                        diagnostics.Error("buffer needs a whole number of messages, at least 1.", setting.Value.Span);
                    break;
                case "expires" or "on-error":
                    diagnostics.Error($"The {setting.Name} default arrives with expiry and the error policy, in step 4.", setting.NameSpan);
                    break;
                default:
                    diagnostics.Error($"There's no default called {setting.Name}." + Suggestions.For(setting.Name, ["buffer", "expires", "on-error"]), setting.NameSpan);
                    break;
            }
        }
        return options;
    }

    private static int? PositiveWholeNumber(object? value) =>
        value is not null && Dynamic.TryConvert(value, typeof(int), out var whole) && whole is int size and > 0 ? size : null;

    private object? Constant(ExpressionSyntax value)
    {
        try
        {
            return ExpressionCompiler.Evaluate(value, CompileScope.Empty).Value;
        }
        catch (BindException exception)
        {
            diagnostics.Error(exception.Message, exception.Span);
            return null;
        }
    }

    private void CreateNodes(IReadOnlyList<ModuleSyntax> modules)
    {
        foreach (var module in modules)
        {
            if (module.Inflection is not null)
            {
                diagnostics.Error($"Choosing an inflection with {module.Verb}.{module.Inflection} arrives with verbs, in step 4.", module.VerbSpan);
            }
            else if (registry.TryGetModule(module.Verb, out var registration))
            {
                var node = new LoadNode(module, registration);
                _nodes[module] = node;
                _ordered.Add(node);
            }
            else
            {
                diagnostics.Error($"There's no module called {module.Verb}." + Suggestions.For(module.Verb, registry.Verbs), module.VerbSpan);
            }
            CreateNodes(module.Children);
        }
    }

    private void CheckNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _ordered)
        {
            if (node.Syntax.Name is { } name && !seen.Add(name))
                diagnostics.Error($"There's already a module called {name}.", node.Syntax.NameSpan ?? node.Syntax.VerbSpan);
        }
    }

    // What fam.Name finds: a module with that name, or failing that, modules with that verb and no name of their own.
    private FamTarget? FamTarget(string name)
    {
        var matches = _ordered.Where(n => n.Syntax.Name == name).ToList();
        if (matches.Count == 0)
            matches = _ordered.Where(n => n.Syntax.Name is null && n.Syntax.Verb == name).ToList();
        if (matches.Count == 0)
            return null;
        var emitted = matches.SelectMany(n => n.Registration.Emits).Distinct().ToList();
        return new FamTarget(emitted.Count == 1 ? emitted[0] : null);
    }

    private NodeOptions BuildOptions(LoadNode node)
    {
        var options = NodeOptions.Default;
        foreach (var modifier in node.Syntax.Modifiers)
        {
            if (node.Registration.IsSource && modifier.Name is "buffer" or "bypass" or "errors" or "no-errors")
            {
                diagnostics.Error($"{node.Syntax.Verb} is a source, so @{modifier.Name} doesn't apply to it.", modifier.Span);
                continue;
            }
            switch (modifier.Name)
            {
                case "buffer":
                    if (modifier.Value is not null && PositiveWholeNumber(Constant(modifier.Value)) is { } size)
                        options = options with { BufferSize = size };
                    else
                        diagnostics.Error("@buffer needs a whole number of messages, at least 1, as in @buffer=1000.", modifier.Span);
                    break;
                case "bypass":
                    options = options with { Bypass = true };
                    break;
                case "errors":
                    options = options with { Errors = ErrorDelivery.Only };
                    break;
                case "no-errors":
                    options = options with { Errors = ErrorDelivery.Exclude };
                    break;
                case "expires" or "accept-expired" or "on-error":
                    diagnostics.Error($"@{modifier.Name} arrives with expiry and the error policy, in step 4.", modifier.Span);
                    break;
                default:
                    diagnostics.Error(
                        $"There's no modifier called @{modifier.Name}." + Suggestions.For(modifier.Name, ["buffer", "bypass", "errors", "no-errors", "expires", "accept-expired", "on-error"]),
                        modifier.Span);
                    break;
            }
        }

        // Messages an expression doesn't fit skip the module (Q69).
        var computed = node.Parameters.Computed;
        if (computed.Count > 0)
            options = options with { HandlesType = type => computed.All(c => c.AppliesTo(type)) };
        return options;
    }

    // Connects modules by indentation. Each module feeds the modules indented under it. A run of + lines feeds the
    // next unmarked line at the same indentation, from every end of each + line's chain.
    private void Wire(LoadNode? parent, IReadOnlyList<ModuleSyntax> siblings)
    {
        var feeders = new List<LoadNode>();
        foreach (var module in siblings)
        {
            if (!_nodes.TryGetValue(module, out var node))
                continue;

            if (module.IsFeeder)
            {
                if (!node.HasOwnInput)
                    diagnostics.Error($"A + line starts a chain of its own, so it needs a source or a Tap, not {module.Verb}.", module.VerbSpan);
                feeders.Add(node);
            }
            else
            {
                if (parent is not null && node.HasOwnInput)
                {
                    diagnostics.Error(
                        node.Registration.IsSource
                            ? $"{module.Verb} is a source, so it can't go under another module. Start a line with it, or with + to merge it."
                            : $"{module.Verb} gets its messages from the module it names, so it can't go under another module.",
                        module.VerbSpan);
                }
                else if (parent is not null)
                {
                    node.Upstream.Add(parent);
                }
                else if (!node.HasOwnInput && feeders.Count == 0)
                {
                    diagnostics.Error($"Nothing feeds {module.Verb}. Indent it under a source, or feed it with + lines above it.", module.VerbSpan);
                }
                else if (node.HasOwnInput && feeders.Count > 0)
                {
                    diagnostics.Error($"{module.Verb} is a source, so + lines can't feed it.", module.VerbSpan);
                }

                foreach (var feeder in feeders)
                {
                    foreach (var end in Ends(feeder.Syntax))
                        node.Upstream.Add(_nodes[end]);
                }
                feeders.Clear();
            }
            Wire(node, module.Children);
        }
    }

    // The ends of a chain: modules with nothing indented under them. + lines inside the chain end at the module
    // they feed, so they're not ends of their own.
    private IEnumerable<ModuleSyntax> Ends(ModuleSyntax module)
    {
        if (!_nodes.ContainsKey(module))
            yield break;
        if (module.Children.Count == 0)
        {
            yield return module;
            yield break;
        }
        foreach (var child in module.Children)
        {
            if (child.IsFeeder)
                continue;
            foreach (var end in Ends(child))
                yield return end;
        }
    }

    private void WireReferences()
    {
        foreach (var node in _ordered)
        {
            // A module is found by its as name only, so adding another module with the same verb can't change
            // what an existing reference finds (Q83).
            foreach (var (name, span) in node.Parameters.References)
            {
                var named = _ordered.FirstOrDefault(n => n.Syntax.Name == name);
                if (named is not null)
                    node.Upstream.Add(named);
                else if (_ordered.Any(n => n.Syntax.Verb == name))
                    diagnostics.Error($"There's no module called {name}. Modules are found by name, so give the {name} you mean a name with 'as', and use that.", span);
                else
                    diagnostics.Error($"There's no module called {name}." + Suggestions.For(name, _ordered.Select(n => n.Syntax.Name).OfType<string>()), span);
            }
        }
    }

    // Works out which message types can reach each module, from what modules declare they emit.
    private void FlowTypes()
    {
        foreach (var node in _ordered.Where(n => n.Registration.IsSource))
        {
            foreach (var type in Declared(node))
                node.Outputs.Add(type);
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var node in _ordered.Where(n => !n.Registration.IsSource))
            {
                node.Inputs.Add(typeof(ErrorMessage)); // any module upstream can fail
                foreach (var upstream in node.Upstream)
                    node.Inputs.UnionWith(upstream.Outputs);

                var before = node.Outputs.Count;
                node.Outputs.UnionWith(Declared(node));
                foreach (var type in node.Inputs.ToList())
                {
                    var (takes, applies) = Reaches(node, type);
                    if (takes && applies && (node.Registration.EmitsInput || node.Registration.IsPassthrough))
                        node.Outputs.Add(type); // passed on
                    else if (takes && !applies)
                        node.Outputs.Add(type); // skips the module
                    else if (!takes && (IsError(type) || node.Options.Bypass))
                        node.Outputs.Add(type); // goes past the module
                }
                changed |= node.Outputs.Count != before;
            }
        }
    }

    private static IEnumerable<Type> Declared(LoadNode node)
    {
        var registration = node.Registration;
        if (registration.Emits.Count > 0)
            return registration.Emits;
        return registration.EmitsInput || registration.IsPassthrough ? [] : [s_unknown];
    }

    // Whether the module would take messages of this type, and if so, whether its expressions fit them.
    private static (bool Takes, bool Applies) Reaches(LoadNode node, Type type)
    {
        var wanted = node.Options.Errors switch
        {
            ErrorDelivery.Only => IsError(type),
            ErrorDelivery.Exclude => !IsError(type),
            _ => true,
        };
        var accepts = type == s_unknown || node.Registration.InputType!.IsAssignableFrom(type);
        if (!wanted || !accepts)
            return (false, false);
        var applies = type == s_unknown || node.Parameters.Computed.All(c => c.AppliesTo(type));
        return (true, applies);
    }

    private static bool IsError(Type type) => typeof(ErrorMessage).IsAssignableFrom(type);

    // Reports modules that could never receive anything, and expressions that fit none of the types that reach them.
    private void CheckTypes()
    {
        foreach (var node in _ordered.Where(n => !n.Registration.IsSource))
        {
            var errorsOnly = node.Options.Errors == ErrorDelivery.Only;
            var candidates = node.Inputs.Where(t => t != s_unknown && (errorsOnly ? IsError(t) : !IsError(t))).ToList();
            var unknown = node.Inputs.Contains(s_unknown);
            var taken = candidates.Where(t => Reaches(node, t).Takes).ToList();

            if (!errorsOnly && !unknown && candidates.Count > 0 && taken.Count == 0)
            {
                var input = ExpressionBinder.FriendlyName(node.Registration.InputType!);
                var arriving = string.Join(" or ", candidates.Select(ExpressionBinder.FriendlyName));
                diagnostics.Error($"{node.Syntax.Verb} takes {input}, but only {arriving} can reach it, so it would never receive anything.", node.Syntax.VerbSpan);
                continue;
            }

            foreach (var computed in node.Parameters.Computed)
            {
                if (taken.Count == 0)
                {
                    if (unknown)
                    {
                        diagnostics.Warning(
                            $"Meeplang can't check {computed.Text} before the pipeline runs, because a module before {node.Syntax.Verb} doesn't say what it emits.",
                            node.Syntax.VerbSpan);
                    }
                    continue;
                }
                if (!taken.Any(computed.AppliesTo) && computed.ProblemWith(taken[0]) is var (problem, span))
                    diagnostics.Error(problem, span);
            }
        }
    }

    private Pipeline Build(PipelineOptions options)
    {
        var builder = new PipelineBuilder(Path.GetFileNameWithoutExtension(syntax.Source.Path) is { Length: > 0 } name ? name : "Pipeline", options);
        var infos = new Dictionary<LoadNode, ModuleInfo>();
        foreach (var node in _ordered)
        {
            var registration = node.Registration;
            var parameters = node.Parameters.Value;
            infos[node] = registration.IsSource
                ? builder.AddSource(registration.Verb, () => (ASource)registration.Create(parameters), node.Syntax.Name)
                : builder.Add(registration.Verb, () => (AInflection)registration.Create(parameters), node.Syntax.Name, node.Options);
        }
        foreach (var node in _ordered)
        {
            foreach (var upstream in node.Upstream)
                builder.Connect(infos[upstream], infos[node]);
        }
        return builder.Build();
    }
}
