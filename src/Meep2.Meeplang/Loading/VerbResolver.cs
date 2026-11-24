using System.Reflection;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Works out which inflections a module line could mean, from its verb, inflection and plugin, and then which of them
/// its parameters fit (Q22, Q105).
/// </summary>
/// <param name="registry">The verbs the pipeline can use.</param>
/// <param name="syntax">The pipeline.</param>
/// <param name="diagnostics">Where mistakes are reported.</param>
internal sealed class VerbResolver(ModuleRegistry registry, PipelineSyntax syntax, Diagnostics diagnostics)
{
    /// <summary>"Send is in both Mail and Chat, so write Mail.Send or Chat.Send."</summary>
    public static string SharedMessage(string name, IReadOnlyList<string> plugins)
    {
        var listed = plugins.Count == 2
            ? $"both {plugins[0]} and {plugins[1]}"
            : string.Join(", ", plugins.Take(plugins.Count - 1)) + " and " + plugins[^1];
        var choices = plugins.Select(plugin => $"{plugin}.{name}").ToList();
        return $"{name} is in {listed}, so write {string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}.";
    }

    /// <summary>
    /// The inflections a module line could mean: all of a verb's, the one Verb.Inflection names, or an inflection
    /// named on its own when no other verb has one by that name. Plugin.Verb and Plugin.Verb.Inflection look only in
    /// that plugin. Null, with the mistake reported, if it means none.
    /// </summary>
    public IReadOnlyList<ModuleRegistration>? Resolve(ModuleSyntax module)
    {
        if (module.Plugin is { } plugin)
            return ResolveInPlugin(module, plugin, module.Verb, module.Inflection);
        if (registry.TryGetVerb(module.Verb, out var inflections) && inflections.Count > 0 && TryVerb(module, inflections, out var resolved))
            return resolved;
        if (module.Inflection is { } second && registry.IsPlugin(module.Verb))
            return ResolveInPlugin(module, module.Verb, second, inflection: null);
        if (module.Inflection is null && TryInflectionAlone(module, out resolved))
            return resolved;
        diagnostics.Error($"There's no module called {module.Verb}." + Suggestions.For(module.Verb, registry.Verbs.Concat(registry.Inflections)), module.VerbSpan);
        return null;
    }

    /// <summary>
    /// Fills in parameters for each inflection the line could mean. Inflections whose parameters records don't fit
    /// the line are left out, and so are ones whose parameters refuse what the line asks (Q22).
    /// </summary>
    public void Bind(LoadNode node, CompileScope scope)
    {
        var groups = node.Registrations.GroupBy(r => r.ParametersType).ToList();
        if (!(groups.Count == 1 ? BindOne(node, groups[0], scope) : BindEach(node, groups, scope)))
            return;
        ChooseByParameters(node);
        if (node.Registration.IsSource && node.Candidates.Count > 1)
        {
            var names = string.Join(" and ", node.Candidates.Select(c => c.Registration.Inflection));
            diagnostics.Error(
                $"{node.Syntax.Verb} has several inflections that could do this: {names}. Name one, as in {node.Registration.Verb}.{node.Candidates[0].Registration.Inflection}.",
                node.Syntax.VerbSpan);
        }
    }

    // A verb's inflections, or the one the line names. False if the line names an inflection the verb hasn't got
    // and the verb is also a plugin's name, so it may be Plugin.Verb.
    private bool TryVerb(ModuleSyntax module, IReadOnlyList<ModuleRegistration> inflections, out IReadOnlyList<ModuleRegistration>? resolved)
    {
        if (module.Inflection is null)
        {
            var shared = registry.SharedBy(module.Verb)
                ?? inflections.Select(r => registry.SharedBy($"{r.Verb}.{r.Inflection}")).FirstOrDefault(plugins => plugins is not null);
            if (shared is not null)
                diagnostics.Error(SharedMessage(module.Verb, shared), module.VerbSpan);
            resolved = shared is null ? inflections : null;
            return true;
        }
        var chosen = inflections.Where(r => r.Inflection == module.Inflection).ToList();
        if (chosen.Count == 1)
        {
            resolved = Unshared(module, chosen, $"{module.Verb}.{module.Inflection}");
            return true;
        }
        resolved = null;
        if (registry.IsPlugin(module.Verb))
            return false;
        diagnostics.Error(
            $"{module.Verb} has no inflection called {module.Inflection}." + Suggestions.For(module.Inflection, inflections.Select(r => r.Inflection)),
            module.VerbSpan);
        return true;
    }

    // An inflection named on its own. False if no verb has one by that name.
    private bool TryInflectionAlone(ModuleSyntax module, out IReadOnlyList<ModuleRegistration>? resolved)
    {
        var named = registry.FindInflection(module.Verb);
        resolved = null;
        if (named.Count == 0)
            return false;
        if (named.Count == 1)
        {
            resolved = Unshared(module, named, $"{named[0].Verb}.{module.Verb}");
            return true;
        }
        var verbs = string.Join(" and ", named.Select(r => r.Verb).Distinct());
        diagnostics.Error($"{module.Verb} is an inflection of {verbs}. Write the one you mean, as in {named[0].Verb}.{module.Verb}.", module.VerbSpan);
        return true;
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

    // Inflections that share one parameters record: bound once, with mistakes reported. False if it didn't bind.
    private bool BindOne(LoadNode node, IGrouping<Type?, ModuleRegistration> group, CompileScope scope)
    {
        var bound = new ParameterBinder(scope, diagnostics, syntax, registry).Bind(node.Syntax, group.First());
        if (bound.Failed)
            return false;
        foreach (var registration in group)
            node.Candidates.Add(new Candidate(registration, bound));
        return true;
    }

    // Inflections with several parameters records: each bound, keeping those the line fits. If it fits none, the
    // problems with the closest are reported, and it's false.
    private bool BindEach(LoadNode node, List<IGrouping<Type?, ModuleRegistration>> groups, CompileScope scope)
    {
        var attempts = new List<Diagnostics>();
        foreach (var group in groups)
        {
            var problems = new Diagnostics(syntax.Source.Path);
            var bound = new ParameterBinder(scope, problems, syntax, registry).Bind(node.Syntax, group.First());
            attempts.Add(problems);
            if (bound.Failed)
                continue;
            foreach (var registration in group)
                node.Candidates.Add(new Candidate(registration, bound));
        }
        if (node.Candidates.Count > 0)
            return true;
        foreach (var diagnostic in attempts.OrderBy(a => a.Items.Count(d => d.IsError)).First().Items)
            diagnostics.Add(diagnostic);
        return false;
    }

    // Asks inflections that parameters choose between whether they take this line's parameters. Parameters that are
    // the same for every message are checked now, so a mistake is reported when the pipeline loads. Ones worked out
    // per message are checked for each message, as are inflections that look at the message itself to choose.
    private void ChooseByParameters(LoadNode node)
    {
        var refusals = new List<string>();
        foreach (var candidate in node.Candidates.ToList())
        {
            if (candidate.Registration.WhyNot is not { } whyNot || candidate.Parameters.Value is not { } parameters)
                continue;
            if (candidate.Parameters.Computed.Count > 0 || candidate.Registration.ChosenForEachMessage)
            {
                candidate.WhyNot = message => whyNot(parameters, message);
                continue;
            }
            if (Refusal(whyNot, parameters) is { } reason)
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

    // Why an inflection won't take parameters fixed for every message, asked with a stand-in message, or null if it will.
    private static string? Refusal(Func<object, Message, string?> whyNot, object parameters)
    {
        try
        {
            return whyNot(parameters, StandIn.Instance);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message;
        }
    }
}
