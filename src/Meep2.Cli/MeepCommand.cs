using System.Reflection;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;

namespace Meep2.Cli;

/// <summary>The meep command line, separate from Program so it can be tested in-process.</summary>
public static class MeepCommand
{
    private const string Usage = """
        Usage:
          meep run <file.meep>    Run a pipeline until it halts, or until Ctrl+C
          meep --version          Show the version
          meep --help             Show this help
        """;

    /// <summary>
    /// Runs a command. Returns the exit code: 0 if it ran, 1 if the pipeline file couldn't be read or loaded, 2 for
    /// bad usage, and 3 if the pipeline's error policy stopped it. Errors the pipeline leaves unhandled are written to
    /// <paramref name="error"/>, with notices such as a branch the error policy stopped, and don't change the exit
    /// code (Q84).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        switch (args)
        {
            case ["--help"] or ["-h"] or ["help"]:
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return 0;
            case ["--version"] or ["version"]:
                await output.WriteLineAsync($"meep {Version} on .NET {Environment.Version}").ConfigureAwait(false);
                return 0;
            case ["run", var file]:
                return await RunFileAsync(file, output, error, cancellationToken).ConfigureAwait(false);
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

    // Runs the fixed bootstrap pipeline: Read the file, Parse it, Build the pipeline, Host it.
    private static async Task<int> RunFileAsync(string file, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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
            UnhandledError = e => Log($"error from {e.EmittedBy?.ToString() ?? "the pipeline"}: {e.Error}"),
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
            builder.Add(() => new Build(registry, hosted)),
            builder.Add(() => new Host(error)));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync(cancellationToken).ConfigureAwait(false);
        return exitCode;
    }
}
