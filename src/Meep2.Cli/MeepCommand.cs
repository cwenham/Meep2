using System.Reflection;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;

namespace Meep2.Cli;

/// <summary>The meep command line, separate from Program so it can be tested in-process.</summary>
public static class MeepCommand
{
    private const string Usage = """
        Usage:
          meep run [--dev] [--otlp <url>] [--sample <share>] [--detach] <file.meep>
                                                Run a pipeline until it halts, or until Ctrl+C.
                                                --dev loads unsigned plugins from this machine.
                                                --otlp sends spans and metrics to an OpenTelemetry collector.
                                                --sample records that share of traces: 0.1 unless it says (1 is all).
                                                --detach runs it in the background, with its output in a log.
          meep daemon [--dev] [--otlp <url>] [--sample <share>] [--config <file>] [<file.meep> ...]
                                                Run the daemon, which hosts pipelines, until Ctrl+C. It starts
                                                the files given, and those its settings file lists.
          meep daemon install [options] [<file.meep> ...]
                                                Run the daemon as a service that starts when you log in
          meep daemon uninstall                 Stop and remove the service
          meep daemon stop                      Stop the daemon
          meep daemon token <name> <capabilities>
                                                Make a token for a client that may only observe, inject or control
          meep start [--dev] <file.meep>        Start a pipeline in the daemon
          meep list                             List the daemon's pipelines, and those that ended recently
          meep stop <pipeline>                  Stop one of them, letting its messages finish
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
                                                Sign a plugin's files with your SSH key, using ssh-keygen
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
        PluginOptions Options(bool developerMode = false) => plugins with { DeveloperMode = plugins.DeveloperMode || developerMode };

        switch (args)
        {
            case ["--help"] or ["-h"] or ["help"]:
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return 0;
            case ["--version"] or ["version"]:
                await output.WriteLineAsync($"meep {Version} on .NET {Environment.Version}").ConfigureAwait(false);
                return 0;
            case ["run", .. var rest]:
            {
                if (HostSettings.Parse(rest, error) is not { Files: [_], Config: null } run)
                {
                    await error.WriteLineAsync("meep run takes one pipeline file, with --dev, --otlp <url>, --sample <share> or --detach.").ConfigureAwait(false);
                    return 2;
                }
                if (run.Detach)
                    return await Detaching.StartAsync(run, plugins.Home, output, error, cancellationToken).ConfigureAwait(false);
                using var telemetry = Telemetry.Start(run.Otlp, run.Sample);
                return await RunFileAsync(run.Files[0], Options(run.DeveloperMode), output, error, cancellationToken).ConfigureAwait(false);
            }
            case ["daemon", "token", var name, .. var capabilities] when capabilities.Length > 0:
                return await DaemonCommands.TokenAsync(name, string.Join(',', capabilities), Options(), output, error).ConfigureAwait(false);
            case ["daemon", "install", .. var rest]:
                return HostSettings.Parse(rest, error) is { Detach: false } install
                    ? await DaemonCommands.InstallAsync(install, Options(), output, error, cancellationToken).ConfigureAwait(false)
                    : 2;
            case ["daemon", "uninstall"]:
                return await DaemonCommands.UninstallAsync(Options(), output, error, cancellationToken).ConfigureAwait(false);
            case ["daemon", .. var rest] when rest is not ["stop"]:
            {
                if (HostSettings.Parse(rest, error) is not { Detach: false } daemon)
                {
                    await error.WriteLineAsync("meep daemon takes pipeline files to start, with --dev, --otlp <url>, --sample <share> or --config <file>. To run it in the background, use meep daemon install.").ConfigureAwait(false);
                    return 2;
                }
                return await DaemonCommands.RunAsync(daemon, Options(daemon.DeveloperMode), output, error, cancellationToken).ConfigureAwait(false);
            }
            case ["daemon", "stop"] or ["start", ..] or ["list"] or ["stop", _] or ["stats", ..] or ["tap", ..] or ["inject", ..]:
            {
                var code = await DaemonCommands.ClientAsync(args, Options(), output, error, cancellationToken).ConfigureAwait(false);
                if (code >= 0)
                    return code;
                await error.WriteLineAsync($"meep doesn't understand: {string.Join(' ', args)}").ConfigureAwait(false);
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return 2;
            }
            case ["keys", "trust", .. var key] when key.Length > 0:
                return await KeysCommands.TrustAsync(key, Options(), output, error, cancellationToken).ConfigureAwait(false);
            case ["keys", "trust"]:
                await error.WriteLineAsync(KeysCommands.TrustUsage).ConfigureAwait(false);
                return 2;
            case ["keys", "list"]:
                return await KeysCommands.ListAsync(Options(), output, error).ConfigureAwait(false);
            case ["sign", .. var rest] when rest is [.., "--key", _] && rest.Length > 2:
                return await KeysCommands.SignAsync(rest[..^2], rest[^1], Options(), output, error, cancellationToken).ConfigureAwait(false);
            case ["sign", .. var rest] when rest.Length > 0 && !rest.Contains("--key"):
                return await KeysCommands.SignAsync(rest, keyPath: null, Options(), output, error, cancellationToken).ConfigureAwait(false);
            case []:
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return 2;
            default:
                await error.WriteLineAsync($"meep doesn't understand: {string.Join(' ', args)}").ConfigureAwait(false);
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return 2;
        }
    }

    private static string Version =>
        typeof(MeepCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MeepCommand).Assembly.GetName().Version?.ToString()
        ?? "?";

    // Runs the fixed bootstrap pipeline: Read the file, Parse it, Install its plugins, Build the pipeline, Host it.
    private static async Task<int> RunFileAsync(string file, PluginOptions plugins, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var exitCode = 0;
        void Log(string text)
        {
            lock (error)
                error.WriteLine(text);
        }
        void Fail(int code, string? text)
        {
            Interlocked.CompareExchange(ref exitCode, code, 0);
            if (text is not null)
                Log(text);
        }

        var registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, output));

        var hosted = new PipelineOptions
        {
            UnhandledError = e => Log(e.AnsweredBy is { } answerer
                ? $"error from {e.EmittedBy?.ToString() ?? "the pipeline"}, answered by {answerer}: {e.Error}"
                : $"error from {e.EmittedBy?.ToString() ?? "the pipeline"}: {e.Error}"),
            Notice = Log,
        };
        var bootstrap = new PipelineOptions
        {
            UnhandledError = e =>
            {
                switch (e)
                {
                    case PipelineStoppedMessage:
                        Fail(3, text: null); // the pipeline's notice already said why
                        break;
                    case MeeplangErrorMessage meeplang:
                        Fail(1, string.Join(Environment.NewLine, meeplang.Diagnostics));
                        break;
                    default:
                        Fail(1, $"error: {e.Error}");
                        break;
                }
            },
            Notice = Log,
        };

        var builder = new PipelineBuilder("bootstrap", bootstrap);
        builder.Chain(
            builder.AddSource(() => new Read(new ReadParameters { Path = file })),
            builder.Add(() => new Parse()),
            builder.Add(() => new Install(registry, new PluginInstaller(plugins))),
            builder.Add(() => new Build(registry, hosted)),
            builder.Add(() => new Host(error, running: _ => Detaching.Running())));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync(cancellationToken).ConfigureAwait(false);
        return exitCode;
    }
}
