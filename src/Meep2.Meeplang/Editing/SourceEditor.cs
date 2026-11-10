using System.Globalization;
using System.Text;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>What an edit does to a module.</summary>
public enum EditKind
{
    /// <summary>Sets a parameter's value, adding the parameter if it isn't there. With no parameter, sets the main value written after the verb.</summary>
    Set,

    /// <summary>Removes a parameter, or with no parameter, the main value.</summary>
    Unset,

    /// <summary>Sets an <c>@</c> modifier, with or without a value.</summary>
    SetModifier,

    /// <summary>Removes an <c>@</c> modifier.</summary>
    RemoveModifier,

    /// <summary>Gives the module a name with <c>as</c>, or with no value, takes its name away.</summary>
    Rename,

    /// <summary>Adds a module as a new branch under this one, after any it has.</summary>
    Add,

    /// <summary>Inserts a module between this one and the modules under it, which move under the new one.</summary>
    Insert,

    /// <summary>Removes the module. The modules under it move up to take its place.</summary>
    Remove,

    /// <summary>Removes the module and everything under it.</summary>
    RemoveBranch,

    /// <summary>Sets a setting under <c>defaults</c>, adding the block if there isn't one (Q125).</summary>
    SetDefault,

    /// <summary>Removes a setting from <c>defaults</c>, and the block if it was the last.</summary>
    UnsetDefault,

    /// <summary>Sets a constant's value, declaring it if it isn't there.</summary>
    SetConstant,

    /// <summary>Removes a constant.</summary>
    UnsetConstant,

    /// <summary>Adds a <c>#plugin</c> line. The value is what comes after <c>#plugin</c>.</summary>
    AddPlugin,

    /// <summary>Removes a <c>#plugin</c> line, named by its <c>as</c> name, its address or its file's name.</summary>
    RemovePlugin,
}

/// <summary>One change to a pipeline file's text, aimed at a module, or at the rest of the file (goal 4).</summary>
/// <param name="Module">The module: its name, its number, or a verb only one module has. Empty for edits to defaults, constants and plugins.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Name">The parameter, modifier, setting, constant or plugin, for those edits.</param>
/// <param name="Value">
/// The new value as Meeplang text, such as <c>5s</c> or <c>"hello"</c>; the new name for <see cref="EditKind.Rename"/>;
/// or the new module's line for <see cref="EditKind.Add"/> and <see cref="EditKind.Insert"/>.
/// </param>
public sealed record SourceEdit(string Module, EditKind Kind, string? Name = null, string? Value = null)
{
    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        EditKind.Set => Name is null ? $"set {Module}'s value to {Value}" : $"set {Module}'s {Name} to {Value}",
        EditKind.Unset => Name is null ? $"remove {Module}'s value" : $"remove {Module}'s {Name}",
        EditKind.SetModifier => $"set @{Name}{(Value is null ? "" : "=" + Value)} on {Module}",
        EditKind.RemoveModifier => $"remove @{Name} from {Module}",
        EditKind.Rename => Value is null ? $"take {Module}'s name away" : $"name {Module} {Value}",
        EditKind.Add => $"add {Value} under {Module}",
        EditKind.Insert => $"insert {Value} after {Module}",
        EditKind.Remove => $"remove {Module}",
        EditKind.RemoveBranch => $"remove {Module} and everything under it",
        EditKind.SetDefault => $"set the default {Name} to {Value}",
        EditKind.UnsetDefault => $"remove the default {Name}",
        EditKind.SetConstant => $"set the constant {Name} to {Value}",
        EditKind.UnsetConstant => $"remove the constant {Name}",
        EditKind.AddPlugin => $"add the plugin {Value}",
        EditKind.RemovePlugin => $"remove the plugin {Name}",
        _ => Kind.ToString(),
    };
}

/// <summary>An edit couldn't be made. The message says why.</summary>
public sealed class EditException(string message) : Exception(message);

