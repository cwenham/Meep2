using System.Reflection;
using Meep2.Modules;
using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary>The meep command line, separate from Program so it can be tested in-process.</summary>
public static class MeepCommand
{
    private const string Usage = """
        Usage:
          meep run [--dev] [--watch] [--otlp <url>] [--sample <share>] [--meep-host-public] [--meep-hosts <hosts>] [--detach] <pipeline>
                                                Run a pipeline until it halts, or until Ctrl+C. It's a file,
                                                - for standard input, an https:// URL, or a file in Git, as
                                                git+https://host/repo.git#file.meep@ref. One from a URL or Git
                                                needs a signature from a key you trust (meep sign), or a pin:
                                                #sha256=… on a URL, a commit's full SHA as Git's ref.
                                                --dev loads unsigned plugins from this machine.
                                                --watch changes the running pipeline when the file changes.
                                                --otlp sends spans and metrics to an OpenTelemetry collector.
                                                --sample records that share of traces: 0.1 unless it says (1 is all).
                                                --meep-host-public tells clients anywhere this run's ID, when they
                                                ask a Listen, not only those on private addresses.
                                                --meep-hosts gives these hosts, known to be Meep, where messages
                                                come from, as studio.local,10.0.0.5:8080.
                                                --detach runs it in the background, with its output in a log.
          meep daemon [--dev] [--otlp <url>] [--sample <share>] [--meep-host-public] [--meep-hosts <hosts>] [--config <file>] [<file.meep> ...]
                                                Run the daemon, which hosts pipelines, until Ctrl+C. It starts
                                                the files given, and those its settings file lists.
          meep daemon install [options] [<file.meep> ...]
                                                Run the daemon as a service that starts when you log in
          meep daemon uninstall                 Stop and remove the service
          meep daemon stop                      Stop the daemon
          meep daemon socket                    Show the daemon's socket, in full, and its ID, a line each,
                                                as for ssh host meep daemon socket before ssh -L
          meep daemon token <name> <capabilities>
                                                Make a token for a client that may only observe, inject or control
          meep daemon revoke <name>             Take a token away, closing what was opened with it
          meep start [--dev] [--watch] <pipeline>
                                                Start a pipeline in the daemon, from any address meep run takes;
                                                - sends standard input, named with --name <name>
          meep list                             List the daemon's pipelines, and those that ended recently
          meep stop <pipeline>                  Stop one of them, letting its messages finish
          meep reload <pipeline> [--force]      Change a running pipeline to match its file, plugins included.
                                                --force goes ahead though it undoes edits not in the file.
          meep source <pipeline>                Show the text a running pipeline runs from
          meep edit <pipeline> <module> <edit> [--message <text>] [--branch <name>] [--no-save]
                                                Edit a running pipeline, and commit its file to pending, signed
                                                with your SSH key, or write the file if it isn't in Git. The edit
                                                is one of: set <name>=<value>, set <value>,
                                                set @<modifier>[=<value>], unset [<name> | @<modifier>],
                                                rename [<name>], add <module line>, insert <module line>,
                                                remove or remove-branch. In place of a module, defaults and
                                                const take set <name>=<value> and unset <name>, and plugin
                                                takes add <address> and remove <name>.
          meep stats <pipeline> [--watch]       Show each module's counts, once or every second
          meep tap <pipeline> <module> [--where <condition>] [--sample <share>]
                                                Show the messages passing a module, until Ctrl+C
          meep inject <pipeline> <module> <text> [--json]
                                                Send a test message into a module
          meep keys trust <name> <type> <key>   Trust plugins signed with someone's SSH key,
          meep keys trust <name> <file.pub>       as their .pub file has it,
          meep keys trust github:<user>           or as their GitHub account has it
          meep keys list                        Show the keys you trust
          meep sign <folder or files> [--key <private key>]
                                                Sign a plugin's files, or .meep pipeline files, with your SSH key,
                                                using ssh-keygen
          meep pin <file.meep> ...              Pin the files a pipeline includes on their #include lines, and
                                                show its own SHA-256, for #sha256= on its URL
          meep lsp                              Run the language server for editors, over standard input and output
          meep --version                        Show the version
          meep --help                           Show this help
        """;

    /// <summary>
    /// Runs a command. Returns the exit code: 0 if it ran, 1 if the pipeline file couldn't be read or loaded, 2 for
    /// bad usage, and 3 if the pipeline's error policy stopped it. Errors the pipeline leaves unhandled are written to
    /// <paramref name="error"/>, with notices such as a branch the error policy stopped, and don't change the exit
    /// code (Q84).
    /// </summary>
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        RunAsync(args, output, error, home: null, cancellationToken);

