using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Connects a pipeline's modules: by indentation, by + lines, and by the modules their parameters name; and warns where
/// a module can emit more at once than one after it can hold.
/// </summary>
/// <param name="nodes">Each module line's node.</param>
/// <param name="ordered">The nodes in the order the pipeline numbers them.</param>
/// <param name="diagnostics">Where mistakes are reported.</param>
internal sealed class Wiring(IReadOnlyDictionary<ModuleSyntax, LoadNode> nodes, IReadOnlyList<LoadNode> ordered, Diagnostics diagnostics)
{
    /// <summary>
    /// Connects modules by indentation. Each module feeds the modules indented under it. A run of + lines feeds the
    /// next unmarked line at the same indentation, from every end of each + line's chain.
    /// </summary>
    public void Wire(IReadOnlyList<ModuleSyntax> modules) => Wire(parent: null, modules);

    /// <summary>Connects each module to the modules its parameters name, which are found by their as names only.</summary>
    public void WireReferences()
    {
        foreach (var node in ordered)
        {
            // A module is found by its as name only, so adding another module with the same verb can't change
            // what an existing reference finds (Q83).
            foreach (var (name, span) in node.References)
            {
                var named = ordered.FirstOrDefault(n => n.Syntax.Name == name);
                if (named is not null && ordered.Any(n => n.Syntax.CopyOf is not null && n.Syntax.Name == name))
                    diagnostics.Error($"{name} is in a branch Distribute copies, so there's more than one of it, and nothing can tell which to take from.", span);
                else if (named is not null)
                    node.Upstream.Add(named);
                else if (ordered.Any(n => n.Syntax.Verb == name))
                    diagnostics.Error($"There's no module called {name}. Modules are found by name, so give the {name} you mean a name with 'as', and use that.", span);
                else
                    diagnostics.Error($"There's no module called {name}." + Suggestions.For(name, ordered.Select(n => n.Syntax.Name).OfType<string>()), span);
            }
        }
    }

    /// <summary>
    /// Warns about a module that can emit more messages at once than a module after it can hold, since emitting never
    /// waits and the earliest would be dropped (Q169). A passthrough module passes them straight on, so the modules
    /// after it need room for them too.
    /// </summary>
    public void CheckBuffers(int pipelineBuffer)
    {
        foreach (var node in ordered)
        {
            var most = node.Candidates
                .Select(c => c.Registration.MostAtOnce is { } count && c.Parameters.Value is { } value ? count(value) : null)
                .Max();
            if (most is { } count)
                CheckBuffersAfter(node, count, pipelineBuffer);
        }
    }

    private void CheckBuffersAfter(LoadNode node, long count, int pipelineBuffer)
    {
        var seen = new HashSet<LoadNode>();
        var next = new Queue<LoadNode>(Downstream(node));
        while (next.TryDequeue(out var after))
        {
            if (!seen.Add(after))
                continue;
            var buffer = after.Options.BufferSize ?? pipelineBuffer;
            if (count > buffer)
            {
                diagnostics.Warning(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{node.Syntax.Verb} can emit {count:N0} messages at once, but {after.Label} only holds {buffer:N0}, so the earliest would be dropped. Give {after.Label} @buffer={count}, or raise buffer under defaults."),
                    node.Syntax.VerbSpan);
            }
            if (after.Registration.IsPassthrough)
            {
                foreach (var further in Downstream(after))
                    next.Enqueue(further);
            }
        }
    }

    private IEnumerable<LoadNode> Downstream(LoadNode node) => ordered.Where(n => n.Upstream.Contains(node));

    private void Wire(LoadNode? parent, IReadOnlyList<ModuleSyntax> siblings)
    {
        var feeders = new List<LoadNode>();
        foreach (var module in siblings)
        {
            if (!nodes.TryGetValue(module, out var node))
                continue;
            if (module.IsFeeder)
            {
                if (!node.HasOwnInput)
                    diagnostics.Error($"A + line starts a chain of its own, so it needs a source or a Tap, not {module.Verb}.", module.VerbSpan);
                feeders.Add(node);
            }
            else
            {
                Attach(node, parent, fed: feeders.Count > 0);
                foreach (var feeder in feeders)
                {
                    foreach (var end in Ends(feeder.Syntax))
                        node.Upstream.Add(nodes[end]);
                }
                feeders.Clear();
            }
            Wire(node, module.Children);
        }
    }

    // Connects a module to the one it's indented under, or says why it can't be there, or why nothing feeds it.
    private void Attach(LoadNode node, LoadNode? parent, bool fed)
    {
        var module = node.Syntax;
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
        else if (!node.HasOwnInput && !fed)
        {
            diagnostics.Error($"Nothing feeds {module.Verb}. Indent it under a source, or feed it with + lines above it.", module.VerbSpan);
        }
        else if (node.HasOwnInput && fed)
        {
            diagnostics.Error($"{module.Verb} is a source, so + lines can't feed it.", module.VerbSpan);
        }
    }

    // The ends of a chain: modules with nothing indented under them. + lines inside the chain end at the module
    // they feed, so they're not ends of their own.
    private IEnumerable<ModuleSyntax> Ends(ModuleSyntax module)
    {
        if (!nodes.ContainsKey(module))
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
}
