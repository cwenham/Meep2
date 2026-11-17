using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Editing;

/// <summary>What changing a running pipeline's text did.</summary>
/// <param name="Applied">Whether the new text loaded and the running pipeline became it.</param>
/// <param name="Report">What happened to each module, if it was applied.</param>
/// <param name="Matches">Which modules of the new text were which of the old.</param>
/// <param name="Diagnostics">Problems with the new text, and warnings.</param>
public sealed record LiveChange(bool Applied, PipelineChangeReport? Report, IReadOnlyList<ModuleMatch> Matches, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether the pipeline's own settings, under <c>defaults</c>, changed.</summary>
    public bool SettingsChanged { get; init; }
}

/// <summary>
/// A pipeline loaded from Meeplang that can be changed while it runs (goal 4): give it new text, and the modules
/// that are the same in both carry on with their state, while the rest are added, removed or reconfigured. Which
/// modules are the same is worked out by <see cref="ModuleMatcher"/>.
/// </summary>
public sealed class LivePipeline
{
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly PipelineOptions _options;
    private State _state;

    private LivePipeline(Pipeline pipeline, State state, PipelineOptions options)
    {
        Pipeline = pipeline;
        _state = state;
        _options = options;
    }

    /// <summary>The running pipeline. A restart replaces it with a new one (<see cref="Rebuild"/>).</summary>
    public Pipeline Pipeline { get; private set; }

    /// <summary>The text the pipeline runs from now: the file as loaded, or as last changed.</summary>
    public PipelineSyntax Syntax => _state.Syntax;

    /// <summary>The verbs and message types the pipeline was last loaded with.</summary>
    public ModuleRegistry Registry => _state.Registry;

    /// <summary>What keeps the pipeline's plugins loaded now. A change that reloads plugins swaps these.</summary>
    public IReadOnlyList<IDisposable> Holds => _state.Holds;

    /// <summary>Loads a parsed file into a pipeline that can be changed while it runs. Null, with the problems, if it doesn't load.</summary>
    /// <param name="holds">What keeps the pipeline's plugins loaded; let go of when they're replaced, or by whoever hosts the pipeline at the end.</param>
    public static (LivePipeline? Pipeline, IReadOnlyList<Diagnostic> Diagnostics) Load(PipelineSyntax syntax, ModuleRegistry registry, PipelineOptions? options = null, IReadOnlyList<IDisposable>? holds = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(registry);
        options ??= PipelineOptions.Default;
        var (modules, diagnostics) = new PipelineLoader(registry).Define(syntax, options);
        if (modules is null)
            return (null, diagnostics);
        var (keys, nextKey) = Keys(modules, null, null, 1);
        var state = new State(syntax, registry, modules, Fingerprints(modules.Expanded, modules), holds ?? [], keys, nextKey);
        return (new LivePipeline(modules.Builder.Build(), state, options), diagnostics);
    }

    /// <summary>The module a module line is now, in the running pipeline.</summary>
    public ModuleInfo? InfoOf(ModuleSyntax module) => _state.Modules.Infos.GetValueOrDefault(module);

    /// <summary>The module line a module of the running pipeline came from.</summary>
    public ModuleSyntax? SyntaxOf(ModuleInfo module)
    {
        foreach (var (syntax, info) in _state.Modules.Infos)
        {
            if (info.Number == module.Number && info.Verb == module.Verb)
                return syntax;
        }
        return null;
    }

    /// <summary>
    /// Builds the pipeline again from the text it runs from now, as when its host restarts it after it failed
    /// (Q112). Edits made while it ran are kept.
    /// </summary>
    public Pipeline Rebuild()
    {
        var state = _state;
        var (modules, diagnostics) = new PipelineLoader(state.Registry).Define(state.Syntax, _options);
        if (modules is null)
            throw new InvalidOperationException($"{Pipeline.Name} didn't load again, so it can't be restarted: {string.Join("; ", diagnostics.Where(d => d.IsError))}");
        var (keys, nextKey) = Keys(modules, ModuleMatcher.Match(state.Modules.Expanded, modules.Expanded), state.Keys, state.NextKey);
        _state = state with { Modules = modules, Fingerprints = Fingerprints(modules.Expanded, modules), Keys = keys, NextKey = nextKey };
        return Pipeline = modules.Builder.Build();
    }

