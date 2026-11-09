using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang;

/// <summary>A value in a settings file, worked out, with where it was written.</summary>
/// <param name="Value">The value: text, a number, a duration, true or false.</param>
/// <param name="Span">Where it's written.</param>
public sealed record SettingValue(object? Value, SourceSpan Span);

/// <summary>One line of a settings file, written as a module line is: a word, a value, and named values.</summary>
/// <param name="Word">The word it starts with, such as <c>Start</c>.</param>
/// <param name="Value">The value written after the word, if any.</param>
/// <param name="Named">The values given by name, such as <c>watch=true</c>.</param>
/// <param name="Span">Where the word is.</param>
public sealed record SettingsLine(string Word, SettingValue? Value, IReadOnlyDictionary<string, SettingValue> Named, SourceSpan Span);

/// <summary>
/// A file of settings written in Meeplang (Q119), such as the daemon's: a <c>defaults</c> block, constants, and lines
/// that each start with a word and take values as module lines do. Nothing in it runs; it's read and worked out.
/// </summary>
/// <remarks>
/// Values can use the file's constants, as in <c>Start "{Pipelines}/ticker.meep"</c>, but not <c>msg</c> or
/// <c>fam</c>, since there are no messages.
/// </remarks>
public sealed class MeeplangSettings
{
    private MeeplangSettings(string path, IReadOnlyDictionary<string, SettingValue> defaults, IReadOnlyList<SettingsLine> lines)
    {
        Path = path;
        Defaults = defaults;
        Lines = lines;
    }

    /// <summary>The file it was read from.</summary>
    public string Path { get; }

    /// <summary>The settings under <c>defaults</c>, by name.</summary>
    public IReadOnlyDictionary<string, SettingValue> Defaults { get; }

    /// <summary>The lines at the left margin, in order.</summary>
    public IReadOnlyList<SettingsLine> Lines { get; }

    /// <summary>Reads a parsed settings file, working out its values.</summary>
    /// <exception cref="FormatException">The file has mistakes. The message lists each, with where it is.</exception>
    public static MeeplangSettings Read(PipelineSyntax syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var problems = new Diagnostics(syntax.Source.Path);
        foreach (var diagnostic in syntax.Diagnostics)
            problems.Add(diagnostic);
        if (syntax.Plugins.Count > 0)
            problems.Error("A settings file doesn't load plugins.", syntax.Plugins[0].Span);

        var constants = new Dictionary<string, ConstantValue>(StringComparer.Ordinal);
        var scope = new CompileScope(constants, _ => null, _ => null, []);
        SettingValue? Evaluate(ExpressionSyntax expression)
        {
            try
            {
                return new SettingValue(ExpressionCompiler.Evaluate(expression, scope).Value, expression.Span);
            }
            catch (BindException exception)
            {
                problems.Error(exception.Message, exception.Span);
                return null;
            }
        }

        foreach (var constant in syntax.Constants)
        {
            if (Evaluate(constant.Value) is { } value)
                constants[constant.Name] = new ConstantValue(value.Value, value.Value?.GetType() ?? typeof(object));
        }
        var defaults = new Dictionary<string, SettingValue>(StringComparer.Ordinal);
        foreach (var setting in syntax.Defaults)
        {
            if (Evaluate(setting.Value) is { } value)
                defaults[setting.Name] = value;
        }
        var lines = new List<SettingsLine>();
        foreach (var module in syntax.Modules)
        {
            if (module.Children.Count > 0)
                problems.Error($"Settings lines aren't indented under each other, as {module.Children[0].Verb} is under {module.Verb}.", module.Children[0].VerbSpan);
            if (module.NameSpan is { } name)
                problems.Error("A settings line doesn't take a name.", name);
            if (module.Modifiers.Count > 0)
                problems.Error("A settings line doesn't take @ modifiers.", module.Modifiers[0].Span);
            var named = new Dictionary<string, SettingValue>(StringComparer.Ordinal);
            foreach (var argument in module.Arguments)
            {
                if (Evaluate(argument.Value) is { } value)
                    named[argument.Name] = value;
            }
            var word = module.Inflection is null ? module.Verb : $"{module.Verb}.{module.Inflection}";
            lines.Add(new SettingsLine(word, module.Positional is null ? null : Evaluate(module.Positional), named, module.VerbSpan));
        }

        if (problems.HasErrors)
            throw new FormatException(string.Join(Environment.NewLine, problems.Items.Where(d => d.IsError)));
        return new MeeplangSettings(syntax.Source.Path, defaults, lines);
    }
}
