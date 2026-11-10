using System.Reflection;
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
/// Turns a parsed Meeplang file into a pipeline: finds each verb's inflections, fills in parameters, connects
/// modules, and checks expressions against the message types that can reach them.
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

    /// <summary>
    /// Loads a parsed file as far as a builder, knowing which module of it each module line became, so a running
    /// pipeline can be changed into it (goal 4). Null, with the problems, if it doesn't load.
    /// </summary>
    internal (LoadedModules? Modules, IReadOnlyList<Diagnostic> Diagnostics) Define(PipelineSyntax syntax, PipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var diagnostics = new Diagnostics(syntax.Source.Path);
        foreach (var diagnostic in syntax.Diagnostics)
            diagnostics.Add(diagnostic);
        LoadedModules? modules = null;
        if (!diagnostics.HasErrors)
        {
            var load = new Load(registry, syntax, diagnostics);
            if (load.Prepare(options ?? PipelineOptions.Default) && !diagnostics.HasErrors)
                modules = load.Define(load.Options);
        }
        var sorted = diagnostics.Items.OrderBy(d => d.Span.Line).ThenBy(d => d.Span.Column).ToList();
        return (diagnostics.HasErrors ? null : modules, sorted);
    }
}

/// <summary>A loaded pipeline as a builder, with the module each module line became and the classes that make it.</summary>
/// <param name="Builder">The pipeline, ready to build, or to change a running one into.</param>
/// <param name="Expanded">The file with its templates copied in: the modules that were built.</param>
/// <param name="Infos">Each module line's identity in the pipeline, by the module lines of <paramref name="Expanded"/>.</param>
/// <param name="Types">The classes each module line's inflections are made from.</param>
internal sealed record LoadedModules(PipelineBuilder Builder, PipelineSyntax Expanded, IReadOnlyDictionary<ModuleSyntax, ModuleInfo> Infos, IReadOnlyDictionary<ModuleSyntax, IReadOnlyList<Type>> Types);

/// <summary>One inflection a module might use, with its parameters filled in.</summary>
internal sealed class Candidate(ModuleRegistration registration, BoundParameters parameters)
{
    public ModuleRegistration Registration { get; } = registration;

    public BoundParameters Parameters { get; } = parameters;

    /// <summary>The inflection's input type. Only modules that handle messages have candidates that are asked.</summary>
    public Type InputType => Registration.InputType ?? typeof(Message);

    /// <summary>
    /// Asked for each message, when the inflection's parameters are worked out per message. Null when they're the
    /// same for every message, because then they were checked when the pipeline loaded.
    /// </summary>
    public Func<Message, string?>? WhyNot { get; set; }
}

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

internal sealed class Load(ModuleRegistry registry, PipelineSyntax syntax, Diagnostics diagnostics)
{
    // typeof(Message) in a type set means "some message type nobody declared".
    private static readonly Type s_unknown = typeof(Message);

    private readonly Dictionary<ModuleSyntax, LoadNode> _nodes = [];
    private readonly List<LoadNode> _ordered = [];

    // The pipeline's modules once templates are copied in.
    private IReadOnlyList<ModuleSyntax> _modules = syntax.Modules;

    /// <summary>The pipeline's settings, with its defaults block applied, once it has loaded.</summary>
    public PipelineOptions Options { get; private set; } = PipelineOptions.Default;

    public Pipeline? Run(PipelineOptions options) => Prepare(options) ? Define(Options).Builder.Build() : null;

    /// <summary>Resolves, binds, wires and checks every module. Returns false if there were errors.</summary>
    public bool Prepare(PipelineOptions options)
    {
        var constants = EvaluateConstants();
        options = ApplyDefaults(options);
        _modules = TemplateExpander.Expand(syntax, diagnostics, name => registry.TryGetVerb(name, out _) || registry.IsPlugin(name));
        _modules = CopyDistributedBranches(_modules, diagnostics);
        if (diagnostics.HasErrors)
            return false;
        CreateNodes(_modules);
        CheckNames();

        var scope = new CompileScope(
            constants,
            FamTarget,
            name => registry.TryGetMessageType(name, out var type) ? type : null,
            [.. _ordered.Select(n => n.Label).Distinct()],
            name => registry.SharedBy(name) is { } plugins
                ? SharedMessage(name, plugins)
                : $"There's no message type called {name}." + Suggestions.For(name, registry.MessageTypeNames));
        foreach (var node in _ordered)
        {
            BindCandidates(node, scope);
            node.Options = BuildOptions(node);
        }
        if (diagnostics.HasErrors)
            return false;

        Wire(parent: null, _modules);
        WireReferences();
        if (diagnostics.HasErrors)
            return false;

        ApplyErrorPolicies(options.ErrorPolicy);
        FlowTypes();
        CheckTypes();
        if (diagnostics.HasErrors)
            return false;
        Options = options;
        return true;
    }

