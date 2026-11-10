using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Copies templates into a pipeline before it's built (Q24, round 2): each module line that names a template is
/// replaced by the template's modules, with the line's values in place of the template's parameters. The modules
/// indented under the line go under the template's last module.
/// </summary>
/// <remarks>
/// <para>
/// A use gives the template's first parameter as its main value and the rest by name, as a module takes its
/// parameters: <c>Departures "12" limit=5</c> for <c>template Departures(Stop, Limit = 10)</c>. Values can use
/// <c>msg</c>, since they're copied in as written.
/// </para>
/// <para>
/// <c>as Board</c> on a use names the template's first module Board, and puts Board_ before the names of the other
/// modules inside it, so a template that names its modules can be used more than once (Q128). References to those
/// names inside the template, by <c>fam.Name</c> or as a parameter, are renamed to match. A use without a name keeps
/// the names as written, so it can be used once. Templates can use other templates, but not themselves.
/// </para>
/// </remarks>
internal static class TemplateExpander
{
    private const int DeepestUse = 32;

    /// <summary>
    /// The pipeline's modules with every template use replaced. The same list if nothing uses a template, so a
    /// pipeline without them is built from its own syntax.
    /// </summary>
    /// <param name="isVerb">Whether a name is a verb, which a template mustn't share.</param>
    public static IReadOnlyList<ModuleSyntax> Expand(PipelineSyntax syntax, Diagnostics diagnostics, Func<string, bool> isVerb)
    {
        var templates = new Dictionary<string, TemplateBlock>(StringComparer.Ordinal);
        foreach (var template in syntax.Templates.Concat(syntax.Included.SelectMany(i => i.Templates)))
        {
            if (!templates.TryAdd(template.Name, template))
                diagnostics.Error($"There's more than one template called {template.Name}: in {templates[template.Name].Path} and {template.Path}.", template.NameSpan);
            else if (isVerb(template.Name))
                diagnostics.Error($"{template.Name} is a module's verb, so a template can't be called that too. Give the template another name.", template.NameSpan);
        }
        if (templates.Count == 0 || !Uses(syntax.Modules, templates))
            return syntax.Modules;
        return new Expansion(templates, diagnostics).List(syntax.Modules, Substitutions.None, []);
    }

    // Whether any module, at any depth, names a template.
    private static bool Uses(IEnumerable<ModuleSyntax> modules, Dictionary<string, TemplateBlock> templates) =>
        modules.Any(m => IsUse(m, templates, out _) || Uses(m.Children, templates));

    private static bool IsUse(ModuleSyntax module, Dictionary<string, TemplateBlock> templates, out TemplateBlock template)
    {
        template = null!;
        return module.Plugin is null && module.Inflection is null && templates.TryGetValue(module.Verb, out template!);
    }

    private sealed class Expansion(Dictionary<string, TemplateBlock> templates, Diagnostics diagnostics)
    {
        public List<ModuleSyntax> List(IReadOnlyList<ModuleSyntax> modules, Substitutions values, string[] within)
        {
            var expanded = new List<ModuleSyntax>();
            foreach (var module in modules)
            {
                if (IsUse(module, templates, out var template))
                    expanded.AddRange(Use(module, template, values, within));
                else
                    expanded.Add(Copy(module, values, within));
            }
            return expanded;
        }

        private ModuleSyntax Copy(ModuleSyntax module, Substitutions values, string[] within)
        {
            var copy = module.Copy(values.Apply, List(module.Children, values, within));
            copy.Name = values.Name(module.Name);
            return copy;
        }

        // A template's modules in place of a line that uses it.
        private IEnumerable<ModuleSyntax> Use(ModuleSyntax use, TemplateBlock template, Substitutions values, string[] within)
        {
            // A use inside a named use's template takes that use's prefix too.
            var useName = values.Name(use.Name);
            if (within.Contains(template.Name))
            {
                diagnostics.Error($"{template.Name} uses itself, through {string.Join(" → ", [.. within, template.Name])}, so it can't be copied in.", use.VerbSpan);
                return [];
            }
            if (within.Length >= DeepestUse)
            {
                diagnostics.Error($"Templates are used inside each other more than {DeepestUse} deep here.", use.VerbSpan);
                return [];
            }
            if (use.Modifiers.Count > 0)
            {
                diagnostics.Error($"A use of the template {template.Name} doesn't take @ modifiers. Put them on the modules inside it.", use.Modifiers[0].Span);
                return [];
            }
            if (Bind(use, template, values) is not { } parameters)
                return [];

            var inside = new Substitutions(parameters, useName is null ? null : Renames(template, useName));
            var modules = List(template.Body, inside, [.. within, template.Name]);
            foreach (var module in Walk(modules))
                module.ExpandedFrom ??= use;
            if (modules.Count == 0)
            {
                diagnostics.Error($"The template {template.Name} has no modules indented under it.", template.NameSpan);
                return [];
            }

            // The use's +, its name, and the modules under it.
            var first = modules[0];
            if (use.IsFeeder || useName is not null)
            {
                var renamed = first.Copy(e => e, first.Children, isFeeder: use.IsFeeder || first.IsFeeder);
                renamed.Name = useName ?? first.Name;
                renamed.NameSpan = use.NameSpan ?? first.NameSpan;
                renamed.ExpandedFrom = use;
                modules[0] = renamed;
            }
            var under = List(use.Children, values, within);
            if (under.Count > 0)
            {
                var ends = modules.SelectMany(Ends).ToList();
                if (ends.Count != 1)
                {
                    diagnostics.Error($"{template.Name} ends in {ends.Count} modules, so nothing can go under its use: which would it come after?", use.Children[0].VerbSpan);
                    return [];
                }
                var end = ends[0];
                foreach (var child in under)
                    end.AddChild(child);
            }
            return modules;
        }

