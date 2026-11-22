using System.Globalization;
using System.Text.Json;
using Meep2.Daemon;
using Meep2.Plugins;
using Meep2.Runtime;

namespace Meep2.Cli;

/// <summary>
/// meep daemon, and the commands that talk to it: start, list, stop, stats, tap and inject (goal 8, Q63, Q64, Q72).
/// A client shows the token in <c>MEEP_TOKEN</c>, if it's set.
/// </summary>
internal static class DaemonCommands
{
    // meep daemon: runs in the foreground until Ctrl+C or meep daemon stop, starting the pipelines its command line
    // and settings file name.
    public static async Task<int> RunAsync(HostSettings settings, PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        DaemonConfig config;
        try
        {
            if (settings.Config is { } named && !File.Exists(named))
                throw new FormatException($"There's no settings file {Path.GetFullPath(named)}.");
            config = DaemonConfig.Load(settings.Config ?? DaemonConfig.DefaultPath(plugins.Home));
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        using var telemetry = Telemetry.Start(settings.Otlp ?? config.Otlp, settings.Sample ?? config.Sample, HostIdentity.IdOf(plugins.Home));
        await using var daemon = new MeepDaemon(new DaemonOptions
        {
            Plugins = plugins,
            Output = output,
            Log = error,
            Start = [.. config.Start, .. settings.Files.Select(file => new DaemonStart(Path.GetFullPath(file), Watch: settings.Watch))],
            MeepHosts = [.. settings.MeepHosts, .. config.MeepHosts],
            MeepHostPublic = config.MeepHostPublic || settings.MeepHostPublic,
        });
        try
        {
            await daemon.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (DaemonException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    // meep daemon install: the daemon as a service that starts when you log in.
    public static async Task<int> InstallAsync(HostSettings settings, PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!DaemonService.Supported)
        {
            await error.WriteLineAsync("meep daemon install knows launchd, on macOS, and systemd, on Linux. On Windows, run meep daemon in a terminal of its own for now.").ConfigureAwait(false);
            return 2;
        }
        var missing = settings.Files.FirstOrDefault(file => !File.Exists(file));
        if (missing is not null)
        {
            await error.WriteLineAsync($"There's no file {Path.GetFullPath(missing)}.").ConfigureAwait(false);
            return 1;
        }
        var command = MeepExecutable.Command().Concat(["daemon", .. settings.Options(), .. settings.Files.Select(Path.GetFullPath)]).ToList();
        var plan = DaemonService.Plan(command, plugins.Home, MeepExecutable.Environment(plugins.Home));
        try
        {
            await DaemonService.InstallAsync(plan, plugins.Home, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Wrote {plan.File}, but couldn't start it: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        await output.WriteLineAsync($"""
            Installed the daemon as a {plan.System} service, in {plan.File}
            It's starting now, and will start whenever you log in. It's restarted if it fails.
            Its log: {plan.Logs}
            Remove it with: meep daemon uninstall
            """).ConfigureAwait(false);
        return 0;
    }

    // meep daemon uninstall: stops the service and removes it.
    public static async Task<int> UninstallAsync(PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!DaemonService.Supported)
        {
            await error.WriteLineAsync("meep daemon install and uninstall know launchd, on macOS, and systemd, on Linux.").ConfigureAwait(false);
            return 2;
        }
        var plan = DaemonService.Plan(MeepExecutable.Command(), plugins.Home, new Dictionary<string, string>());
        try
        {
            var removed = await DaemonService.UninstallAsync(plan, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(removed ? $"Stopped the daemon's service and removed {plan.File}." : "The daemon isn't installed as a service.").ConfigureAwait(false);
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    // meep daemon token <name> <capabilities>: a token that gives a client only those capabilities.
    public static async Task<int> TokenAsync(string name, string capabilities, PluginOptions plugins, TextWriter output, TextWriter error)
    {
        try
        {
            var options = new DaemonOptions { Plugins = plugins };
            var granted = CapabilityNames.Parse(capabilities);
            var token = DaemonTokens.Create(options.TokensFile, name, granted);
            await output.WriteLineAsync($"""
                Made the token {name}, which can {string.Join(", ", CapabilityNames.Of(granted))}. It's only shown this once:
                  {token}
                A client uses it by setting MEEP_TOKEN, as in: MEEP_TOKEN={token} meep list
                """).ConfigureAwait(false);
            return 0;
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
    }

    // meep daemon socket: where the daemon's control socket is, in full, then its ID, a line each, so that
    // ssh host meep daemon socket can come before forwarding it with ssh -L (Q209). The ID is the running daemon's,
    // or the one it will take when it starts; whether it's running goes to standard error.
    public static async Task<int> SocketAsync(PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var socket = Path.GetFullPath(DaemonOptions.DefaultSocket(plugins.Home));
        string id;
        string? note = null;
        using (var client = new DaemonClient(socket, Environment.GetEnvironmentVariable("MEEP_TOKEN") is { Length: > 0 } token ? token : null))
        {
            try
            {
                var info = await client.InfoAsync(cancellationToken).ConfigureAwait(false);
                id = info.Id ?? HostIdentity.IdOf(plugins.Home);
            }
            catch (DaemonException exception)
            {
                id = HostIdentity.IdOf(plugins.Home);
                note = exception.Status == 0
                    ? "No daemon is running there now; the ID is the one it will take when it starts."
                    : $"The daemon is running, but wouldn't say who it is: {exception.Message}";
            }
        }
        await output.WriteLineAsync(socket).ConfigureAwait(false);
        await output.WriteLineAsync(id).ConfigureAwait(false);
        if (note is not null)
            await error.WriteLineAsync(note).ConfigureAwait(false);
        return 0;
    }

    // The commands that ask the daemon to do something: they connect, ask, and report.
    public static async Task<int> ClientAsync(string[] args, PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(DaemonOptions.DefaultSocket(plugins.Home), Environment.GetEnvironmentVariable("MEEP_TOKEN") is { Length: > 0 } token ? token : null);
        var cli = new CliContext(client, output, error, cancellationToken);
        try
        {
            switch (args)
            {
                case ["daemon", "stop"]:
                    await client.StopDaemonAsync(cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync("Asked the daemon to stop. It lets its pipelines' messages finish first.").ConfigureAwait(false);
                    return 0;
                case ["start", .. var rest] when Named(rest, out var named) is { } start && StartFile(start) is { } file:
                    return await StartAsync(cli, file, named, start.Contains("--dev"), start.Contains("--watch")).ConfigureAwait(false);
                case ["reload", .. var words] when words.Where(w => w != "--force").ToList() is [var name] && words.All(w => w == "--force" || !w.StartsWith("--", StringComparison.Ordinal)):
                    await WriteChangeAsync($"Reloaded {name}", await client.ReloadAsync(name, words.Contains("--force"), cancellationToken).ConfigureAwait(false), output).ConfigureAwait(false);
                    return 0;
                case ["source", var name]:
                    await output.WriteAsync((await client.SourceAsync(name, cancellationToken).ConfigureAwait(false)).Text).ConfigureAwait(false);
                    return 0;
                case ["edit", var name, var module, .. var rest]:
                    return await EditAsync(cli, name, module, rest).ConfigureAwait(false);
                case ["list"]:
                    await ListAsync(cli).ConfigureAwait(false);
                    return 0;
                case ["stop", var name]:
                    return await StopAsync(cli, name).ConfigureAwait(false);
                case ["stats", var name]:
                    WriteStats(await client.GetAsync(name, cancellationToken).ConfigureAwait(false), output);
                    return 0;
                case ["stats", var name, "--watch"]:
                    await WatchAsync(cli, name).ConfigureAwait(false);
                    return 0;
                case ["stats", "--watch", var name]:
                    await WatchAsync(cli, name).ConfigureAwait(false);
                    return 0;
                case ["tap", var pipeline, var module, .. var rest]:
                    return await TapAsync(cli, pipeline, module, rest).ConfigureAwait(false);
                case ["inject", var pipeline, var module, var content]:
                    return await InjectAsync(cli, pipeline, module, content, json: false).ConfigureAwait(false);
                case ["inject", var pipeline, var module, var content, "--json"]:
                    return await InjectAsync(cli, pipeline, module, content, json: true).ConfigureAwait(false);
                case ["inject", "--json", var pipeline, var module, var content]:
                    return await InjectAsync(cli, pipeline, module, content, json: true).ConfigureAwait(false);
                default:
                    return -1;
            }
        }
        catch (DaemonException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            foreach (var detail in exception.Details)
                await error.WriteLineAsync(detail).ConfigureAwait(false);
            if (exception.Status == 0 && args is ["start", .. var start] && start.FirstOrDefault(word => !word.StartsWith("--", StringComparison.Ordinal)) is { } file)
                await error.WriteLineAsync($"Or run it here, without a daemon:         meep run {file}   (add --detach to run it in the background)").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0; // Ctrl+C while watching or tapping
        }
    }

    // meep start's file, among --dev and --watch; null if there's anything else.
    // --name <name>, taken out of the words.
    private static string[]? Named(string[] words, out string? name)
    {
        name = null;
        var at = Array.IndexOf(words, "--name");
        if (at < 0)
            return words;
        if (at + 1 >= words.Length)
            return null;
        name = words[at + 1];
        return [.. words[..at], .. words[(at + 2)..]];
    }

    private static string? StartFile(string[] words)
    {
        var files = words.Where(word => !word.StartsWith("--", StringComparison.Ordinal)).ToList();
        return files.Count == 1 && words.All(word => !word.StartsWith("--", StringComparison.Ordinal) || word is "--dev" or "--watch") ? files[0] : null;
    }

    private static async Task<int> StartAsync(CliContext cli, string file, string? name, bool developerMode, bool watch)
    {
        // Standard input is sent as text, since the daemon can't read this terminal (goal 7). Other addresses go as
        // they are, and the daemon reads them itself.
        var started = file == "-"
            ? await cli.Client.StartTextAsync(name ?? "stdin", await Console.In.ReadToEndAsync(cli.Cancellation).ConfigureAwait(false), developerMode, cli.Cancellation).ConfigureAwait(false)
            : await cli.Client.StartAsync(file, developerMode, watch, cli.Cancellation).ConfigureAwait(false);
        await cli.Output.WriteLineAsync($"Started {started.Name}, from {started.Path}.{(watch ? " It reloads whenever the file changes." : "")}").ConfigureAwait(false);
        foreach (var warning in started.Warnings)
            await cli.Output.WriteLineAsync(warning).ConfigureAwait(false);
        return 0;
    }

    // meep edit <pipeline> <module> <edit> [--message <text>] [--branch <name>] [--no-save]
    private static async Task<int> EditAsync(CliContext cli, string pipeline, string module, string[] words)
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
        if (EditOf(module, edit) is not { } item)
        {
            await cli.Error.WriteLineAsync(EditUsage).ConfigureAwait(false);
            return 2;
        }
        var result = await cli.Client.EditAsync(pipeline, new EditRequest([item], message, branch, save), cli.Cancellation).ConfigureAwait(false);
        await WriteChangeAsync($"Edited {pipeline}", result, cli.Output).ConfigureAwait(false);
        if (result.Saved is { } saved)
        {
            await cli.Output.WriteLineAsync($"Committed to {saved.Branch} as {saved.Sha[..7]}, in {saved.Repository}.").ConfigureAwait(false);
            await cli.Output.WriteLineAsync(saved.Unsigned is { } unsigned ? $"It isn't signed: {unsigned}." : "It's signed with your SSH key.").ConfigureAwait(false);
        }
        else if (result.WrittenTo is { } file)
        {
            await cli.Output.WriteLineAsync($"Wrote it to {file}, which isn't in a Git repository.").ConfigureAwait(false);
        }
        else if (result.NotSaved is { } why)
        {
            await cli.Output.WriteLineAsync($"Not saved: {why}").ConfigureAwait(false);
        }
        return 0;
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

    // What a reload or an edit did, a line for each kind of change.
    private static async Task WriteChangeAsync(string what, ChangeResult result, TextWriter output)
    {
        var lines = new List<string>();
        void Line(string label, IReadOnlyList<string> modules)
        {
            if (modules.Count > 0)
                lines.Add($"  {label}: {string.Join(", ", modules)}");
        }
        Line("added", result.Added);
        Line("removed", result.Removed);
        Line("took new parameters", result.Reconfigured);
        Line("made again", result.Replaced);
        if (result.SettingsChanged)
            lines.Add("  its defaults changed");
        await output.WriteLineAsync(lines.Count == 0 ? $"{what}: nothing changed." : $"{what}:").ConfigureAwait(false);
        foreach (var line in lines)
            await output.WriteLineAsync(line).ConfigureAwait(false);
        foreach (var warning in result.Diagnostics)
            await output.WriteLineAsync(warning).ConfigureAwait(false);
    }

    private static async Task ListAsync(CliContext cli)
    {
        var pipelines = await cli.Client.ListAsync(cli.Cancellation).ConfigureAwait(false);
        if (pipelines.Count == 0)
        {
            await cli.Output.WriteLineAsync("Nothing is running.").ConfigureAwait(false);
            return;
        }
        var width = Math.Max(4, pipelines.Max(p => p.Name.Length));
        await cli.Output.WriteLineAsync($"{"NAME".PadRight(width)}  STATE       SINCE     FILE").ConfigureAwait(false);
        foreach (var pipeline in pipelines)
        {
            var since = Time(pipeline.Ended ?? pipeline.Started);
            await cli.Output.WriteLineAsync($"{pipeline.Name.PadRight(width)}  {pipeline.State,-10}  {since,-8}  {pipeline.Path}").ConfigureAwait(false);
            if (Why(pipeline.State, pipeline.Reason, pipeline.Restarts) is { } why)
                await cli.Output.WriteLineAsync($"{"".PadRight(width)}  {why}").ConfigureAwait(false);
        }
    }

    // Why a pipeline has ended or is restarting, as a sentence; nothing for one that's running as it should.
    private static string? Why(string state, string? reason, int restarts)
    {
        var restarted = restarts switch { 0 => "", 1 => " It has been restarted once.", _ => $" It has been restarted {restarts} times." };
        return state switch
        {
            PipelineStates.Restarting or PipelineStates.Failed or PipelineStates.Finished when reason is not null =>
                $"{char.ToUpperInvariant(reason[0])}{reason[1..]}.{restarted}",
            PipelineStates.Running when restarts > 0 => restarted.Trim(),
            _ => null,
        };
    }

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private static async Task<int> StopAsync(CliContext cli, string name)
    {
        var before = await cli.Client.GetAsync(name, cli.Cancellation).ConfigureAwait(false);
        await cli.Client.StopAsync(name, cli.Cancellation).ConfigureAwait(false);
        await cli.Output.WriteLineAsync(before.Ended is null ? $"Stopped {name}." : $"Took {name} off the list. It had {before.State} at {Time(before.Ended.Value)}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task WatchAsync(CliContext cli, string name)
    {
        await foreach (var detail in cli.Client.WatchAsync(name, TimeSpan.FromSeconds(1), cli.Cancellation).ConfigureAwait(false))
        {
            await cli.Output.WriteLineAsync(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)).ConfigureAwait(false);
            WriteStats(detail, cli.Output);
            await cli.Output.WriteLineAsync().ConfigureAwait(false);
        }
    }

    // A table of a pipeline's modules and their counts, after why it ended, if it has.
    private static void WriteStats(PipelineDetail detail, TextWriter output)
    {
        if (detail.Ended is { } ended)
            output.WriteLine($"{detail.Name} {detail.State} at {Time(ended)}. These are its final counts.");
        if (Why(detail.State, detail.Reason, detail.Restarts) is { } why)
            output.WriteLine(why);
        var width = Math.Max(6, detail.Modules.Count == 0 ? 0 : detail.Modules.Max(m => m.Module.Length));
        output.WriteLine($"{"MODULE".PadRight(width)}  {"RECEIVED",9}  {"HANDLED",9}  {"EMITTED",9}  {"PASSED",9}  {"QUEUED",7}  {"DROPPED",8}  {"EXPIRED",8}  {"FAILED",7}");
        foreach (var module in detail.Modules)
        {
            var c = module.Counts;
            output.WriteLine($"{module.Module.PadRight(width)}  {c.Received,9}  {c.Handled,9}  {c.Emitted,9}  {c.Passed,9}  {c.Queued,7}  {c.Dropped,8}  {c.Expired,8}  {c.Failed,7}");
        }
    }

    private static async Task<int> TapAsync(CliContext cli, string pipeline, string module, string[] options)
    {
        string? where = null;
        var sample = 1.0;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--where" when i + 1 < options.Length:
                    where = options[++i];
                    break;
                case "--sample" when i + 1 < options.Length && double.TryParse(options[i + 1], CultureInfo.InvariantCulture, out sample) && sample is > 0 and <= 1:
                    i++;
                    break;
                default:
                    await cli.Error.WriteLineAsync("meep tap takes --where <condition> and --sample <share of messages, such as 0.1>.").ConfigureAwait(false);
                    return 2;
            }
        }

        long dropped = 0;
        await foreach (var (message, droppedSoFar) in cli.Client.TapAsync(pipeline, module, where, sample, cli.Cancellation).ConfigureAwait(false))
        {
            if (droppedSoFar > dropped)
            {
                await cli.Error.WriteLineAsync($"(dropped {droppedSoFar - dropped} messages while catching up)").ConfigureAwait(false);
                dropped = droppedSoFar;
            }
            await cli.Output.WriteLineAsync(JsonSerializer.Serialize(message, TapJson.Default.JsonElement)).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> InjectAsync(CliContext cli, string pipeline, string module, string content, bool json)
    {
        var injected = await cli.Client.InjectAsync(pipeline, module, content, json, cli.Cancellation).ConfigureAwait(false);
        await cli.Output.WriteLineAsync($"Sent {injected.Type} {injected.Id} into {injected.Module}.").ConfigureAwait(false);
        return 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class TapJson : System.Text.Json.Serialization.JsonSerializerContext;
