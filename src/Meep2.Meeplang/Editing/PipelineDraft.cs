using System.Globalization;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;

namespace Meep2.Meeplang.Editing;

/// <summary>What a pipeline's text came to, as <see cref="PipelineDraft.Update"/> found it.</summary>
/// <param name="Loaded">Whether the text loaded, so <paramref name="Shape"/> is this text's.</param>
/// <param name="Shape">The shape of the text as it is, if it loaded, or of the last text that did, or null if none has.</param>
/// <param name="Diagnostics">Problems with the text, and warnings.</param>
/// <param name="Renamed">
/// Modules whose key changed although they're the same module: the old key, then the new. Empty unless a base was set
/// or changed (<see cref="PipelineDraft.SetBase"/>), or the text moved a module so it now matches one of the base's.
/// </param>
public sealed record DraftUpdate(bool Loaded, PipelineShape? Shape, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> Renamed);

/// <summary>
/// A pipeline's text as it's edited, as a graph, without running it, for an editor that draws it (<c>meep lsp</c>'s
/// <c>meep/graph</c>). Each module has a key, as a running pipeline's do (<see cref="ModuleShape.Key"/>), which lasts
/// while the text changes: each new text's modules are matched with the last text's by <see cref="ModuleMatcher"/>, as a
/// reload's are.
/// </summary>
/// <remarks>
/// <para>
/// When the text is also running, give the running pipeline's text and keys as the base (<see cref="SetBase"/>). Then
/// the modules that are in the base are matched with the base's instead, the way the running pipeline will match them
/// when the text is saved and reloaded, so each has the key it will have there. Modules that aren't in the base are
/// still matched with the last text's, so a module being written keeps its key while it's written; it may get another
/// when the running pipeline reloads it and the base is set again, which <see cref="DraftUpdate.Renamed"/> reports.
/// </para>
/// <para>Text that doesn't load leaves the last shape that did, so a drawing of it can stay while the text is mended.</para>
/// </remarks>
public sealed class PipelineDraft
{
    private Keyed? _last;
    private Keyed? _base;

    /// <summary>The shape of the last text that loaded, or null if none has.</summary>
    public PipelineShape? Shape => _last?.Shape;

    /// <summary>Whether a base is set.</summary>
    public bool HasBase => _base is not null;

    /// <summary>Works out the shape of new text, giving each module its key. Text that doesn't load changes nothing.</summary>
    /// <param name="syntax">The text, parsed.</param>
    /// <param name="registry">The verbs to load it with.</param>
    public DraftUpdate Update(PipelineSyntax syntax, ModuleRegistry registry)
    {
        var (update, next) = Try(syntax, registry);
        if (next is not null)
            _last = next;
        return update;
    }

    /// <summary>
    /// Works out the shape new text would have, with the keys <see cref="Update"/> would give each module if it were
    /// given this text now, without changing anything. For showing an edit before it's made, and for knowing the key
    /// a module an edit adds will have.
    /// </summary>
    /// <param name="syntax">The text, parsed.</param>
    /// <param name="registry">The verbs to load it with.</param>
    public DraftUpdate Preview(PipelineSyntax syntax, ModuleRegistry registry) => Try(syntax, registry).Update;

    private (DraftUpdate Update, Keyed? Next) Try(PipelineSyntax syntax, ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(registry);
        var (modules, diagnostics) = new PipelineLoader(registry).Define(syntax);
        if (modules is null)
            return (new DraftUpdate(false, _last?.Shape, diagnostics, s_none), null);
        var (next, renamed) = Key(syntax, modules);
        return (new DraftUpdate(true, next.Shape, diagnostics, renamed), next);
    }

