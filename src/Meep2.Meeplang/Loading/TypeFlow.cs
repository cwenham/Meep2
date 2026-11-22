using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Runtime;
using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Works out which message types can reach each module, from what modules declare they emit, and reports modules that
/// could never receive anything, types that reach a module that doesn't take them, inflections that can't be chosen
/// between, and expressions that fit none of the types that reach them.
/// </summary>
/// <param name="ordered">The pipeline's nodes, wired, in the order it numbers them.</param>
/// <param name="diagnostics">Where mistakes are reported.</param>
internal sealed class TypeFlow(IReadOnlyList<LoadNode> ordered, Diagnostics diagnostics)
{
    // typeof(Message) in a type set means "some message type nobody declared".
    private static readonly Type s_unknown = typeof(Message);

    /// <summary>Fills in each module's inputs and outputs, going round until nothing more can reach anything.</summary>
    public void Flow()
    {
        foreach (var node in ordered.Where(n => n.Registration.IsSource))
            node.Outputs.UnionWith(Declared(node.Candidates[0]));

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var node in ordered.Where(n => !n.Registration.IsSource))
                changed |= FlowThrough(node);
        }
    }

    /// <summary>Reports what doesn't fit, once the types have flowed.</summary>
    public void Check()
    {
        foreach (var node in ordered.Where(n => !n.Registration.IsSource))
        {
            var errorsOnly = node.Options.Errors == ErrorDelivery.Only;
            var arriving = node.Inputs.Where(t => t != s_unknown && (errorsOnly ? IsError(t) : !IsError(t))).ToList();
            var unknown = node.Inputs.Contains(s_unknown);
            var taken = arriving.Where(t => Taking(node, t).Count > 0).ToList();

            if (!errorsOnly && !unknown && arriving.Count > 0 && taken.Count == 0)
            {
                ReportNeverReceives(node, arriving);
                continue;
            }
            // Q20: every type that can reach a module needs an inflection that takes it, unless @bypass lets it past.
            // Otherwise each one would become an error (Q54).
            if (!errorsOnly && !node.Options.Bypass)
                ReportNotTaken(node, arriving.Except(taken));
            ReportUnchosen(node, taken);
            CheckExpressions(node, taken, unknown);
        }
    }

    // Adds what reaches a module to its inputs, and what it emits or passes on to its outputs. True if it has more
    // outputs than before.
    private static bool FlowThrough(LoadNode node)
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
        return node.Outputs.Count != before;
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

    private void ReportNeverReceives(LoadNode node, List<Type> arriving)
    {
        var verb = node.Syntax.Verb;
        var input = string.Join(" or ", node.Candidates.Select(c => Friendly(c.InputType)).Distinct());
        var reaching = string.Join(" or ", arriving.Select(Friendly));
        diagnostics.Error($"{verb} takes {input}, but only {reaching} can reach it, so it would never receive anything.", node.Syntax.VerbSpan);
    }

    private void ReportNotTaken(LoadNode node, IEnumerable<Type> untaken)
    {
        var verb = node.Syntax.Verb;
        foreach (var type in untaken)
        {
            diagnostics.Error(
                $"{Friendly(type)} can reach {verb}, but {verb} doesn't take it. Add @bypass to let it go past, or filter it out before {verb}.",
                node.Syntax.VerbSpan);
        }
    }

    // Q20: inflections that fit a type equally well need parameters worked out per message to choose between them.
    // Otherwise the line has to name one.
    private void ReportUnchosen(LoadNode node, List<Type> taken)
    {
        foreach (var type in taken)
        {
            var closest = Taking(node, type).GroupBy(c => InflectionOrder.Rank(type, c.InputType)).First().ToList();
            if (closest.Count > 1 && closest.Any(c => c.WhyNot is null))
            {
                var names = string.Join(" and ", closest.Select(c => c.Registration.Inflection));
                diagnostics.Error(
                    $"{node.Syntax.Verb} can't choose between {names} for {Friendly(type)}. Name one, as in {node.Registration.Verb}.{closest[0].Registration.Inflection}.",
                    node.Syntax.VerbSpan);
            }
        }
    }

    // Expressions, checked once for each set of parameters the inflections share.
    private void CheckExpressions(LoadNode node, List<Type> taken, bool unknown)
    {
        foreach (var parameters in node.Candidates.Select(c => c.Parameters).Distinct())
        {
            bool Fits(Type t) => node.Candidates.Any(c => c.Parameters == parameters && c.InputType.IsAssignableFrom(t));
            var takenHere = taken.Where(Fits).ToList();
            var errorsHere = node.Inputs.Where(t => t != s_unknown && IsError(t) && Fits(t)).ToList();
            foreach (var computed in parameters.Computed)
            {
                var checkedAgainst = computed.ForErrors == true ? errorsHere : takenHere;
                if (checkedAgainst.Count == 0)
                {
                    if (unknown)
                    {
                        diagnostics.Warning(
                            $"Meeplang can't check {computed.Text} before the pipeline runs, because a module before {node.Syntax.Verb} doesn't say what it emits.",
                            node.Syntax.VerbSpan);
                    }
                    continue;
                }
                if (!checkedAgainst.Any(computed.AppliesTo) && computed.ProblemWith(checkedAgainst[0]) is { } problem)
                    diagnostics.Error(problem.Message, problem.Span);
            }
        }
    }
}