        // The names a use called useName gives the modules named inside its template: the first module takes the use's
        // name, and the others have it put before theirs.
        private static Dictionary<string, string> Renames(TemplateBlock template, string useName)
        {
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var module in Walk(template.Body))
            {
                if (module.Name is { } name)
                    renames.TryAdd(name, ReferenceEquals(module, template.Body[0]) ? useName : $"{useName}_{name}");
            }
            return renames;
        }

        // The values a use gives a template's parameters, from its main value, its named values and the defaults.
        private Dictionary<string, ExpressionSyntax>? Bind(ModuleSyntax use, TemplateBlock template, Substitutions values)
        {
            var bound = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var ok = true;
            if (use.Positional is { } main)
            {
                if (template.Parameters.Count == 0)
                {
                    diagnostics.Error($"The template {template.Name} takes no parameters, so it takes no value after its name.", main.Span);
                    ok = false;
                }
                else
                {
                    bound[template.Parameters[0].Name] = values.Apply(main);
                }
            }
            foreach (var argument in use.Arguments)
            {
                var parameter = template.Parameters.FirstOrDefault(p => string.Equals(p.Name, argument.Name, StringComparison.OrdinalIgnoreCase));
                if (parameter is null)
                {
                    diagnostics.Error(
                        $"The template {template.Name} has no parameter called {argument.Name}." + Suggestions.For(argument.Name, template.Parameters.Select(p => p.Name)),
                        argument.NameSpan);
                    ok = false;
                }
                else if (!bound.TryAdd(parameter.Name, values.Apply(argument.Value)))
                {
                    diagnostics.Error($"{parameter.Name} is given twice: as the value after {template.Name}, and by name.", argument.NameSpan);
                    ok = false;
                }
            }
            foreach (var parameter in template.Parameters)
            {
                if (bound.ContainsKey(parameter.Name))
                    continue;
                if (parameter.Default is { } fallback)
                {
                    bound[parameter.Name] = fallback;
                }
                else
                {
                    diagnostics.Error($"The template {template.Name} needs a value for {parameter.Name}.", use.VerbSpan);
                    ok = false;
                }
            }
            return ok ? bound : null;
        }

        private static IEnumerable<ModuleSyntax> Walk(IEnumerable<ModuleSyntax> modules) =>
            modules.SelectMany(m => Walk(m.Children).Prepend(m));

        // The modules with nothing under them, which is where the use's own modules continue.
        private static IEnumerable<ModuleSyntax> Ends(ModuleSyntax module) =>
            module.Children.Count == 0 ? [module] : module.Children.SelectMany(Ends);
    }

    /// <summary>
    /// Parameters' values, put in place of their names wherever a template's expressions use them, and the new names of
    /// modules named inside a named use, put in place of references to them. A value is put in as it is, since it's
    /// written outside the template.
    /// </summary>
    private sealed class Substitutions(IReadOnlyDictionary<string, ExpressionSyntax> values, IReadOnlyDictionary<string, string>? names = null)
    {
        public static readonly Substitutions None = new(new Dictionary<string, ExpressionSyntax>());

        public ExpressionSyntax Apply(ExpressionSyntax expression) =>
            values.Count == 0 && names is null ? expression : Rewrite(expression);

        /// <summary>A module's name inside the template, as the use renames it.</summary>
        public string? Name(string? name) =>
            name is not null && names is not null && names.TryGetValue(name, out var renamed) ? renamed : name;

        private ExpressionSyntax Rewrite(ExpressionSyntax expression) => expression switch
        {
            NameSyntax name when values.TryGetValue(name.Name, out var value) => value,
            NameSyntax name when Name(name.Name) is { } renamed && renamed != name.Name => name with { Name = renamed },
            TemplateSyntax template => template with
            {
                Parts = [.. template.Parts.Select(part => part is HolePart hole ? hole with { Expression = Rewrite(hole.Expression) } : part)],
            },
            MemberSyntax { Target: NameSyntax { Name: "fam" } } member => member with { Member = Name(member.Member)! }, // fam.Name names a module
            MemberSyntax member => member with { Target = Rewrite(member.Target) },
            CallSyntax call => call with { Callee = Rewrite(call.Callee), Arguments = [.. call.Arguments.Select(Rewrite)] },
            IndexSyntax index => index with { Target = Rewrite(index.Target), Index = Rewrite(index.Index) },
            UnarySyntax unary => unary with { Operand = Rewrite(unary.Operand) },
            BinarySyntax binary => binary with { Left = Rewrite(binary.Left), Right = Rewrite(binary.Right) },
            IsSyntax test => test with { Operand = Rewrite(test.Operand) },
            _ => expression,
        };
    }
}