/// <summary>
/// Changes a pipeline file's text the way a person would (goal 4): each edit touches only the text it has to, so
/// comments, blank lines and the way things are laid out stay as they were.
/// </summary>
/// <remarks>
/// Inserting a module mid-chain moves the modules under it one level in, and removing one moves them one level out,
/// as the staircase layout needs. New lines are indented as the file already indents its modules.
/// </remarks>
public static class SourceEditor
{
    /// <summary>Makes the edits, one after another, each seeing the text as the ones before left it.</summary>
    /// <exception cref="EditException">An edit names a module that isn't there, or can't be made.</exception>
    public static PipelineSyntax Apply(PipelineSyntax syntax, IEnumerable<SourceEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
            syntax = MeeplangParser.Parse(new SourceFile(syntax.Source.Path, Apply(syntax, edit)));
        return syntax;
    }

    /// <summary>
    /// The line an edit to a module changes. The module is named as the running pipeline names it (Q129): by its name,
    /// its number counting from 1 through the modules it runs, its label as messages show it (such as <c>Where #3</c>),
    /// or a verb (or Verb.Inflection) only one module has. A module copied from a template is edited in the
    /// template, and a copy Distribute made is edited as its original. A template's use isn't a module that runs, but
    /// can be named by its template's name, to edit the values it gives.
    /// </summary>
    /// <exception cref="EditException">There's no such module, or more than one, or it's in an included file.</exception>
    public static ModuleSyntax Find(PipelineSyntax syntax, string module) => Target(syntax, module, renaming: false);

    private static ModuleSyntax Target(PipelineSyntax syntax, string module, bool renaming)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(module);
        var running = RunningModules(syntax);
        List<(ModuleSyntax Module, string Label)> found;
        if (int.TryParse(module, out var number))
        {
            found = number >= 1 && number <= running.Count
                ? [running[number - 1]]
                : throw new EditException($"There's no module {number}: the pipeline runs {running.Count}.");
        }
        else
        {
            // A label as messages and the daemon show it, such as Where #3, names one module.
            found = [.. running.Where(m => m.Module.Name == module || m.Label == module)];
            if (found.Count == 0)
                found = [.. running.Where(m => m.Module.Name is null && (m.Module.Verb == module || $"{m.Module.Verb}.{m.Module.Inflection}" == module || m.Module.Inflection == module))];
            if (found.Count == 0 && TemplateUses(syntax, module) is { Count: > 0 } uses)
            {
                return uses.Count == 1
                    ? uses[0]
                    : throw new EditException($"The template {module} is used {uses.Count} times, on lines {Listed(uses.Select(u => u.VerbSpan.Line.ToString(CultureInfo.InvariantCulture)))}, so an edit can't tell which use to change. Change the file itself.");
            }
        }

        // Copies of one line, from a template used more than once or a branch Distribute copied, are that line.
        var lines = found.GroupBy(m => m.Module.Written).ToList();
        var (copy, label) = lines switch
        {
            [var one] => one.First(),
            [] => throw new EditException($"There's no module called {module}. The modules are {string.Join(", ", running.Select(m => m.Label))}."),
            _ => throw new EditException($"There's more than one {module}: {string.Join(", ", found.Select(m => m.Label))}. Say which by number."),
        };