    /// <summary>
    /// Changes the running pipeline to new text. Modules found in both carry on; those whose parameters changed are
    /// offered the new ones, and replaced if they don't take them. Text that doesn't load leaves the pipeline as it was.
    /// </summary>
    /// <param name="next">The new text.</param>
    /// <param name="registry">The verbs to load it with, as after plugins were installed again, or null for the ones it has.</param>
    /// <param name="holds">What keeps that registry's plugins loaded. The old holds are let go once the change is made.</param>
    public async Task<LiveChange> ChangeAsync(PipelineSyntax next, ModuleRegistry? registry = null, IReadOnlyList<IDisposable>? holds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            var (modules, diagnostics) = new PipelineLoader(registry ?? state.Registry).Define(next, _options);
            var matches = ModuleMatcher.Match(state.Modules.Expanded, modules?.Expanded ?? next);
            if (modules is null)
            {
                Release(holds);
                return new LiveChange(false, null, matches, diagnostics);
            }

            var fingerprints = Fingerprints(modules.Expanded, modules);
            var change = new PipelineChange(modules.Builder);
            foreach (var match in matches)
            {
                if (match is { Old: { } old, Next: { } kept }
                    && state.Modules.Infos.TryGetValue(old, out var running)
                    && modules.Infos.TryGetValue(kept, out var becomes))
                {
                    change.Keep(running, becomes, parametersChanged: state.Fingerprints[old] != fingerprints[kept]);
                }
            }
            PipelineChangeReport report;
            try
            {
                report = await Pipeline.ApplyAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Release(holds);
                throw;
            }

            var previous = state.Holds;
            var (keys, nextKey) = Keys(modules, matches, state.Keys, state.NextKey);
            _state = new State(next, registry ?? state.Registry, modules, fingerprints, holds ?? state.Holds, keys, nextKey);
            if (holds is not null)
                Release(previous);
            return new LiveChange(true, report, matches, diagnostics) { SettingsChanged = Settings(state.Syntax) != Settings(next) };
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Changes the running pipeline to new text, parsed from a string.</summary>
    public Task<LiveChange> ChangeAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ChangeAsync(MeeplangParser.Parse(new SourceFile(Syntax.Source.Path, text)), cancellationToken: cancellationToken);
    }

    // What a module is made from: its parameters as written, the constants they might use, and the classes that make
    // it, which differ when a plugin is reloaded. A module whose fingerprint changes gets new parameters.
    private static Dictionary<ModuleSyntax, string> Fingerprints(PipelineSyntax syntax, LoadedModules modules)
    {
        var constants = string.Join(';', syntax.Constants.Concat(syntax.Included.SelectMany(i => i.Constants)).Select(c => c.Name + "=" + ExpressionText.Of(c.Value)));
        return modules.Infos.Keys.ToDictionary(
            module => module,
            module => $"{ModuleMatcher.ParametersOf(syntax, module)}|{constants}|{string.Join(',', modules.Types[module].Select(t => t.AssemblyQualifiedName + "@" + t.Assembly.GetHashCode()))}");
    }

    /// <summary>
    /// The pipeline as a graph: its modules, each with a key that lasts while the pipeline is changed and restarted,
    /// and the links between them, with the message types each can carry.
    /// </summary>
    public PipelineShape Shape()
    {
        var state = _state;
        return PipelineShape.Of(state.Syntax, state.Modules, state.Keys);
    }

    /// <summary>
    /// The key of each module of the running pipeline (<see cref="ModuleShape.Key"/>), by the identity the pipeline
    /// gives it now, as <see cref="Pipeline.Modules"/> and <see cref="Pipeline.GetLinkStats"/> have it. Cheaper than
    /// <see cref="Shape"/>, for putting keys on counts.
    /// </summary>
    public IReadOnlyDictionary<ModuleInfo, string> ModuleKeys()
    {
        var state = _state;
        var keys = new Dictionary<ModuleInfo, string>(state.Modules.Infos.Count, ReferenceEqualityComparer.Instance);
        foreach (var (syntax, info) in state.Modules.Infos)
        {
            if (state.Keys.TryGetValue(syntax, out var key))
                keys[info] = key;
        }
        return keys;
    }

    // Each module's key: the key of the module it was matched with before the change, or a new one, never used
    // before in this pipeline. A pipeline's first keys are m1, m2 and so on, in the order it numbers its modules.
    internal static (IReadOnlyDictionary<ModuleSyntax, string> Keys, int Next) Keys(
        LoadedModules modules, IReadOnlyList<ModuleMatch>? matches, IReadOnlyDictionary<ModuleSyntax, string>? old, int next)
    {
        var keys = new Dictionary<ModuleSyntax, string>(ReferenceEqualityComparer.Instance);
        foreach (var match in matches ?? [])
        {
            if (match is { Old: { } before, Next: { } after } && old!.TryGetValue(before, out var key) && modules.Infos.ContainsKey(after))
                keys[after] = key;
        }
        foreach (var module in modules.Modules)
        {
            if (!keys.ContainsKey(module.Syntax))
                keys[module.Syntax] = $"m{next++}";
        }
        return (keys, next);
    }

    private static string Settings(PipelineSyntax syntax) =>
        string.Join(';', syntax.Defaults.Select(d => d.Name + "=" + ExpressionText.Of(d.Value)));

    private static void Release(IReadOnlyList<IDisposable>? holds)
    {
        foreach (var hold in holds ?? [])
            hold.Dispose();
    }

    private sealed record State(
        PipelineSyntax Syntax,
        ModuleRegistry Registry,
        LoadedModules Modules,
        IReadOnlyDictionary<ModuleSyntax, string> Fingerprints,
        IReadOnlyList<IDisposable> Holds,
        IReadOnlyDictionary<ModuleSyntax, string> Keys,
        int NextKey);
}