    private Dictionary<string, ConstantValue> EvaluateConstants()
    {
        var constants = new Dictionary<string, ConstantValue>(StringComparer.Ordinal);
        var scope = new CompileScope(constants, _ => null, _ => null, []);
        // Included files' constants come first, so the pipeline's own can't be redeclared by them unnoticed.
        foreach (var constant in syntax.Included.SelectMany(i => i.Constants).Concat(syntax.Constants))
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
                    if (TryConstant(setting.Value, out var value))
                    {
                        if (PositiveWholeNumber(value) is { } size)
                            options = options with { BufferSize = size };
                        else
                            diagnostics.Error("buffer needs a whole number of messages, at least 1.", setting.Value.Span);
                    }
                    break;
                case "expires":
                    if (Lifetime(setting.Value, "expires = 5s") is { } lifetime)
                        options = options with { Expires = lifetime == Timeout.InfiniteTimeSpan ? null : lifetime };
                    break;
                case "on-error":
                    if (PolicyText(setting.Value, "on-error = log, stop-branch after 5 in 1s") is { } policy
                        && ErrorPolicyParser.Parse(policy.Text, policy.Start, forPipeline: true, diagnostics) is { } parts)
                    {
                        options = options with { ErrorPolicy = parts.ApplyTo(options.ErrorPolicy) };
                    }
                    break;
                case "keep-finished":
                    if (Lifetime(setting.Value, "keep-finished = 24h", allowZero: true) is { } keep)
                        options = options with { KeepFinished = keep == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : keep };
                    break;
                default:
                    diagnostics.Error($"There's no default called {setting.Name}." + Suggestions.For(setting.Name, ["buffer", "expires", "on-error", "keep-finished"]), setting.NameSpan);
                    break;
            }
        }
        return options;
    }

    private static int? PositiveWholeNumber(object? value) =>
        value is not null && Dynamic.TryConvert(value, typeof(int), out var whole) && whole is int size and > 0 ? size : null;

    // Works out a value that's the same for every message. Returns false, having reported why, if it can't.
    private bool TryConstant(ExpressionSyntax value, out object? result) => TryConstant(value, diagnostics, out result);

    private static bool TryConstant(ExpressionSyntax value, Diagnostics diagnostics, out object? result)
    {
        try
        {
            result = ExpressionCompiler.Evaluate(value, CompileScope.Empty).Value;
            return true;
        }
        catch (BindException exception)
        {
            diagnostics.Error(exception.Message, exception.Span);
            result = null;
            return false;
        }
    }

    // A duration, or none (Timeout.InfiniteTimeSpan): for expiry, none clears it; for keep-finished, it's zero.
    private TimeSpan? Lifetime(ExpressionSyntax value, string example, bool allowZero = false)
    {
        if (value is NameSyntax { Name: "none" })
            return Timeout.InfiniteTimeSpan;
        if (!TryConstant(value, out var result))
            return null;
        if (result is TimeSpan lifetime && (lifetime > TimeSpan.Zero || allowZero && lifetime == TimeSpan.Zero))
            return lifetime;
        diagnostics.Error(allowZero
            ? $"This needs a duration, as in {example}, or none."
            : $"Expiry needs a duration longer than zero, as in {example}, or none.", value.Span);
        return null;
    }

    // The text of an error policy, and where it starts, whether it's written as it is (under defaults) or in quotes.
    private (string Text, SourceSpan Start)? PolicyText(ExpressionSyntax value, string example)
    {
        if (!TryConstant(value, out var result))
            return null;
        if (result is not string text)
        {
            diagnostics.Error($"on-error needs an error policy, as in {example}.", value.Span);
            return null;
        }
        var written = syntax.TextOf(value.Span);
        var quoted = written.Length > 0 && written[0] is '"' or '\'';
        return (text, quoted ? value.Span with { Column = value.Span.Column + 1 } : value.Span);
    }

    private void CreateNodes(IReadOnlyList<ModuleSyntax> modules)
    {
        foreach (var module in modules)
        {
            if (Resolve(module) is { } registrations)
            {
                var node = new LoadNode(module, registrations);
                _nodes[module] = node;
                _ordered.Add(node);
            }
            CreateNodes(module.Children);
        }
    }

    // The inflections a module line could mean: all of a verb's, the one Verb.Inflection names, or an inflection
    // named on its own when no other verb has one by that name. Plugin.Verb and Plugin.Verb.Inflection look only
    // in that plugin (Q105).
    private IReadOnlyList<ModuleRegistration>? Resolve(ModuleSyntax module)
    {
        if (module.Plugin is { } plugin)
            return ResolveInPlugin(module, plugin, module.Verb, module.Inflection);

        if (registry.TryGetVerb(module.Verb, out var inflections) && inflections.Count > 0)
        {
            if (module.Inflection is null)
            {
                var shared = registry.SharedBy(module.Verb)
                    ?? inflections.Select(r => registry.SharedBy($"{r.Verb}.{r.Inflection}")).FirstOrDefault(plugins => plugins is not null);
                if (shared is not null)
                {
                    diagnostics.Error(SharedMessage(module.Verb, shared), module.VerbSpan);
                    return null;
                }
                return inflections;
            }
            var chosen = inflections.Where(r => r.Inflection == module.Inflection).ToList();
            if (chosen.Count == 1)
                return Unshared(module, chosen, $"{module.Verb}.{module.Inflection}");
            if (!registry.IsPlugin(module.Verb))
            {
                diagnostics.Error(
                    $"{module.Verb} has no inflection called {module.Inflection}." + Suggestions.For(module.Inflection, inflections.Select(r => r.Inflection)),
                    module.VerbSpan);
                return null;
            }
        }

        if (module.Inflection is { } second && registry.IsPlugin(module.Verb))
            return ResolveInPlugin(module, module.Verb, second, inflection: null);

        if (module.Inflection is null)
        {
            var named = registry.FindInflection(module.Verb);
            if (named.Count == 1)
                return Unshared(module, named, $"{named[0].Verb}.{module.Verb}");
            if (named.Count > 1)
            {
                var verbs = string.Join(" and ", named.Select(r => r.Verb).Distinct());
                diagnostics.Error($"{module.Verb} is an inflection of {verbs}. Write the one you mean, as in {named[0].Verb}.{module.Verb}.", module.VerbSpan);
                return null;
            }
        }

        diagnostics.Error($"There's no module called {module.Verb}." + Suggestions.For(module.Verb, registry.Verbs.Concat(registry.Inflections)), module.VerbSpan);
        return null;
    }

    // A plugin's own verb, inflection of a verb, or inflection named on its own.
    private IReadOnlyList<ModuleRegistration>? ResolveInPlugin(ModuleSyntax module, string plugin, string verb, string? inflection)
    {
        if (!registry.IsPlugin(plugin))
        {
            diagnostics.Error($"There's no plugin called {plugin}." + Suggestions.For(plugin, registry.Plugins), module.VerbSpan);
            return null;
        }
        if (registry.TryGetPluginVerb(plugin, verb, out var inflections))
        {
            if (inflection is null)
                return inflections;
            var chosen = inflections.Where(r => r.Inflection == inflection).ToList();
            if (chosen.Count == 1)
                return chosen;
            diagnostics.Error(
                $"{plugin}.{verb} has no inflection called {inflection}." + Suggestions.For(inflection, inflections.Select(r => r.Inflection)),
                module.VerbSpan);
            return null;
        }
        if (inflection is null)
        {
            var named = registry.FindPluginInflection(plugin, verb);
            if (named.Count == 1)
                return named;
            if (named.Count > 1)
            {
                diagnostics.Error($"{verb} is an inflection of more than one of {plugin}'s verbs. Write the one you mean, as in {plugin}.{named[0].Verb}.{verb}.", module.VerbSpan);
                return null;
            }
        }
        diagnostics.Error($"The plugin {plugin} has no module called {verb}." + Suggestions.For(verb, registry.PluginVerbs(plugin)), module.VerbSpan);
        return null;
    }

    // The inflections found, unless two plugins share them, so only the plugin's name can say which is meant.
    private IReadOnlyList<ModuleRegistration>? Unshared(ModuleSyntax module, IReadOnlyList<ModuleRegistration> found, string name)
    {
        if ((registry.SharedBy(name) ?? registry.SharedBy(found[0].Verb)) is not { } plugins)
            return found;
        diagnostics.Error(SharedMessage(name, plugins), module.VerbSpan);
        return null;
    }

    // "Send is in both Mail and Chat, so write Mail.Send or Chat.Send."
    private static string SharedMessage(string name, IReadOnlyList<string> plugins)
    {
        var listed = plugins.Count == 2
            ? $"both {plugins[0]} and {plugins[1]}"
            : string.Join(", ", plugins.Take(plugins.Count - 1)) + " and " + plugins[^1];
        var choices = plugins.Select(plugin => $"{plugin}.{name}").ToList();
        return $"{name} is in {listed}, so write {string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}.";
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

    /// <summary>
    /// The modules a pipeline runs, in the order it numbers them: with templates copied in and Distribute's branches
    /// copied. Mistakes are left for loading to report. Edits find modules by these numbers (Q129).
    /// </summary>
    internal static IReadOnlyList<ModuleSyntax> Running(PipelineSyntax syntax)
    {
        var ignored = new Diagnostics(syntax.Source.Path);
        return CopyDistributedBranches(TemplateExpander.Expand(syntax, ignored, _ => false), ignored);
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
                    if (module.Positional is null || !TryConstant(module.Positional, diagnostics, out var value) || PositiveWholeNumber(value) is not (>= 2 and var copies))
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

    // Fills in parameters for each inflection the line could mean. Inflections whose parameters records don't fit
    // the line are left out, and so are ones whose parameters refuse what the line asks (Q22).
    private void BindCandidates(LoadNode node, CompileScope scope)
    {
        var groups = node.Registrations.GroupBy(r => r.ParametersType).ToList();
        if (groups.Count == 1)
        {
            var bound = new ParameterBinder(scope, diagnostics, syntax).Bind(node.Syntax, groups[0].First());
            if (bound.Failed)
                return;
            foreach (var registration in groups[0])
                node.Candidates.Add(new Candidate(registration, bound));
        }
        else
        {
            var attempts = new List<Diagnostics>();
            foreach (var group in groups)
            {
                var problems = new Diagnostics(syntax.Source.Path);
                var bound = new ParameterBinder(scope, problems, syntax).Bind(node.Syntax, group.First());
                attempts.Add(problems);
                if (bound.Failed)
                    continue;
                foreach (var registration in group)
                    node.Candidates.Add(new Candidate(registration, bound));
            }
            if (node.Candidates.Count == 0)
            {
                // The line fits none of them: report the problems with the closest.
                foreach (var diagnostic in attempts.OrderBy(a => a.Items.Count(d => d.IsError)).First().Items)
                    diagnostics.Add(diagnostic);
                return;
            }
        }

        ChooseByParameters(node);
        if (node.Registration.IsSource && node.Candidates.Count > 1)
        {
            var names = string.Join(" and ", node.Candidates.Select(c => c.Registration.Inflection));
            diagnostics.Error(
                $"{node.Syntax.Verb} has several inflections that could do this: {names}. Name one, as in {node.Registration.Verb}.{node.Candidates[0].Registration.Inflection}.",
                node.Syntax.VerbSpan);
        }
    }

    // Asks inflections that parameters choose between whether they take this line's parameters. Parameters that are
    // the same for every message are checked now, so a mistake is reported when the pipeline loads. Ones worked out
    // per message are checked for each message.
    private void ChooseByParameters(LoadNode node)
    {
        var refusals = new List<string>();
        foreach (var candidate in node.Candidates.ToList())
        {
            if (candidate.Registration.WhyNot is not { } whyNot || candidate.Parameters.Value is not { } parameters)
                continue;
            if (candidate.Parameters.Computed.Count > 0)
            {
                candidate.WhyNot = message => whyNot(parameters, message);
                continue;
            }

            string? reason;
            try
            {
                reason = whyNot(parameters, StandIn.Instance);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reason = (exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message;
            }
            if (reason is not null)
            {
                refusals.Add($"{candidate.Registration.Inflection} {reason}");
                node.Candidates.Remove(candidate);
            }
        }

        if (node.Candidates.Count == 0)
        {
            diagnostics.Error(
                $"None of {node.Syntax.Verb}'s inflections can do this: {string.Join("; ", refusals)}.",
                node.Syntax.Positional?.Span ?? node.Syntax.VerbSpan);
        }
    }

    private NodeOptions BuildOptions(LoadNode node)
    {
        var options = NodeOptions.Default;
        foreach (var modifier in node.Syntax.Modifiers)
        {
            if (node.Registration.IsSource && modifier.Name is "buffer" or "bypass" or "errors" or "no-errors" or "accept-expired")
            {
                diagnostics.Error($"{node.Syntax.Verb} is a source, so @{modifier.Name} doesn't apply to it.", modifier.Span);
                continue;
            }
            switch (modifier.Name)
            {
                case "buffer":
                    if (modifier.Value is null)
                    {
                        diagnostics.Error("@buffer needs a whole number of messages, at least 1, as in @buffer=1000.", modifier.Span);
                    }
                    else if (TryConstant(modifier.Value, out var value))
                    {
                        if (PositiveWholeNumber(value) is { } size)
                            options = options with { BufferSize = size };
                        else
                            diagnostics.Error("@buffer needs a whole number of messages, at least 1, as in @buffer=1000.", modifier.Span);
                    }
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
                case "expires":
                    if (modifier.Value is null)
                        diagnostics.Error("@expires needs a duration, as in @expires=5s, or none.", modifier.Span);
                    else if (Lifetime(modifier.Value, "@expires=5s") is { } lifetime)
                        options = options with { Expires = lifetime };
                    break;
                case "accept-expired":
                    options = options with { AcceptExpired = true };
                    break;
                case "on-error":
                    if (modifier.Value is null)
                        diagnostics.Error("@on-error needs an error policy in quotes, as in @on-error=\"stop-branch after 5 in 1s\".", modifier.Span);
                    else if (PolicyText(modifier.Value, "@on-error=\"stop-branch after 5 in 1s\"") is { } policy)
                        node.OnError = ErrorPolicyParser.Parse(policy.Text, policy.Start, forPipeline: false, diagnostics);
                    break;
                default:
                    diagnostics.Error(
                        $"There's no modifier called @{modifier.Name}." + Suggestions.For(modifier.Name, ["buffer", "bypass", "errors", "no-errors", "expires", "accept-expired", "on-error"]),
                        modifier.Span);
                    break;
            }
        }

        // Messages an expression doesn't fit skip the module (Q69): a type goes in only if an inflection that takes
        // it has expressions that fit it.
        var candidates = node.Candidates;
        if (!node.Registration.IsSource && candidates.Any(c => c.Parameters.Computed.Count > 0))
        {
            options = options with
            {
                HandlesType = type => candidates.Any(c => c.InputType.IsAssignableFrom(type) && c.Parameters.Computed.All(computed => computed.Fits(type))),
            };
        }
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
                    node.Parent = parent;
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
            foreach (var (name, span) in node.References)
            {
                var named = _ordered.FirstOrDefault(n => n.Syntax.Name == name);
                if (named is not null && _ordered.Any(n => n.Syntax.CopyOf is not null && n.Syntax.Name == name))
                    diagnostics.Error($"{name} is in a branch Distribute copies, so there's more than one of it, and nothing can tell which to take from.", span);
                else if (named is not null)
                    node.Upstream.Add(named);
                else if (_ordered.Any(n => n.Syntax.Verb == name))
                    diagnostics.Error($"There's no module called {name}. Modules are found by name, so give the {name} you mean a name with 'as', and use that.", span);
                else
                    diagnostics.Error($"There's no module called {name}." + Suggestions.For(name, _ordered.Select(n => n.Syntax.Name).OfType<string>()), span);
            }
        }
    }

    // Works out each module's error policy: its own @on-error over the policy of the module it's indented under,
    // and at the left margin, the pipeline's.
    private void ApplyErrorPolicies(ErrorPolicy pipeline)
    {
        var policies = new Dictionary<LoadNode, ErrorPolicy>();
        foreach (var node in _ordered)
        {
            // A module comes after the module it's indented under, so its policy is already known.
            var basis = node.Parent is { } parent ? policies[parent] : pipeline;
            var policy = node.OnError?.ApplyTo(basis) ?? basis;
            policies[node] = policy;
            if (!ReferenceEquals(policy, pipeline))
                node.Options = node.Options with { ErrorPolicy = policy };
        }
    }

    // Works out which message types can reach each module, from what modules declare they emit.
    private void FlowTypes()
    {
        foreach (var node in _ordered.Where(n => n.Registration.IsSource))
            node.Outputs.UnionWith(Declared(node.Candidates[0]));

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var node in _ordered.Where(n => !n.Registration.IsSource))
            {
                node.Inputs.Add(typeof(ErrorMessage)); // any module upstream can fail
                foreach (var upstream in node.Upstream)
                    node.Inputs.UnionWith(upstream.Outputs);

                var before = node.Outputs.Count;
                foreach (var type in node.Inputs.ToList())
                {
                    var taking = Taking(node, type);
                    if (taking.Count == 0)
                    {
                        if (IsError(type) || node.Options.Bypass)
                            node.Outputs.Add(type); // goes past the module
                        continue;
                    }
                    if (type != s_unknown && !taking.Any(c => c.Parameters.Computed.All(computed => computed.Fits(type))))
                    {
                        node.Outputs.Add(type); // its expressions don't fit, so it skips the module
                        continue;
                    }
                    foreach (var candidate in Routed(taking, type))
                    {
                        node.Outputs.UnionWith(Declared(candidate));
                        if (candidate.Registration.EmitsInput || candidate.Registration.IsPassthrough)
                            node.Outputs.Add(type); // passed on
                    }
                }
                changed |= node.Outputs.Count != before;
            }
        }
    }

    private static IEnumerable<Type> Declared(Candidate candidate)
    {
        var registration = candidate.Registration;
        if (registration.Emits.Count > 0)
            return registration.EmitsWith(candidate.Parameters.Value);
        return registration.EmitsInput || registration.IsPassthrough ? [] : [s_unknown];
    }

    // The inflections that would take messages of this type, closest fit first, or none if the module's options keep
    // them out. Any of them might take a type nobody declared.
    private static List<Candidate> Taking(LoadNode node, Type type)
    {
        var wanted = node.Options.Errors switch
        {
            ErrorDelivery.Only => IsError(type),
            ErrorDelivery.Exclude => !IsError(type),
            _ => true,
        };
        if (!wanted)
            return [];
        if (type == s_unknown)
            return [.. node.Candidates];
        return [.. node.Candidates
            .Select(c => (Candidate: c, Rank: InflectionOrder.Rank(type, c.InputType)))
            .Where(c => c.Rank is not null)
            .OrderBy(c => c.Rank)
            .Select(c => c.Candidate)];
    }

    // The inflections a message of this type can go to: the closest fits, and if their parameters can refuse it, the
    // ones after them in turn, as the runtime tries them.
    private static List<Candidate> Routed(List<Candidate> taking, Type type)
    {
        if (type == s_unknown)
            return taking;
        var routed = new List<Candidate>();
        foreach (var tier in taking.GroupBy(c => InflectionOrder.Rank(type, c.InputType)))
        {
            routed.AddRange(tier);
            if (tier.Any(c => c.WhyNot is null))
                break;
        }
        return routed;
    }

    private static bool IsError(Type type) => typeof(ErrorMessage).IsAssignableFrom(type);

    private static string Friendly(Type type) => ExpressionBinder.FriendlyName(type);

    // Reports modules that could never receive anything, types that can reach a module that doesn't take them,
    // inflections that can't be chosen between, and expressions that fit none of the types that reach them.
    private void CheckTypes()
    {
        foreach (var node in _ordered.Where(n => !n.Registration.IsSource))
        {
            var verb = node.Syntax.Verb;
            var errorsOnly = node.Options.Errors == ErrorDelivery.Only;
            var arriving = node.Inputs.Where(t => t != s_unknown && (errorsOnly ? IsError(t) : !IsError(t))).ToList();
            var unknown = node.Inputs.Contains(s_unknown);
            var taken = arriving.Where(t => Taking(node, t).Count > 0).ToList();

            if (!errorsOnly && !unknown && arriving.Count > 0 && taken.Count == 0)
            {
                var input = string.Join(" or ", node.Candidates.Select(c => Friendly(c.InputType)).Distinct());
                var reaching = string.Join(" or ", arriving.Select(Friendly));
                diagnostics.Error($"{verb} takes {input}, but only {reaching} can reach it, so it would never receive anything.", node.Syntax.VerbSpan);
                continue;
            }

            // Q20: every type that can reach a module needs an inflection that takes it, unless @bypass lets it past.
            // Otherwise each one would become an error (Q54).
            if (!errorsOnly && !node.Options.Bypass)
            {
                foreach (var type in arriving.Except(taken))
                {
                    diagnostics.Error(
                        $"{Friendly(type)} can reach {verb}, but {verb} doesn't take it. Add @bypass to let it go past, or filter it out before {verb}.",
                        node.Syntax.VerbSpan);
                }
            }

            // Q20: inflections that fit a type equally well need parameters worked out per message to choose between
            // them. Otherwise the line has to name one.
            foreach (var type in taken)
            {
                var closest = Taking(node, type).GroupBy(c => InflectionOrder.Rank(type, c.InputType)).First().ToList();
                if (closest.Count > 1 && closest.Any(c => c.WhyNot is null))
                {
                    var names = string.Join(" and ", closest.Select(c => c.Registration.Inflection));
                    diagnostics.Error(
                        $"{verb} can't choose between {names} for {Friendly(type)}. Name one, as in {node.Registration.Verb}.{closest[0].Registration.Inflection}.",
                        node.Syntax.VerbSpan);
                }
            }

            // Expressions, checked once for each set of parameters the inflections share.
            foreach (var parameters in node.Candidates.Select(c => c.Parameters).Distinct())
            {
                var takenHere = taken
                    .Where(t => node.Candidates.Any(c => c.Parameters == parameters && c.InputType.IsAssignableFrom(t)))
                    .ToList();
                var errorsHere = node.Inputs
                    .Where(t => t != s_unknown && IsError(t) && node.Candidates.Any(c => c.Parameters == parameters && c.InputType.IsAssignableFrom(t)))
                    .ToList();
                foreach (var computed in parameters.Computed)
                {
                    var checkedAgainst = computed.ForErrors == true ? errorsHere : takenHere;
                    if (checkedAgainst.Count == 0)
                    {
                        if (unknown)
                        {
                            diagnostics.Warning(
                                $"Meeplang can't check {computed.Text} before the pipeline runs, because a module before {verb} doesn't say what it emits.",
                                node.Syntax.VerbSpan);
                        }
                        continue;
                    }
                    if (!checkedAgainst.Any(computed.AppliesTo) && computed.ProblemWith(checkedAgainst[0]) is var (problem, span))
                        diagnostics.Error(problem, span);
                }
            }
        }
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
        {
            if (node.Registration.IsSource)
            {
                var source = node.Candidates[0];
                infos[node] = builder.AddSource(
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
            else
            {
                var inflections = node.Candidates
                    .Select(c => new VerbInflection(c.Registration.Inflection, () => (AInflection)c.Registration.Create(c.Parameters.Value), c.WhyNot)
                    {
                        Parameters = c.Parameters.Value,
                        Reconfigure = c.Registration.Reconfigure,
                    })
                    .ToList();
                infos[node] = builder.AddVerb(node.Registration.Verb, inflections, node.Syntax.Name, node.Options);
            }
        }
        foreach (var node in _ordered)
        {
            foreach (var upstream in node.Upstream)
                builder.Connect(infos[upstream], infos[node]);
        }
        return new LoadedModules(
            builder,
            ReferenceEquals(_modules, syntax.Modules) ? syntax : syntax.WithModules(_modules),
            _ordered.ToDictionary(node => node.Syntax, node => infos[node]),
            _ordered.ToDictionary(node => node.Syntax, node => (IReadOnlyList<Type>)[.. node.Candidates.Select(c => c.Registration.ModuleType)]));
    }
}

/// <summary>A message to ask inflections about parameters with when the pipeline loads, before any message exists.</summary>
internal sealed record StandIn : Message
{
    public static StandIn Instance { get; } = new();
}
