using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// Works out the values a pipeline fixes when it loads: its constants, its settings with the <c>defaults</c> block
/// applied, each module's options from its modifiers, and each module's error policy.
/// </summary>
/// <param name="syntax">The pipeline.</param>
/// <param name="diagnostics">Where mistakes are reported.</param>
internal sealed class OptionsBuilder(PipelineSyntax syntax, Diagnostics diagnostics)
{
    private static readonly string[] s_defaults = ["buffer", "expires", "on-error", "keep-finished"];
    private static readonly string[] s_modifiers = ["buffer", "bypass", "errors", "no-errors", "expires", "accept-expired", "on-error"];

    /// <summary>The pipeline's constants, included files' first, so the pipeline's own can't be redeclared by them unnoticed.</summary>
    public Dictionary<string, ConstantValue> Constants()
    {
        var constants = new Dictionary<string, ConstantValue>(StringComparer.Ordinal);
        var scope = new CompileScope(constants, _ => null, _ => null, []);
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

    /// <summary><paramref name="options"/> with the pipeline's <c>defaults</c> block applied.</summary>
    public PipelineOptions WithDefaults(PipelineOptions options)
    {
        foreach (var setting in syntax.Defaults)
            options = WithDefault(options, setting);
        return options;
    }

    /// <summary>A module's options, from its modifiers and from which message types its expressions fit.</summary>
    public NodeOptions For(LoadNode node)
    {
        var options = NodeOptions.Default;
        foreach (var modifier in node.Syntax.Modifiers)
        {
            if (node.Registration.IsSource && modifier.Name is "buffer" or "bypass" or "errors" or "no-errors" or "accept-expired")
                diagnostics.Error($"{node.Syntax.Verb} is a source, so @{modifier.Name} doesn't apply to it.", modifier.Span);
            else
                options = With(options, node, modifier);
        }
        return WithHandlesType(options, node);
    }

    /// <summary>
    /// Works out each module's error policy: its own @on-error over the policy of the module it's indented under, and
    /// at the left margin, the pipeline's.
    /// </summary>
    public static void ApplyErrorPolicies(IReadOnlyList<LoadNode> ordered, ErrorPolicy pipeline)
    {
        var policies = new Dictionary<LoadNode, ErrorPolicy>();
        foreach (var node in ordered)
        {
            // A module comes after the module it's indented under, so its policy is already known.
            var basis = node.Parent is { } parent ? policies[parent] : pipeline;
            var policy = node.OnError?.ApplyTo(basis) ?? basis;
            policies[node] = policy;
            if (!ReferenceEquals(policy, pipeline))
                node.Options = node.Options with { ErrorPolicy = policy };
        }
    }

    /// <summary>A whole number of at least 1, or null.</summary>
    public static int? PositiveWholeNumber(object? value) =>
        value is not null && Dynamic.TryConvert(value, typeof(int), out var whole) && whole is int size and > 0 ? size : null;

    /// <summary>Works out a value that's the same for every message. Returns false, having reported why, if it can't.</summary>
    public static bool TryConstant(ExpressionSyntax value, Diagnostics diagnostics, out object? result)
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

    private PipelineOptions WithDefault(PipelineOptions options, ArgumentSyntax setting)
    {
        switch (setting.Name)
        {
            case "buffer":
                if (TryConstant(setting.Value, diagnostics, out var value))
                {
                    if (PositiveWholeNumber(value) is { } size)
                        return options with { BufferSize = size };
                    diagnostics.Error("buffer needs a whole number of messages, at least 1.", setting.Value.Span);
                }
                return options;
            case "expires":
                return Lifetime(setting.Value, "expires = 5s") is { } lifetime
                    ? options with { Expires = lifetime == Timeout.InfiniteTimeSpan ? null : lifetime }
                    : options;
            case "on-error":
                return Policy(setting.Value, "on-error = log, stop-branch after 5 in 1s", forPipeline: true) is { } parts
                    ? options with { ErrorPolicy = parts.ApplyTo(options.ErrorPolicy) }
                    : options;
            case "keep-finished":
                return Lifetime(setting.Value, "keep-finished = 24h", allowZero: true) is { } keep
                    ? options with { KeepFinished = keep == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : keep }
                    : options;
            default:
                diagnostics.Error($"There's no default called {setting.Name}." + Suggestions.For(setting.Name, s_defaults), setting.NameSpan);
                return options;
        }
    }

    private NodeOptions With(NodeOptions options, LoadNode node, ModifierSyntax modifier)
    {
        switch (modifier.Name)
        {
            case "buffer":
                return Buffer(modifier) is { } size ? options with { BufferSize = size } : options;
            case "bypass":
                return options with { Bypass = true };
            case "errors":
                return options with { Errors = ErrorDelivery.Only };
            case "no-errors":
                return options with { Errors = ErrorDelivery.Exclude };
            case "expires":
                if (modifier.Value is null)
                    diagnostics.Error("@expires needs a duration, as in @expires=5s, or none.", modifier.Span);
                else if (Lifetime(modifier.Value, "@expires=5s") is { } lifetime)
                    return options with { Expires = lifetime };
                return options;
            case "accept-expired":
                return options with { AcceptExpired = true };
            case "on-error":
                if (modifier.Value is null)
                    diagnostics.Error("@on-error needs an error policy in quotes, as in @on-error=\"stop-branch after 5 in 1s\".", modifier.Span);
                else
                    node.OnError = Policy(modifier.Value, "@on-error=\"stop-branch after 5 in 1s\"", forPipeline: false) ?? node.OnError;
                return options;
            default:
                diagnostics.Error($"There's no modifier called @{modifier.Name}." + Suggestions.For(modifier.Name, s_modifiers), modifier.Span);
                return options;
        }
    }

    // @buffer's size, or null, having reported why.
    private int? Buffer(ModifierSyntax modifier)
    {
        const string Needs = "@buffer needs a whole number of messages, at least 1, as in @buffer=1000.";
        if (modifier.Value is null)
        {
            diagnostics.Error(Needs, modifier.Span);
            return null;
        }
        if (!TryConstant(modifier.Value, diagnostics, out var value))
            return null;
        if (PositiveWholeNumber(value) is { } size)
            return size;
        diagnostics.Error(Needs, modifier.Span);
        return null;
    }

    // Messages an expression doesn't fit skip the module (Q69): a type goes in only if an inflection that takes it has
    // expressions that fit it.
    private static NodeOptions WithHandlesType(NodeOptions options, LoadNode node)
    {
        var candidates = node.Candidates;
        if (node.Registration.IsSource || !candidates.Any(c => c.Parameters.Computed.Count > 0))
            return options;
        return options with
        {
            HandlesType = type => candidates.Any(c => c.InputType.IsAssignableFrom(type) && c.Parameters.Computed.All(computed => computed.Fits(type))),
        };
    }

    // A duration, or none (Timeout.InfiniteTimeSpan): for expiry, none clears it; for keep-finished, it's zero.
    private TimeSpan? Lifetime(ExpressionSyntax value, string example, bool allowZero = false)
    {
        if (value is NameSyntax { Name: "none" })
            return Timeout.InfiniteTimeSpan;
        if (!TryConstant(value, diagnostics, out var result))
            return null;
        if (result is TimeSpan lifetime && (lifetime > TimeSpan.Zero || allowZero && lifetime == TimeSpan.Zero))
            return lifetime;
        diagnostics.Error(allowZero
            ? $"This needs a duration, as in {example}, or none."
            : $"Expiry needs a duration longer than zero, as in {example}, or none.", value.Span);
        return null;
    }

    // An error policy, whether it's written as it is (under defaults) or in quotes, or null, having reported why.
    private ErrorPolicyParts? Policy(ExpressionSyntax value, string example, bool forPipeline)
    {
        if (!TryConstant(value, diagnostics, out var result))
            return null;
        if (result is not string text)
        {
            diagnostics.Error($"on-error needs an error policy, as in {example}.", value.Span);
            return null;
        }
        var written = syntax.TextOf(value.Span);
        var quoted = written.Length > 0 && written[0] is '"' or '\'';
        return ErrorPolicyParser.Parse(text, quoted ? value.Span with { Column = value.Span.Column + 1 } : value.Span, forPipeline, diagnostics);
    }
}