    /// <summary>
    /// Sets the running pipeline's text and keys as the base to match against, and works out the last text's keys
    /// again. Null, with the reason, if the base's text doesn't load, or doesn't have the modules
    /// <paramref name="keyOf"/> describes.
    /// </summary>
    /// <param name="syntax">The text the pipeline runs from now.</param>
    /// <param name="registry">The verbs to load it with.</param>
    /// <param name="keyOf">
    /// The running pipeline's key for each of its modules, by the module's number and verb, or null if it has no such
    /// module, as when the text isn't the one it runs.
    /// </param>
    public (DraftUpdate? Update, string? Problem) SetBase(PipelineSyntax syntax, ModuleRegistry registry, Func<int, string, string?> keyOf)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(keyOf);
        var (modules, diagnostics) = new PipelineLoader(registry).Define(syntax);
        if (modules is null)
            return (null, "The running pipeline's text doesn't load here: " + string.Join("; ", diagnostics.Where(d => d.IsError).Select(d => d.ToString())));
        var keys = new Dictionary<ModuleSyntax, string>(ReferenceEqualityComparer.Instance);
        var next = 1;
        foreach (var module in modules.Modules)
        {
            if (keyOf(module.Info.Number, module.Info.Verb) is not { } key)
                return (null, $"The running pipeline has no module {module.Info.Number} that's a {module.Info.Verb}, so its graph isn't of this text.");
            keys[module.Syntax] = key;
            next = Math.Max(next, NumberOf(key) + 1);
        }
        _base = new Keyed(PipelineShape.Of(syntax, modules, keys), modules, keys, next);
        if (_last is null)
            return (new DraftUpdate(false, null, [], s_none), null);
        var (keyed, renamed) = Key(_last.Shape.Syntax, _last.Loaded);
        _last = keyed;
        return (new DraftUpdate(true, keyed.Shape, [], renamed), null);
    }

    /// <summary>Stops matching against a base. The keys stay as they are.</summary>
    public void ClearBase() => _base = null;

    // Each module's key: the base's key for one the base has, then the last text's key for one it had (if the base
    // doesn't use it), then a new one. With no base, it's what LivePipeline does on a reload.
    private (Keyed Next, IReadOnlyDictionary<string, string> Renamed) Key(PipelineSyntax syntax, LoadedModules modules)
    {
        var keys = new Dictionary<ModuleSyntax, string>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (_base is { } baseline)
        {
            foreach (var match in ModuleMatcher.Match(baseline.Expanded, modules.Expanded))
            {
                if (match is { Old: { } before, Next: { } after } && baseline.Keys.TryGetValue(before, out var key) && modules.Infos.ContainsKey(after))
                {
                    keys[after] = key;
                    used.Add(key);
                }
            }
        }
        var sinceLast = _last is { } last ? ModuleMatcher.Match(last.Expanded, modules.Expanded) : [];
        var baseKeys = _base?.Keys.Values.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var match in sinceLast)
        {
            if (match is { Old: { } before, Next: { } after } && _last!.Keys.TryGetValue(before, out var key) && modules.Infos.ContainsKey(after)
                && !keys.ContainsKey(after) && !baseKeys.Contains(key) && used.Add(key))
            {
                keys[after] = key;
            }
        }
        var next = Math.Max(_base?.NextKey ?? 1, _last?.NextKey ?? 1);
        foreach (var module in modules.Modules)
        {
            if (!keys.ContainsKey(module.Syntax))
            {
                while (!used.Add($"m{next}"))
                    next++;
                keys[module.Syntax] = $"m{next++}";
            }
        }

        // The same module as one of the last text's, with another key: the base gave it one, or the base took its key.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var match in sinceLast)
        {
            if (match is { Old: { } before, Next: { } after } && _last!.Keys.TryGetValue(before, out var old)
                && keys.TryGetValue(after, out var now) && old != now)
            {
                renamed[old] = now;
            }
        }
        return (new Keyed(PipelineShape.Of(syntax, modules, keys), modules, keys, next), renamed);
    }

    private static int NumberOf(string key) =>
        key.Length > 1 && key[0] == 'm' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static readonly IReadOnlyDictionary<string, string> s_none = new Dictionary<string, string>();

    private sealed record Keyed(PipelineShape Shape, LoadedModules Loaded, IReadOnlyDictionary<ModuleSyntax, string> Keys, int NextKey)
    {
        public PipelineSyntax Expanded => Loaded.Expanded;
    }
}