    /// <summary>
    /// Runs a command, with <paramref name="home"/> as Meep's folder instead of <c>MEEP_HOME</c> or <c>~/.meep</c>,
    /// for tests.
    /// </summary>
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string? home, CancellationToken cancellationToken) =>
        RunAsync(args, output, error, home is null ? PluginOptions.ForUser() : new PluginOptions { Home = home }, cancellationToken);

    /// <summary>Runs a command with these plugin settings, for tests. <c>--dev</c> turns developer mode on.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, PluginOptions plugins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(plugins);
        var context = new CommandContext(plugins, output, error, cancellationToken);
        return args switch
        {
            ["--help"] or ["-h"] or ["help"] => await WriteAsync(output, Usage, 0).ConfigureAwait(false),
            ["--version"] or ["version"] => await WriteAsync(output, $"meep {Version} on .NET {Environment.Version}", 0).ConfigureAwait(false),
            ["run", .. var rest] => await RunAsync(context, rest).ConfigureAwait(false),
            ["daemon", "token", var name, .. var capabilities] when capabilities.Length > 0 =>
                await DaemonCommands.TokenAsync(name, string.Join(',', capabilities), context.Options(), output, error).ConfigureAwait(false),
            ["daemon", "revoke", var name] => await DaemonCommands.RevokeAsync(name, context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["daemon", "install", .. var rest] => HostSettings.Parse(rest, error) is { Detach: false } install
                ? await DaemonCommands.InstallAsync(install, context.Options(), output, error, cancellationToken).ConfigureAwait(false)
                : 2,
            ["daemon", "socket"] => await DaemonCommands.SocketAsync(context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["daemon", "uninstall"] => await DaemonCommands.UninstallAsync(context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["daemon", .. var rest] when rest is not ["stop"] => await DaemonAsync(context, rest).ConfigureAwait(false),
            ["daemon", "stop"] or ["start", ..] or ["list"] or ["stop", _] or ["stats", ..] or ["tap", ..] or ["inject", ..] or ["reload", ..] or ["source", _] or ["edit", ..] =>
                await ClientAsync(context, args).ConfigureAwait(false),
            ["keys", "trust", .. var key] when key.Length > 0 => await KeysCommands.TrustAsync(key, context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["keys", "trust"] => await WriteAsync(error, KeysCommands.TrustUsage, 2).ConfigureAwait(false),
            ["keys", "list"] => await KeysCommands.ListAsync(context.Options(), output, error).ConfigureAwait(false),
            ["sign", .. var rest] when rest is [.., "--key", _] && rest.Length > 2 =>
                await KeysCommands.SignAsync(rest[..^2], rest[^1], context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["sign", .. var rest] when rest.Length > 0 && !rest.Contains("--key") =>
                await KeysCommands.SignAsync(rest, keyPath: null, context.Options(), output, error, cancellationToken).ConfigureAwait(false),
            ["pin", .. var rest] when rest.Length > 0 => await Pinning.PinAsync(rest, output, error).ConfigureAwait(false),
            ["lsp"] => await LspAsync(context).ConfigureAwait(false),
            [] => await WriteAsync(error, Usage, 2).ConfigureAwait(false),
            _ => await NotUnderstoodAsync(error, args).ConfigureAwait(false),
        };
    }

    private static async Task<int> WriteAsync(TextWriter writer, string text, int code)
    {
        await writer.WriteLineAsync(text).ConfigureAwait(false);
        return code;
    }

    private static async Task<int> NotUnderstoodAsync(TextWriter error, string[] args)
    {
        await error.WriteLineAsync($"meep doesn't understand: {string.Join(' ', args)}").ConfigureAwait(false);
        await error.WriteLineAsync(Usage).ConfigureAwait(false);
        return 2;
    }

    // meep run: runs one pipeline here, or in the background with --detach.
    private static async Task<int> RunAsync(CommandContext context, string[] rest)
    {
        if (HostSettings.Parse(rest, context.Error) is not { Files: [_], Config: null } run)
            return await WriteAsync(context.Error, "meep run takes one pipeline file, with --dev, --watch, --otlp <url>, --sample <share>, --meep-host-public, --meep-hosts <hosts> or --detach.", 2).ConfigureAwait(false);
        if (run.Detach)
            return await Detaching.StartAsync(run, context.Plugins.Home, context.Output, context.Error, context.Cancellation).ConfigureAwait(false);
        // A run is a Meep host of its own, with an ID for this run: other Meep hosts count what it sends by
        // it, and its spans and metrics carry it as service.instance.id.
        var instance = Guid.NewGuid().ToString();
        using var telemetry = Telemetry.Start(run.Otlp, run.Sample, instance);
        if (run.MeepHostPublic)
            MeepHosts.AnswerAnyone = true;
        MeepHosts.Configure(run.MeepHosts);
        return await new FileRun(context with { Plugins = context.Options(run.DeveloperMode) }, run.Files[0], run.Watch, instance).RunAsync().ConfigureAwait(false);
    }

    // meep daemon: runs the daemon in the foreground.
    private static async Task<int> DaemonAsync(CommandContext context, string[] rest)
    {
        if (HostSettings.Parse(rest, context.Error) is not { Detach: false } daemon)
            return await WriteAsync(context.Error, "meep daemon takes pipeline files to start, with --dev, --otlp <url>, --sample <share>, --meep-host-public, --meep-hosts <hosts> or --config <file>. To run it in the background, use meep daemon install.", 2).ConfigureAwait(false);
        return await DaemonCommands.RunAsync(daemon, context.Options(daemon.DeveloperMode), context.Output, context.Error, context.Cancellation).ConfigureAwait(false);
    }

    // The commands that ask the daemon for something.
    private static async Task<int> ClientAsync(CommandContext context, string[] args)
    {
        var code = await DaemonCommands.ClientAsync(args, context.Options(), context.Output, context.Error, context.Cancellation).ConfigureAwait(false);
        return code >= 0 ? code : await NotUnderstoodAsync(context.Error, args).ConfigureAwait(false);
    }

    // The language server for editors: LSP over standard input and output.
    private static async Task<int> LspAsync(CommandContext context)
    {
        await using var input = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        return await new LanguageServer(input, stdout, context.Options() with { Offline = true }).RunAsync(context.Cancellation).ConfigureAwait(false);
    }

    private static string Version =>
        typeof(MeepCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MeepCommand).Assembly.GetName().Version?.ToString()
        ?? "?";
}
