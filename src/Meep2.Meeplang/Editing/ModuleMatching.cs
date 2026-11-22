using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>
/// One matching of two versions of a pipeline's modules (<see cref="ModuleMatcher.Match"/>): the passes, in order,
/// each pairing modules the passes before it left.
/// </summary>
internal sealed class ModuleMatching
{
    private readonly List<Entry> _before;
    private readonly List<Entry> _after;
    private readonly Dictionary<Entry, Pairing> _matched = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Entry> _taken = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Entry> _named;

    public ModuleMatching(PipelineSyntax old, PipelineSyntax next)
    {
        _before = Flatten(old);
        _after = Flatten(next);
        _named = _before.Where(e => e.Module.Name is not null).ToDictionary(e => e.Module.Name!, StringComparer.Ordinal);
    }

    /// <summary>Each module of the new text, with the old one it is, if any, then the old ones left over.</summary>
    public IReadOnlyList<ModuleMatch> Run()
    {
        PairByName();

        // Unchanged: the same verb, name and parameters, under the same module, top to bottom so a module's parent is
        // paired before it. Done before structure, which takes the first same-verb sibling it meets, so a module
        // written above an unchanged one with the same verb can't take the unchanged one's place (Q186).
        PairUnchanged(underSameModule: true);

        // Unchanged, wherever it is now: the same verb, name and parameters, under any module, the nearest in order.
        // After those under the same module, so a module that stayed where it was keeps its place first; before
        // structure, so a module that an insert pushed a level down (under a module with its verb, or above one) keeps
        // its key and state, rather than the new module above it taking them (Q195). Reported as a structural match,
        // as the pass before it is, and as moved.
        PairUnchanged(underSameModule: false);

        // Structure, then position, in turns: a module matched by position lets those under it match by structure.
        while (MatchByStructure() || MatchOneByPosition())
        {
        }
        return Results();
    }

    private void Pair(Entry nextEntry, Entry oldEntry, MatchedBy by)
    {
        _matched[nextEntry] = new Pairing(oldEntry, by);
        _taken.Add(oldEntry);
    }

    // The old module the one an entry is under was paired with: null at the left margin, or if its parent isn't paired.
    private Entry? ParentWas(Entry entry) =>
        entry.Parent is not null && _matched.TryGetValue(entry.Parent, out var parent) ? parent.Old : null;

    // Name: the same name and the same verb.
    private void PairByName()
    {
        foreach (var entry in _after)
        {
            if (entry.Module.Name is { } name && _named.TryGetValue(name, out var same) && same.Key == entry.Key)
                Pair(entry, same, MatchedBy.Name);
        }
    }

    // Pairs each module left with an old one of the same verb, name and parameters, the nearest in order, and, when
    // asked, only under the module its parent was paired with.
    private void PairUnchanged(bool underSameModule)
    {
        foreach (var entry in _after)
        {
            if (_matched.ContainsKey(entry))
                continue;
            Entry? parentWas = null;
            if (underSameModule)
            {
                parentWas = ParentWas(entry);
                if (entry.Parent is not null && parentWas is null)
                    continue;
            }
            var same = _before
                .Where(candidate => !_taken.Contains(candidate) && candidate.Key == entry.Key && candidate.Parameters == entry.Parameters
                    && candidate.Module.Name == entry.Module.Name && (!underSameModule || ReferenceEquals(candidate.Parent, parentWas)))
                .OrderBy(candidate => Math.Abs(candidate.Order - entry.Order))
                .FirstOrDefault();
            if (same is not null)
                Pair(entry, same, MatchedBy.Structure);
        }
    }

    // Structure: the same verb under the same module, top to bottom, so a module's parent is matched before it.
    private bool MatchByStructure()
    {
        var progress = false;
        foreach (var entry in _after)
        {
            if (_matched.ContainsKey(entry))
                continue;
            var parentWas = ParentWas(entry);
            if (entry.Parent is not null && parentWas is null)
                continue; // its parent isn't matched (yet)
            var best = _before
                .Where(candidate => !_taken.Contains(candidate) && candidate.Key == entry.Key && ReferenceEquals(candidate.Parent, parentWas))
                .Where(candidate => candidate.Module.Name is null || entry.Module.Name is null || !_named.ContainsKey(entry.Module.Name) || candidate.Module.Name == entry.Module.Name)
                .OrderByDescending(candidate => Similarity(candidate, entry))
                .ThenBy(candidate => Math.Abs(candidate.Order - entry.Order))
                .FirstOrDefault();
            if (best is not null)
            {
                Pair(entry, best, MatchedBy.Structure);
                progress = true;
            }
        }
        return progress;
    }

    // Position: the first module left, top to bottom, takes the module in the same place among those left with its
    // verb.
    private bool MatchOneByPosition()
    {
        foreach (var entry in _after)
        {
            if (_matched.ContainsKey(entry))
                continue;
            var rank = _after.Where(e => !_matched.ContainsKey(e) && e.Key == entry.Key).ToList().IndexOf(entry);
            var left = _before.Where(e => !_taken.Contains(e) && e.Key == entry.Key).ToList();
            if (rank < left.Count)
            {
                Pair(entry, left[rank], MatchedBy.Position);
                return true;
            }
        }
        return false;
    }

    private List<ModuleMatch> Results()
    {
        var results = new List<ModuleMatch>();
        foreach (var entry in _after)
        {
            if (_matched.TryGetValue(entry, out var pair))
            {
                var parentWas = ParentWas(entry);
                results.Add(new ModuleMatch(pair.Old.Module, entry.Module, pair.By)
                {
                    ParametersChanged = pair.Old.Parameters != entry.Parameters,
                    Moved = !ReferenceEquals(pair.Old.Parent, parentWas) || (pair.Old.Parent is null) != (entry.Parent is null),
                });
            }
            else
            {
                results.Add(new ModuleMatch(null, entry.Module, MatchedBy.Nothing));
            }
        }
        foreach (var entry in _before.Where(e => !_taken.Contains(e)))
            results.Add(new ModuleMatch(entry.Module, null, MatchedBy.Nothing));
        return results;
    }

    // How alike two modules' parameters are: the same text is best, then the number of values they share.
    private static int Similarity(Entry a, Entry b)
    {
        if (a.Parameters == b.Parameters)
            return int.MaxValue;
        var shared = a.Values.Intersect(b.Values, StringComparer.Ordinal).Count();
        return shared;
    }

    private static List<Entry> Flatten(PipelineSyntax syntax)
    {
        var entries = new List<Entry>();
        void Walk(IReadOnlyList<ModuleSyntax> modules, Entry? parent)
        {
            foreach (var module in modules)
            {
                var values = new List<string>();
                if (module.Positional is { } positional)
                    values.Add("=" + ExpressionText.Of(positional));
                values.AddRange(module.Arguments.Select(a => a.Name + "=" + ExpressionText.Of(a.Value)));
                var entry = new Entry(module, parent, entries.Count, $"{module.Plugin}.{module.Verb}.{module.Inflection}", ModuleMatcher.ParametersOf(syntax, module), values);
                entries.Add(entry);
                Walk(module.Children, entry);
            }
        }
        Walk(syntax.Modules, null);
        return entries;
    }

    private sealed record Entry(ModuleSyntax Module, Entry? Parent, int Order, string Key, string Parameters, IReadOnlyList<string> Values);

    private sealed record Pairing(Entry Old, MatchedBy By);
}