        // A name given by a template's use is written on the use's line.
        var target = renaming && copy.ExpandedFrom is { NameSpan: { } useName } use && copy.NameSpan == useName ? use : copy.Written;
        if (target.VerbSpan.File is { } file)
            throw new EditException($"{label} is copied from the template {copy.ExpandedFrom?.Verb} in {file}, and edits only change the pipeline's own file so far. Change {file} itself.");
        if (renaming && target.Name != copy.Name)
            throw new EditException($"{label} is named {target.Name} in the template {copy.ExpandedFrom?.Verb}, and its use's name is put before that. Rename it in the template, or rename the use.");
        return target;
    }

    // The modules the pipeline runs, in its order, with the labels it gives them.
    private static List<(ModuleSyntax Module, string Label)> RunningModules(PipelineSyntax syntax)
    {
        var all = new List<(ModuleSyntax, string)>();
        void Walk(IReadOnlyList<ModuleSyntax> modules)
        {
            foreach (var module in modules)
            {
                all.Add((module, module.Name ?? $"{module.Verb} #{all.Count + 1}"));
                Walk(module.Children);
            }
        }
        Walk(Load.Running(syntax));
        return all;
    }

    private static string Listed(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count == 1 ? all[0] : string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1];
    }

    // The lines in the pipeline that use the template called name.
    private static List<ModuleSyntax> TemplateUses(PipelineSyntax syntax, string name) =>
        syntax.Templates.Concat(syntax.Included.SelectMany(i => i.Templates)).Any(t => t.Name == name)
            ? [.. AllModules(syntax).Select(m => m.Module).Where(m => m.Verb == name && m.Plugin is null && m.Inflection is null)]
            : [];

    private static string Apply(PipelineSyntax syntax, SourceEdit edit)
    {
        if (edit.Kind >= EditKind.SetDefault)
            return ApplyToFile(syntax, edit);
        var module = Target(syntax, edit.Module, renaming: edit.Kind == EditKind.Rename);
        var text = new Text(syntax.Source.Text);
        var line = Line.Of(syntax, module);
        switch (edit.Kind)
        {
            case EditKind.Set:
                Set(syntax, text, module, line, edit.Name, Required(edit));
                break;
            case EditKind.Unset:
                Unset(text, module, line, edit.Name);
                break;
            case EditKind.SetModifier:
                SetModifier(text, module, line, Required(edit.Name, "the modifier"), edit.Value);
                break;
            case EditKind.RemoveModifier:
                var modifier = module.Modifiers.FirstOrDefault(m => m.Name == edit.Name)
                    ?? throw new EditException($"{Label(module)} has no @{edit.Name}.");
                text.Remove(modifier.Span.Line, LeadingSpace(text, modifier.Span), End(modifier.Span));
                break;
            case EditKind.Rename:
                Rename(text, module, line, edit.Value);
                break;
            case EditKind.Add:
                Add(syntax, text, module, Required(edit));
                break;
            case EditKind.Insert:
                Insert(syntax, text, module, Required(edit));
                break;
            case EditKind.Remove:
                Remove(syntax, text, module, keepBranch: true);
                break;
            case EditKind.RemoveBranch:
                Remove(syntax, text, module, keepBranch: false);
                break;
            default:
                throw new EditException($"There's no edit called {edit.Kind}.");
        }
        return text.ToString();
    }

    private static void Set(PipelineSyntax syntax, Text text, ModuleSyntax module, Line line, string? name, string value)
    {
        if (name is null)
        {
            if (module.Positional is { } positional)
                text.Replace(positional.Span.Line, positional.Span.Column, End(positional.Span), value);
            else
                text.Insert(line.Number, line.HeadEnd, " " + value);
            return;
        }
        if (module.Arguments.FirstOrDefault(a => a.Name == name) is { } argument)
        {
            text.Replace(argument.Value.Span.Line, argument.Value.Span.Column, End(argument.Value.Span), value);
            return;
        }
        // A key is written on the module's line, as by key (Q130).
        if (string.Equals(name, "by", StringComparison.OrdinalIgnoreCase))
        {
            text.Insert(line.Number, line.ParametersEnd, $" by {value}");
            return;
        }
        // A module whose parameters are on lines of their own gets another such line; otherwise it goes on its line.
        var parameterLines = module.Arguments.Where(a => a.NameSpan.Line != line.Number).ToList();
        if (parameterLines.Count > 0)
        {
            var last = parameterLines.Max(a => a.NameSpan.Line);
            var indent = text.IndentOf(parameterLines[0].NameSpan.Line);
            text.InsertLine(last + 1, $"{indent}{name} = {value}");
            return;
        }
        text.Insert(line.Number, line.ParametersEnd, $" {name}={value}");
        _ = syntax;
    }

    private static void Unset(Text text, ModuleSyntax module, Line line, string? name)
    {
        if (name is null)
        {
            var positional = module.Positional ?? throw new EditException($"{Label(module)} has no value after its verb to remove.");
            text.Remove(positional.Span.Line, LeadingSpace(text, positional.Span), End(positional.Span));
            return;
        }
        var argument = module.Arguments.FirstOrDefault(a => a.Name == name)
            ?? throw new EditException($"{Label(module)} has no parameter called {name}.");
        if (argument.NameSpan.Line != line.Number)
        {
            text.RemoveLines(argument.NameSpan.Line, argument.NameSpan.Line);
            return;
        }
        text.Remove(line.Number, LeadingSpace(text, argument.NameSpan), End(argument.Value.Span));
    }

    private static void SetModifier(Text text, ModuleSyntax module, Line line, string name, string? value)
    {
        if (value is not null && value.Contains(' ', StringComparison.Ordinal) && !(value.StartsWith('"') && value.EndsWith('"')))
            value = "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var written = value is null ? $"@{name}" : $"@{name}={value}";
        if (module.Modifiers.FirstOrDefault(m => m.Name == name) is { } existing)
            text.Replace(existing.Span.Line, existing.Span.Column, End(existing.Span), written);
        else
            text.Insert(line.Number, line.CodeEnd, " " + written);
    }

    private static void Rename(Text text, ModuleSyntax module, Line line, string? name)
    {
        if (name is not null && !IsIdentifier(name))
            throw new EditException($"{name} can't be a name: names are letters, digits and underscores, starting with a letter.");
        if (module.NameSpan is { } span)
        {
            if (name is not null)
            {
                text.Replace(span.Line, span.Column, End(span), name);
                return;
            }
            // From the space before "as" to the end of the name.
            var lineText = text.LineAt(span.Line);
            var asColumn = lineText.LastIndexOf(" as ", span.Column - 1, StringComparison.Ordinal) + 1;
            text.Remove(span.Line, asColumn, End(span));
            return;
        }
        if (name is not null)
            text.Insert(line.Number, line.ParametersEnd, $" as {name}");
    }

    private static void Add(PipelineSyntax syntax, Text text, ModuleSyntax module, string newLine)
    {
        var indent = module.Children.Count > 0
            ? text.IndentOf(module.Children[0].VerbSpan.Line)
            : text.IndentOf(module.VerbSpan.Line) + Unit(syntax, text);
        text.InsertLine(LastLine(module) + 1, indent + newLine.Trim());
    }

    private static void Insert(PipelineSyntax syntax, Text text, ModuleSyntax module, string newLine)
    {
        var unit = Unit(syntax, text);
        var ownEnd = OwnLastLine(module);
        if (module.Children.Count > 0)
            text.Indent(ownEnd + 1, LastLine(module), unit); // with any comments above the first of them
        text.InsertLine(ownEnd + 1, text.IndentOf(module.VerbSpan.Line) + unit + newLine.Trim());
    }

    private static void Remove(PipelineSyntax syntax, Text text, ModuleSyntax module, bool keepBranch)
    {
        var first = module.VerbSpan.Line;
        var ownEnd = OwnLastLine(module);
        var last = LastLine(module);
        if (keepBranch && module.Children.Count > 0)
        {
            var by = text.IndentOf(module.Children[0].VerbSpan.Line).Length - text.IndentOf(first).Length;
            text.Outdent(ownEnd + 1, last, by);
            text.RemoveLines(first, ownEnd);
        }
        else
        {
            text.RemoveLines(first, keepBranch ? ownEnd : last);
        }
        _ = syntax;
    }

    // Edits to the parts of a file above its modules: defaults, constants and plugins (Q125). Each keeps the file's
    // order, defaults and constants first, then plugins, then modules.
    private static string ApplyToFile(PipelineSyntax syntax, SourceEdit edit)
    {
        var text = new Text(syntax.Source.Text);
        switch (edit.Kind)
        {
            case EditKind.SetDefault:
            {
                var name = Required(edit.Name, "the setting");
                var value = Required(edit);
                if (syntax.Defaults.FirstOrDefault(d => d.Name == name) is { } setting)
                {
                    text.Replace(setting.Value.Span.Line, setting.Value.Span.Column, End(setting.Value.Span), value);
                }
                else if (syntax.Defaults.Count > 0)
                {
                    var last = syntax.Defaults.Max(d => d.Value.Span.Line);
                    text.InsertLine(last + 1, $"{text.IndentOf(syntax.Defaults[0].NameSpan.Line)}{name} = {value}");
                }
                else
                {
                    InsertBlock(text, TopOf(syntax, text), ["defaults", $"{Unit(syntax, text)}{name} = {value}"], newSection: true);
                }
                break;
            }
            case EditKind.UnsetDefault:
            {
                var name = Required(edit.Name, "the setting");
                var setting = syntax.Defaults.FirstOrDefault(d => d.Name == name)
                    ?? throw new EditException($"The defaults block has no {name}." + (syntax.Defaults.Count == 0 ? " The file has no defaults block." : $" It has {string.Join(", ", syntax.Defaults.Select(d => d.Name))}."));
                if (syntax.Defaults.Count == 1 && HeaderOf(syntax, text) is { } header)
                    text.RemoveLines(header, setting.Value.Span.Line); // the last one goes with its header
                else
                    text.RemoveLines(setting.NameSpan.Line, setting.Value.Span.Line);
                break;
            }
            case EditKind.SetConstant:
            {
                var name = Required(edit.Name, "the constant");
                if (!IsIdentifier(name))
                    throw new EditException($"{name} can't be a constant's name: names are letters, digits and underscores, starting with a letter.");
                var value = Required(edit);
                if (syntax.Constants.FirstOrDefault(c => c.Name == name) is { } constant)
                    text.Replace(constant.Value.Span.Line, constant.Value.Span.Column, End(constant.Value.Span), value);
                else
                    InsertBlock(text, AfterSettings(syntax, text, constants: true), [$"const {name} = {value}"], newSection: syntax.Constants.Count == 0);
                break;
            }
            case EditKind.UnsetConstant:
            {
                var name = Required(edit.Name, "the constant");
                var constant = syntax.Constants.FirstOrDefault(c => c.Name == name)
                    ?? throw new EditException($"There's no constant called {name}." + Suggestions(name, syntax.Constants.Select(c => c.Name)));
                text.RemoveLines(constant.NameSpan.Line, constant.NameSpan.Line);
                break;
            }
            case EditKind.AddPlugin:
            {
                var line = Required(edit).Trim();
                if (!line.StartsWith("#plugin", StringComparison.Ordinal))
                    line = "#plugin " + line;
                var at = syntax.Plugins.Count > 0 ? syntax.Plugins.Max(p => p.Span.Line) + 1 : AfterSettings(syntax, text, constants: false);
                InsertBlock(text, at, [line], newSection: syntax.Plugins.Count == 0);
                break;
            }
            case EditKind.RemovePlugin:
            {
                var name = Required(edit.Name, "the plugin");
                var found = syntax.Plugins.Where(p => Names(p).Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                var plugin = found switch
                {
                    [var one] => one,
                    [] => throw new EditException($"There's no plugin called {name}." + (syntax.Plugins.Count == 0 ? " The file has none." : $" The file has {string.Join(", ", syntax.Plugins.Select(p => p.Alias ?? p.ToString()))}.")),
                    _ => throw new EditException($"More than one plugin could be {name}: {string.Join(", ", found)}. Name it by the address or path on its line."),
                };
                text.RemoveLines(plugin.Span.Line, plugin.Span.Line);
                break;
            }
            default:
                throw new EditException($"There's no edit called {edit.Kind}.");
        }
        return text.ToString();

        // What a plugin line can be called by: its as name, its address or path, and its last part without .dll.
        static IEnumerable<string> Names(PluginDirective plugin)
        {
            if (plugin.Alias is { } alias)
                yield return alias;
            yield return plugin.Name;
            yield return plugin.ToString();
            var last = plugin.Name.Replace('\\', '/').Split('/')[^1];
            yield return last;
            if (last.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                yield return last[..^4];
        }
    }

    private static string Suggestions(string name, IEnumerable<string> names) =>
        Meep2.Meeplang.Compilation.Suggestions.For(name, names);

    // The line the defaults header is on, if the file has a defaults block.
    private static int? HeaderOf(PipelineSyntax syntax, Text text)
    {
        if (syntax.Defaults.Count == 0)
            return null;
        for (var line = syntax.Defaults.Min(d => d.NameSpan.Line) - 1; line >= 1; line--)
        {
            if (text.LineAt(line).Trim() == "defaults")
                return line;
        }
        return null;
    }

    // Where something new goes at the top of the file: after a heading of comments that a blank line ends, or at the
    // very top. A comment right above the first module describes that module, so nothing goes between them.
    private static int TopOf(PipelineSyntax syntax, Text text)
    {
        var first = FirstSyntaxLine(syntax);
        for (var line = first - 1; line >= 1; line--)
        {
            if (text.LineAt(line).Trim().Length == 0)
                return line + 1;
        }
        return 1;
    }

    // Where constants, or plugins, go: after the last default or constant before them, or at the top.
    private static int AfterSettings(PipelineSyntax syntax, Text text, bool constants)
    {
        var lines = syntax.Defaults.Select(d => d.Value.Span.Line).Concat(syntax.Constants.Select(c => c.NameSpan.Line));
        if (!constants)
            lines = lines.Concat(syntax.Plugins.Select(p => p.Span.Line));
        else if (syntax.Constants.Count > 0)
            return syntax.Constants.Max(c => c.NameSpan.Line) + 1;
        return lines.Any() ? lines.Max() + 1 : TopOf(syntax, text);
    }

    private static int FirstSyntaxLine(PipelineSyntax syntax) =>
        syntax.Defaults.Select(d => d.NameSpan.Line - 1)
            .Concat(syntax.Constants.Select(c => c.NameSpan.Line))
            .Concat(syntax.Plugins.Select(p => p.Span.Line))
            .Concat(syntax.Modules.Select(m => m.VerbSpan.Line))
            .DefaultIfEmpty(1)
            .Min();

    // Inserts lines, with a blank line after them if they start a new section and text follows straight on.
    private static void InsertBlock(Text text, int at, IReadOnlyList<string> lines, bool newSection)
    {
        var followedByText = at <= text.Count && text.LineAt(at).Trim().Length > 0;
        for (var i = lines.Count - 1; i >= 0; i--)
            text.InsertLine(at, lines[i]);
        if (newSection && followedByText)
            text.InsertLine(at + lines.Count, "");
    }

    // How far the file indents a module under another: the smallest step it uses, or four spaces.
    private static string Unit(PipelineSyntax syntax, Text text)
    {
        var steps = AllModules(syntax)
            .Where(m => m.Parent is not null)
            .Select(m => text.IndentOf(m.Module.VerbSpan.Line).Length - text.IndentOf(m.Parent!.VerbSpan.Line).Length)
            .Where(step => step > 0)
            .ToList();
        var uses = AllModules(syntax).Select(m => text.IndentOf(m.Module.VerbSpan.Line)).FirstOrDefault(i => i.Length > 0);
        var tab = uses is not null && uses.StartsWith('\t');
        return steps.Count == 0 ? "    " : tab ? new string('\t', 1) : new string(' ', steps.Min());
    }

    // The module's own last line: its line, or its last parameter line.
    private static int OwnLastLine(ModuleSyntax module) =>
        module.Arguments.Select(a => a.Value.Span.Line).Append(module.VerbSpan.Line).Max();

    // The last line of the module and everything under it.
    private static int LastLine(ModuleSyntax module) =>
        module.Children.Select(LastLine).Append(OwnLastLine(module)).Max();

    private static IEnumerable<(ModuleSyntax Module, ModuleSyntax? Parent, string Label)> AllModules(PipelineSyntax syntax)
    {
        var number = 0;
        IEnumerable<(ModuleSyntax, ModuleSyntax?, string)> Walk(IReadOnlyList<ModuleSyntax> modules, ModuleSyntax? parent)
        {
            foreach (var module in modules)
            {
                number++;
                yield return (module, parent, module.Name ?? $"{module.Verb} #{number}");
                foreach (var child in Walk(module.Children, module))
                    yield return child;
            }
        }
        return Walk(syntax.Modules, null).ToList();
    }

    private static string Required(SourceEdit edit) =>
        edit.Value ?? throw new EditException($"To {edit.Kind.ToString().ToLowerInvariant()}, the edit needs a value.");

    private static string Required(string? value, string what) =>
        value ?? throw new EditException($"The edit needs {what}.");

    private static string Label(ModuleSyntax module) => module.Name ?? module.Verb;

    private static int End(SourceSpan span) => span.Column + span.Length;

    // A span's start, moved back over the spaces before it, so removing it leaves no double space.
    private static int LeadingSpace(Text text, SourceSpan span)
    {
        var line = text.LineAt(span.Line);
        var column = span.Column;
        while (column > 1 && line[column - 2] == ' ')
            column--;
        return column;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Where things are on a module's line.</summary>
    private sealed record Line(int Number, int HeadEnd, int ParametersEnd, int CodeEnd)
    {
        public static Line Of(PipelineSyntax syntax, ModuleSyntax module)
        {
            var number = module.VerbSpan.Line;
            var text = syntax.Source.Text.Split('\n')[number - 1].TrimEnd('\r');

            // The verb, with .Inflection or Plugin.Verb.Inflection.
            var head = module.VerbSpan.Column - 1;
            while (head < text.Length && (char.IsLetterOrDigit(text[head]) || text[head] is '_' or '.'))
                head++;
            var headEnd = head + 1;

            var parametersEnd = headEnd;
            if (module.Positional is { } positional && positional.Span.Line == number)
                parametersEnd = Math.Max(parametersEnd, End(positional.Span));
            foreach (var argument in module.Arguments.Where(a => a.NameSpan.Line == number))
                parametersEnd = Math.Max(parametersEnd, End(argument.Value.Span));

            var codeEnd = parametersEnd;
            if (module.NameSpan is { } name)
                codeEnd = Math.Max(codeEnd, End(name));
            foreach (var modifier in module.Modifiers)
                codeEnd = Math.Max(codeEnd, End(modifier.Span));
            return new Line(number, headEnd, parametersEnd, codeEnd);
        }
    }

    /// <summary>A file's lines, changed in place, keeping its line endings.</summary>
    private sealed class Text
    {
        private readonly List<string> _lines;
        private readonly string _newline;

        public Text(string text)
        {
            _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            _lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
        }

        public string LineAt(int number) => _lines[number - 1];

        public int Count => _lines.Count;

        public string IndentOf(int number)
        {
            var line = _lines[number - 1];
            var content = line.TrimStart(' ', '\t');
            return line[..(line.Length - content.Length)];
        }

        public void Replace(int number, int from, int to, string with)
        {
            var line = _lines[number - 1];
            var after = line[(to - 1)..];

            // A comment later on the line stays in its column, where there's room, so comments lined up stay so.
            var grows = with.Length - (to - from);
            if (grows != 0 && CommentAt(after) is { } comment)
            {
                var spaces = 0;
                while (spaces < comment && after[comment - spaces - 1] == ' ')
                    spaces++;
                if (spaces >= 2)
                {
                    var keep = Math.Max(1, spaces - grows);
                    after = after[..(comment - spaces)] + new string(' ', keep) + after[comment..];
                }
            }
            _lines[number - 1] = line[..(from - 1)] + with + after;
        }

        // Where a // comment starts in some text from a line, outside any string, or null.
        private static int? CommentAt(string text)
        {
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    return i;
                }
            }
            return null;
        }

        public void Insert(int number, int at, string what) => Replace(number, at, at, what);

        public void Remove(int number, int from, int to) => Replace(number, from, to, "");

        public void InsertLine(int number, string line) => _lines.Insert(Math.Min(number - 1, _lines.Count), line);

        public void RemoveLines(int first, int last) => _lines.RemoveRange(first - 1, last - first + 1);

        public void Indent(int first, int last, string by)
        {
            for (var i = first; i <= last; i++)
            {
                if (_lines[i - 1].Trim().Length > 0)
                    _lines[i - 1] = by + _lines[i - 1];
            }
        }

        public void Outdent(int first, int last, int by)
        {
            for (var i = first; i <= last; i++)
            {
                var line = _lines[i - 1];
                var strip = 0;
                while (strip < by && strip < line.Length && line[strip] is ' ' or '\t')
                    strip++;
                _lines[i - 1] = line[strip..];
            }
        }

        public override string ToString()
        {
            var text = new StringBuilder();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (i > 0)
                    text.Append(_newline);
                text.Append(_lines[i]);
            }
            return text.ToString();
        }
    }
}
