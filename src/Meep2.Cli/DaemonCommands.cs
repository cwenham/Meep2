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
        using var telemetry = Telemetry.Start(settings.Otlp ?? config.Otlp, settings.Sample ?? config.Sample);
        await using var daemon = new MeepDaemon(new DaemonOptions
        {
            Plugins = plugins,
            Output = output,
            Log = error,
            Start = [.. config.Start, .. settings.Files.Select(Path.GetFullPath)],
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

    // The commands that ask the daemon to do something: they connect, ask, and report.
    public static async Task<int> ClientAsync(string[] args, PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(DaemonOptions.DefaultSocket(plugins.Home), Environment.GetEnvironmentVariable("MEEP_TOKEN") is { Length: > 0 } token ? token : null);
        try
        {
            switch (args)
            {
                case ["daemon", "stop"]:
                    await client.StopDaemonAsync(cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync("Asked the daemon to stop. It lets its pipelines' messages finish first.").ConfigureAwait(false);
                    return 0;
                case ["start", var file]:
                    return await StartAsync(client, file, developerMode: false, output, cancellationToken).ConfigureAwait(false);
                case ["start", "--dev", var file]:
                    return await StartAsync(client, file, developerMode: true, output, cancellationToken).ConfigureAwait(false);
                case ["start", var file, "--dev"]:
                    return await StartAsync(client, file, developerMode: true, output, cancellationToken).ConfigureAwait(false);
                case ["list"]:
                    await ListAsync(client, output, cancellationToken).ConfigureAwait(false);
                    return 0;
                case ["stop", var name]:
                    return await StopAsync(client, name, output, cancellationToken).ConfigureAwait(false);
                case ["stats", var name]:
                    WriteStats(await client.GetAsync(name, cancellationToken).ConfigureAwait(false), output);
                    return 0;
                case ["stats", var name, "--watch"]:
                    await WatchAsync(client, name, output, cancellationToken).ConfigureAwait(false);
                    return 0;
                case ["stats", "--watch", var name]:
                    await WatchAsync(client, name, output, cancellationToken).ConfigureAwait(false);
                    return 0;
                case ["tap", var pipeline, var module, .. var rest]:
                    return await TapAsync(client, pipeline, module, rest, output, error, cancellationToken).ConfigureAwait(false);
                case ["inject", var pipeline, var module, var content]:
                    return await InjectAsync(client, pipeline, module, content, json: false, output, cancellationToken).ConfigureAwait(false);
                case ["inject", var pipeline, var module, var content, "--json"]:
                    return await InjectAsync(client, pipeline, module, content, json: true, output, cancellationToken).ConfigureAwait(false);
                case ["inject", "--json", var pipeline, var module, var content]:
                    return await InjectAsync(client, pipeline, module, content, json: true, output, cancellationToken).ConfigureAwait(false);
                default:
                    return -1;
            }
        }
        catch (DaemonException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            foreach (var detail in exception.Details)
                await error.WriteLineAsync(detail).ConfigureAwait(false);
            if (exception.Status == 0 && args is ["start", .. var start] && start.FirstOrDefault(word => word != "--dev") is { } file)
                await error.WriteLineAsync($"Or run it here, without a daemon:         meep run {file}   (add --detach to run it in the background)").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0; // Ctrl+C while watching or tapping
        }
    }

    private static async Task<int> StartAsync(DaemonClient client, string file, bool developerMode, TextWriter output, CancellationToken cancellationToken)
    {
        var started = await client.StartAsync(file, developerMode, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Started {started.Name}, from {started.Path}.").ConfigureAwait(false);
        foreach (var warning in started.Warnings)
            await output.WriteLineAsync(warning).ConfigureAwait(false);
        return 0;
    }

    private static async Task ListAsync(DaemonClient client, TextWriter output, CancellationToken cancellationToken)
    {
        var pipelines = await client.ListAsync(cancellationToken).ConfigureAwait(false);
        if (pipelines.Count == 0)
        {
            await output.WriteLineAsync("Nothing is running.").ConfigureAwait(false);
            return;
        }
        var width = Math.Max(4, pipelines.Max(p => p.Name.Length));
        await output.WriteLineAsync($"{"NAME".PadRight(width)}  STATE       SINCE     FILE").ConfigureAwait(false);
        foreach (var pipeline in pipelines)
        {
            var since = Time(pipeline.Ended ?? pipeline.Started);
            await output.WriteLineAsync($"{pipeline.Name.PadRight(width)}  {pipeline.State,-10}  {since,-8}  {pipeline.Path}").ConfigureAwait(false);
            if (Why(pipeline.State, pipeline.Reason, pipeline.Restarts) is { } why)
                await output.WriteLineAsync($"{"".PadRight(width)}  {why}").ConfigureAwait(false);
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

    private static async Task<int> StopAsync(DaemonClient client, string name, TextWriter output, CancellationToken cancellationToken)
    {
        var before = await client.GetAsync(name, cancellationToken).ConfigureAwait(false);
        await client.StopAsync(name, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(before.Ended is null ? $"Stopped {name}." : $"Took {name} off the list. It had {before.State} at {Time(before.Ended.Value)}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task WatchAsync(DaemonClient client, string name, TextWriter output, CancellationToken cancellationToken)
    {
        await foreach (var detail in client.WatchAsync(name, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)).ConfigureAwait(false);
            WriteStats(detail, output);
            await output.WriteLineAsync().ConfigureAwait(false);
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
        output.WriteLine($"{"MODULE".PadRight(width)}  {"RECEIVED",9}  {"HANDLED",9}  {"EMITTED",9}  {"QUEUED",7}  {"DROPPED",8}  {"EXPIRED",8}  {"FAILED",7}");
        foreach (var module in detail.Modules)
        {
            var c = module.Counts;
            output.WriteLine($"{module.Module.PadRight(width)}  {c.Received,9}  {c.Handled,9}  {c.Emitted,9}  {c.Queued,7}  {c.Dropped,8}  {c.Expired,8}  {c.Failed,7}");
        }
    }

    private static async Task<int> TapAsync(DaemonClient client, string pipeline, string module, string[] options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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
                    await error.WriteLineAsync("meep tap takes --where <condition> and --sample <share of messages, such as 0.1>.").ConfigureAwait(false);
                    return 2;
            }
        }

        long dropped = 0;
        await foreach (var (message, droppedSoFar) in client.TapAsync(pipeline, module, where, sample, cancellationToken).ConfigureAwait(false))
        {
            if (droppedSoFar > dropped)
            {
                await error.WriteLineAsync($"(dropped {droppedSoFar - dropped} messages while catching up)").ConfigureAwait(false);
                dropped = droppedSoFar;
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(message, TapJson.Default.JsonElement)).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> InjectAsync(DaemonClient client, string pipeline, string module, string content, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        var injected = await client.InjectAsync(pipeline, module, content, json, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Sent {injected.Type} {injected.Id} into {injected.Module}.").ConfigureAwait(false);
        return 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class TapJson : System.Text.Json.Serialization.JsonSerializerContext;
