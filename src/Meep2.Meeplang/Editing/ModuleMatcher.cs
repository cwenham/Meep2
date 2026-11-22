using System.Text;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>
/// Works out which modules of a pipeline's new text are which of its old text, without IDs in the file (Q30, Q61):
/// by name first, then those whose verb, name and parameters are unchanged under the same module, then those unchanged
/// anywhere else, then by structure (the same verb, under the same module, with the most similar parameters), then by
/// position among the modules with the same verb. A module whose verb changed is a new module.
/// </summary>
/// <remarks>
/// Structure is what keeps the commonest edit right, inserting a module mid-chain: matching by position alone would
/// shift every module after the insertion by one, and give each one's state to the wrong module.
/// </remarks>
public static class ModuleMatcher
{
    /// <summary>Matches the modules of two versions of a pipeline file.</summary>
    public static IReadOnlyList<ModuleMatch> Match(PipelineSyntax old, PipelineSyntax next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        var before = Flatten(old);
        var after = Flatten(next);
        var matched = new Dictionary<Entry, (Entry Old, MatchedBy By)>(ReferenceEqualityComparer.Instance);
        var taken = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
        void Pair(Entry nextEntry, Entry oldEntry, MatchedBy by)
        {
            matched[nextEntry] = (oldEntry, by);
            taken.Add(oldEntry);
        }

        // Name: the same name and the same verb.
        var named = before.Where(e => e.Module.Name is not null).ToDictionary(e => e.Module.Name!, StringComparer.Ordinal);
        foreach (var entry in after)
        {
            if (entry.Module.Name is { } name && named.TryGetValue(name, out var same) && same.Key == entry.Key)
                Pair(entry, same, MatchedBy.Name);
        }

        // Unchanged: the same verb, name and parameters, under the same module, top to bottom so a module's parent is
        // paired before it. Done before structure, which takes the first same-verb sibling it meets, so a module
        // written above an unchanged one with the same verb can't take the unchanged one's place (Q186).
        foreach (var entry in after)
        {
            if (matched.ContainsKey(entry))
                continue;
            Entry? parentWas = entry.Parent is null ? null : matched.TryGetValue(entry.Parent, out var parent) ? parent.Old : null;
            if (entry.Parent is not null && parentWas is null)
                continue;
            var same = before
                .Where(candidate => !taken.Contains(candidate) && candidate.Key == entry.Key && candidate.Parameters == entry.Parameters
                    && candidate.Module.Name == entry.Module.Name && ReferenceEquals(candidate.Parent, parentWas))
                .OrderBy(candidate => Math.Abs(candidate.Order - entry.Order))
                .FirstOrDefault();
            if (same is not null)
                Pair(entry, same, MatchedBy.Structure);
        }

        // Unchanged, wherever it is now: the same verb, name and parameters, under any module, the nearest in order.
        // After those under the same module, so a module that stayed where it was keeps its place first; before
        // structure, so a module that an insert pushed a level down (under a module with its verb, or above one) keeps
        // its key and state, rather than the new module above it taking them (Q195). Reported as a structural match,
        // as the pass before it is, and as moved.
        foreach (var entry in after)
        {
            if (matched.ContainsKey(entry))
                continue;
            var same = before
                .Where(candidate => !taken.Contains(candidate) && candidate.Key == entry.Key && candidate.Parameters == entry.Parameters
                    && candidate.Module.Name == entry.Module.Name)
                .OrderBy(candidate => Math.Abs(candidate.Order - entry.Order))
                .FirstOrDefault();
            if (same is not null)
                Pair(entry, same, MatchedBy.Structure);
        }

        // Structure, then position, in turns: a module matched by position lets those under it match by structure.
        while (MatchByStructure() || MatchOneByPosition())
        {
        }

        // Structure: the same verb under the same module, top to bottom, so a module's parent is matched before it.
        bool MatchByStructure()
        {
            var progress = false;
            foreach (var entry in after)
            {
                if (matched.ContainsKey(entry))
                    continue;
                Entry? parentWas = entry.Parent is null ? null : matched.TryGetValue(entry.Parent, out var parent) ? parent.Old : null;
                if (entry.Parent is not null && parentWas is null)
                    continue; // its parent isn't matched (yet)
                var best = before
                    .Where(candidate => !taken.Contains(candidate) && candidate.Key == entry.Key && ReferenceEquals(candidate.Parent, parentWas))
                    .Where(candidate => candidate.Module.Name is null || entry.Module.Name is null || !named.ContainsKey(entry.Module.Name) || candidate.Module.Name == entry.Module.Name)
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

        // Position: the first module left, top to bottom, takes the module in the same place among those left with
        // its verb.
        bool MatchOneByPosition()
        {
            foreach (var entry in after)
            {
                if (matched.ContainsKey(entry))
                    continue;
                var rank = after.Where(e => !matched.ContainsKey(e) && e.Key == entry.Key).ToList().IndexOf(entry);
                var left = before.Where(e => !taken.Contains(e) && e.Key == entry.Key).ToList();
                if (rank < left.Count)
                {
                    Pair(entry, left[rank], MatchedBy.Position);
                    return true;
                }
            }
            return false;
        }

        var results = new List<ModuleMatch>();
        foreach (var entry in after)
        {
            if (matched.TryGetValue(entry, out var pair))
            {
                var parentWas = entry.Parent is not null && matched.TryGetValue(entry.Parent, out var parent) ? parent.Old : null;
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
        foreach (var entry in before.Where(e => !taken.Contains(e)))
            results.Add(new ModuleMatch(entry.Module, null, MatchedBy.Nothing));
        return results;
    }

    /// <summary>
    /// A module's parameters: its verb, main value and named parameters, but not its name. They're written from the
    /// syntax, so a template's copy shows its parameters' values.
    /// </summary>
    public static string ParametersOf(PipelineSyntax syntax, ModuleSyntax module)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(module);
        var text = new StringBuilder();
        text.Append(module.Plugin).Append('.').Append(module.Verb).Append('.').Append(module.Inflection);
        if (module.Positional is { } positional)
            text.Append(' ').Append(ExpressionText.Of(positional));
        foreach (var argument in module.Arguments)
            text.Append(' ').Append(argument.Name).Append('=').Append(ExpressionText.Of(argument.Value));
        return text.ToString();
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
                var entry = new Entry(module, parent, entries.Count, $"{module.Plugin}.{module.Verb}.{module.Inflection}", ParametersOf(syntax, module), values);
                entries.Add(entry);
                Walk(module.Children, entry);
            }
        }
        Walk(syntax.Modules, null);
        return entries;
    }

    private sealed record Entry(ModuleSyntax Module, Entry? Parent, int Order, string Key, string Parameters, IReadOnlyList<string> Values);
}
