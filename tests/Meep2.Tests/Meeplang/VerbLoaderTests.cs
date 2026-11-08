using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Verbs, inflections, expiry and error policies in Meeplang (step 4).</summary>
public class VerbLoaderTests
{
    private static async Task<string> TempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meep-fetch-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Test]
    public async Task Fetch_reads_a_file_when_its_address_is_a_path()
    {
        var path = await TempJson("""{ "x": 7 }""");
        try
        {
            var run = await TestRun.Start($"""
                Interval 10ms count=1
                    Fetch '{path}'
                        Extract "$.x"
                            Sink
                """);

            await Assert.That(run.Diagnostics).IsEqualTo("");
            await Assert.That(((JsonMessage)run.At("Sink").Single()).Text).IsEqualTo("7");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Fetch_chooses_an_inflection_for_each_message_when_its_address_is_worked_out_per_message()
    {
        var path = await TempJson("""{ "x": 7 }""");
        try
        {
            var run = await TestRun.Start($$"""
                Interval 10ms count=1 payload='{{path}}'
                    Fetch "{msg.Payload}"
                        Sink
                """);

            var file = (FileText)run.At("Sink").Single();
            await Assert.That(file.Path).IsEqualTo(Path.GetFullPath(path));
            await Assert.That(file.EmittedBy!.Inflection).IsEqualTo("File");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task An_inflection_can_be_named_after_its_verb_or_on_its_own_and_fam_finds_it_by_name()
    {
        var path = await TempJson("""{ "x": 7 }""");
        try
        {
            var run = await TestRun.Start($$"""
                Interval 10ms count=1
                    Fetch.File '{{path}}'
                        Sink as Named
                    File '{{path}}'
                        Extract "$.x"
                            WriteLine "{msg} from {fam.File.Path}"
                """);

            await Assert.That(run.Diagnostics).IsEqualTo("");
            await Assert.That(run.At("Named").Single()).IsTypeOf<FileText>();
            await Assert.That(run.Output.ToString().Trim()).IsEqualTo($"7 from {Path.GetFullPath(path)}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Naming_an_inflection_settles_a_tie()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Echo.Quiet
                    Sink
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(run.At("Sink").Single()).IsTypeOf<Tick>();
    }

    [Test]
    public async Task Bypass_lets_a_type_no_inflection_takes_go_past()
    {
        var run = await TestRun.Start("""
            + Read 'x.json'
            + Interval 1s
            Extract "$.x" @bypass
                Sink
            """, run: false);

        await Assert.That(run.Result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Defaults_set_the_pipelines_expiry_and_error_policy()
    {
        var run = await TestRun.Start("""
            defaults
                expires = 50s
                on-error = ignore, stop-branch never, stop-pipeline at 20%, restart after 1s up to 5 times // a comment
            Interval 10ms count=1
                Sink
            """, run: false);

        var options = run.Result.Pipeline!.Options;
        await Assert.That(options.Expires).IsEqualTo(TimeSpan.FromSeconds(50));
        await Assert.That(options.ErrorPolicy.LogUnhandled).IsFalse();
        await Assert.That(options.ErrorPolicy.StopBranch).IsNull();
        await Assert.That(options.ErrorPolicy.StopPipelineAtPercent).IsEqualTo(20);
        await Assert.That(options.ErrorPolicy.RestartSources).IsEqualTo(new SourceRestart(TimeSpan.FromSeconds(1), 5));
    }

    [Test]
    public async Task A_policy_in_quotes_works_too_and_parts_left_out_keep_their_defaults()
    {
        var run = await TestRun.Start("""
            defaults
                on-error = "stop-pipeline never"
            Interval 10ms count=1
                Sink
            """, run: false);

        var policy = run.Result.Pipeline!.Options.ErrorPolicy;
        await Assert.That(policy.StopPipelineAtPercent).IsNull();
        await Assert.That(policy.LogUnhandled).IsTrue();
        await Assert.That(policy.StopBranch).IsEqualTo(ErrorPolicy.Default.StopBranch);
    }

    [Test]
    public async Task A_branch_inherits_the_error_policy_of_the_module_above_it()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Boom as Quiet @on-error="ignore"
                    Boom as AlsoQuiet
                Boom as Loud
            """);

        await Assert.That(run.Unhandled.Single().EmittedBy!.Name).IsEqualTo("Loud");
    }

    [Test]
    public async Task Expiry_can_be_set_by_default_and_on_a_source()
    {
        var run = await TestRun.Start("""
            defaults
                expires = 1h
            Interval 10ms count=1
                Sink as Inherited
            Interval 10ms count=1 @expires=none
                Sink as Cleared
            """);

        var remaining = run.At("Inherited").Single().Expires!.Value.Remaining;
        await Assert.That(remaining > TimeSpan.FromMinutes(59)).IsTrue();
        await Assert.That(run.At("Cleared").Single().Expires).IsNull();
    }

    [Test]
    public async Task A_pipeline_stops_when_its_error_policy_says_so()
    {
        var run = await TestRun.Start("""
            defaults
                on-error = stop-pipeline at 50%
            Interval 1ms
                Fetch '/no/such/file.json'
            """);

        await Assert.That(run.Notices.Single()).Contains("which reaches its limit of 50%");
        await Assert.That(run.Result.Pipeline!.StoppedBecause).IsNotNull();
    }
}
