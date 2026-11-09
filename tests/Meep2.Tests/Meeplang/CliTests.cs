using Meep2.Cli;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

public class CliTests
{
    private static async Task<(int ExitCode, string Output, string Error)> Meep(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Wait.For(MeepCommand.RunAsync(args, output, error, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<string> TempFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meep-cli-{Guid.NewGuid():N}.meep");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    [Test]
    public async Task Run_parses_builds_and_hosts_a_pipeline_file()
    {
        var path = await TempFile("""
            Interval 10ms count=2
                WriteLine "tick {msg.Count}"
            """);
        try
        {
            var (code, output, error) = await Meep("run", path);

            await Assert.That(error).IsEqualTo("");
            await Assert.That(output.ReplaceLineEndings("\n")).IsEqualTo("tick 1\ntick 2\n");
            await Assert.That(code).IsEqualTo(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_reports_mistakes_in_the_file_and_fails()
    {
        var path = await TempFile("Interval 1s\n    Wher msg.Count > 1\n");
        try
        {
            var (code, _, error) = await Meep("run", path);

            await Assert.That(code).IsEqualTo(1);
            await Assert.That(error).Contains($"{path}(2,5): error: There's no module called Wher. Did you mean 'Where'?");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_reports_a_missing_file()
    {
        var (code, _, error) = await Meep("run", "/no/such/file.meep");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(error).Contains("error: Could not find");
    }

    [Test]
    public async Task Run_logs_errors_the_pipeline_left_unhandled_and_carries_on()
    {
        var path = await TempFile("""
            Interval 10ms count=2
                Fetch "/no/such/file.json"
                    Extract "$.x"
            """);
        try
        {
            var (code, _, error) = await Meep("run", path);

            // Q84: only a file that doesn't load fails the run.
            await Assert.That(code).IsEqualTo(0);
            var logged = error.Split('\n').Count(line => line.Contains("error from Fetch #2: Couldn't read /no/such/file.json", StringComparison.Ordinal));
            await Assert.That(logged).IsEqualTo(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_exits_with_3_when_the_error_policy_stops_the_pipeline()
    {
        var path = await TempFile("""
            defaults
                on-error = stop-pipeline at 50%
            Interval 1ms
                Fetch '/no/such/file.json'
            """);
        try
        {
            var (code, _, error) = await Meep("run", path);

            await Assert.That(code).IsEqualTo(3);
            await Assert.That(error).Contains("Stopping the pipeline, because ");
            await Assert.That(error).Contains(" messages from its sources ended in errors nothing handled, which reaches its limit of 50%.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Version_and_usage()
    {
        var (versionCode, version, _) = await Meep("--version");
        var (noArgumentsCode, _, usage) = await Meep();

        await Assert.That(versionCode).IsEqualTo(0);
        await Assert.That(version).StartsWith("meep ");
        await Assert.That(noArgumentsCode).IsEqualTo(2);
        await Assert.That(usage).Contains("meep run [--dev] [--watch] [--otlp <url>] [--sample <share>] [--detach] <file.meep>");
    }
}
