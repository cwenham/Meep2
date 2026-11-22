using Meep2.Daemon;

namespace Meep2.Cli;

/// <summary>
/// <c>meep edit &lt;pipeline&gt; &lt;module&gt; &lt;edit&gt; [--message &lt;text&gt;] [--branch &lt;name&gt;] [--no-save]</c>:
/// edits a running pipeline through the daemon, which saves the edit to its file, a branch, or nowhere.
/// </summary>
internal static class EditCommand
{
    public static async Task<int> RunAsync(CliContext cli, string pipeline, string module, string[] words)
    {
        var options = EditOptions.Parse(words);
        if (EditOf(module, options.Edit) is not { } item)
        {
            await cli.Error.WriteLineAsync(EditUsage).ConfigureAwait(false);
            return 2;
        }
        var result = await cli.Client.EditAsync(pipeline, new EditRequest([item], options.Message, options.Branch, options.Save), cli.Cancellation).ConfigureAwait(false);
        await DaemonCommands.WriteChangeAsync($"Edited {pipeline}", result, cli.Output).ConfigureAwait(false);
        await WriteSavedAsync(result, cli.Output).ConfigureAwait(false);
        return 0;
    }

    // Where the edit was saved, or why it wasn't.
    private static async Task WriteSavedAsync(ChangeResult result, TextWriter output)
    {
        if (result.Saved is { } saved)
        {
            await output.WriteLineAsync($"Committed to {saved.Branch} as {saved.Sha[..7]}, in {saved.Repository}.").ConfigureAwait(false);
            await output.WriteLineAsync(saved.Unsigned is { } unsigned ? $"It isn't signed: {unsigned}." : "It's signed with your SSH key.").ConfigureAwait(false);
        }
        else if (result.WrittenTo is { } file)
        {
            await output.WriteLineAsync($"Wrote it to {file}, which isn't in a Git repository.").ConfigureAwait(false);
        }
        else if (result.NotSaved is { } why)
        {
            await output.WriteLineAsync($"Not saved: {why}").ConfigureAwait(false);
        }
    }

    private const string EditUsage = """
        meep edit <pipeline> defaults set <name>=<value>    set a default, such as defaults set expires=5s
        meep edit <pipeline> defaults unset <name>
        meep edit <pipeline> const set <Name>=<value>       declare or change a constant
        meep edit <pipeline> const unset <Name>
        meep edit <pipeline> plugin add <address or path>   add a #plugin line
        meep edit <pipeline> plugin remove <name>
        meep edit <pipeline> <module> takes one edit:
          set <name>=<value>         set a parameter, such as set count=5
          set <value>                set the value after the verb, such as set 10s
          set @<modifier>[=<value>]  set a modifier, such as set @buffer=100
          unset [<name> | @<modifier>]
          rename [<name>]            name it, or with no name, take its name away
          add <module line>          add a branch under it
          insert <module line>       put a module between it and those under it
          remove                     remove it; those under it move up
          remove-branch              remove it and everything under it
        """;

    // An edit as the command line writes it. defaults, const and plugin aren't modules, but the rest of the file.
    private static EditItem? EditOf(string module, List<string> words) => (module, words) switch
    {
        ("defaults", ["set", var setting]) when Setting(setting) is var (name, value) => new EditItem(null, "set-default", name, value),
        ("defaults", ["unset", var name]) => new EditItem(null, "unset-default", name),
        ("const", ["set", var setting]) when Setting(setting) is var (name, value) => new EditItem(null, "set-const", name, value),
        ("const", ["unset", var name]) => new EditItem(null, "unset-const", name),
        ("plugin" or "plugins", ["add", .. var line]) when line.Count > 0 => new EditItem(null, "add-plugin", null, string.Join(' ', line)),
        ("plugin" or "plugins", ["remove", var name]) => new EditItem(null, "remove-plugin", name),
        ("defaults" or "const" or "plugin" or "plugins", _) => null,
        _ => ModuleEditOf(module, words),
    };

    private static (string Name, string Value)? Setting(string text) =>
        text.IndexOf('=', StringComparison.Ordinal) is > 0 and var at && IsName(text[..at]) ? (text[..at], text[(at + 1)..]) : null;

    private static EditItem? ModuleEditOf(string module, List<string> words) => words switch
    {
        ["set", var setting] when setting.StartsWith('@') => Modifier(module, setting),
        ["set", var setting] when setting.IndexOf('=', StringComparison.Ordinal) is > 0 and var at && IsName(setting[..at]) =>
            new EditItem(module, "set", setting[..at], setting[(at + 1)..]),
        ["set", .. var value] when value.Count > 0 => new EditItem(module, "set", null, string.Join(' ', value)),
        ["unset"] => new EditItem(module, "unset"),
        ["unset", var name] when name.StartsWith('@') => new EditItem(module, "remove-modifier", name[1..]),
        ["unset", var name] => new EditItem(module, "unset", name),
        ["rename"] => new EditItem(module, "rename"),
        ["rename", var name] => new EditItem(module, "rename", null, name),
        ["add" or "insert", .. var line] when line.Count > 0 => new EditItem(module, words[0], null, string.Join(' ', line)),
        ["remove"] => new EditItem(module, "remove"),
        ["remove-branch"] => new EditItem(module, "remove-branch"),
        _ => null,
    };

    private static EditItem Modifier(string module, string setting)
    {
        var at = setting.IndexOf('=', StringComparison.Ordinal);
        return at < 0 ? new EditItem(module, "set-modifier", setting[1..]) : new EditItem(module, "set-modifier", setting[1..at], setting[(at + 1)..]);
    }

    private static bool IsName(string text) => text.Length > 0 && char.IsLetter(text[0]) && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The edit's words, and how to save it: with a commit message, on a branch, or not at all.</summary>
    private sealed record EditOptions(List<string> Edit, string? Message, string? Branch, bool Save)
    {
        public static EditOptions Parse(string[] words)
        {
            string? message = null;
            string? branch = null;
            var save = true;
            var edit = new List<string>();
            for (var i = 0; i < words.Length; i++)
            {
                switch (words[i])
                {
                    case "--message" when i + 1 < words.Length:
                        message = words[++i];
                        break;
                    case "--branch" when i + 1 < words.Length:
                        branch = words[++i];
                        break;
                    case "--no-save":
                        save = false;
                        break;
                    default:
                        edit.Add(words[i]);
                        break;
                }
            }
            return new EditOptions(edit, message, branch, save);
        }
    }
}
