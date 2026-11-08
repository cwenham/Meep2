using System.Text.Json;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

public class ModuleTests
{
    private static TestServer Server() => new(async context =>
    {
        switch (context.Request.Url!.AbsolutePath)
        {
            case "/departures":
                await TestServer.Reply(context, 200, """{ "departures": [ { "route": "7", "minutesAway": 4 }, { "route": "49", "minutesAway": 35 } ] }""");
                break;
            case "/slow":
                await Task.Delay(TimeSpan.FromSeconds(3));
                await TestServer.Reply(context, 200, "{}");
                break;
            default:
                await TestServer.Reply(context, 404, "no such page", "text/plain");
                break;
        }
    });

    [Test]
    public async Task Fetch_Extract_and_Where_read_an_api()
    {
        await using var server = Server();
        var run = await TestRun.Start($$"""
            const Api = "{{server.Url}}"
            Interval 10ms count=1
                Fetch "{Api}/departures"
                    Extract "$.departures[*]"
                        Where msg.minutesAway < 30
                            WriteLine "{msg.route} in {msg.minutesAway} min"
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(run.Output.ToString().Trim()).IsEqualTo("7 in 4 min");
    }

    [Test]
    public async Task Fetch_emits_the_response_with_its_status_and_body()
    {
        await using var server = Server();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Fetch "{{server.Url}}/departures"
                    Sink
            """);

        var response = (HttpResponse)run.At("Sink").Single();
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.ContentType).IsEqualTo("application/json");
        await Assert.That(response.Text).Contains("\"route\": \"7\"");
        await Assert.That(response.Body.Length).IsEqualTo(response.Text.Length);
    }

    [Test]
    public async Task Fetch_turns_an_error_status_into_an_HttpErrorMessage()
    {
        await using var server = Server();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Fetch "{{server.Url}}/missing" as Api

            Tap Api @errors
                Sink
            """);

        var error = (HttpErrorMessage)run.At("Sink").Single();
        await Assert.That(error.StatusCode).IsEqualTo(404);
        await Assert.That(error.Text).IsEqualTo("no such page");
        await Assert.That(error.Error).IsEqualTo($"{server.Url}/missing answered 404 Not Found");
    }

    [Test]
    public async Task Fetch_reports_a_timeout_as_an_error()
    {
        await using var server = Server();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Fetch "{{server.Url}}/slow" timeout=200ms
                    Sink
            """);

        var error = (ErrorMessage)run.At("Sink").Single();
        await Assert.That(error.Error).IsEqualTo($"{server.Url}/slow didn't answer within 200ms");
    }

    [Test]
    public async Task Fetch_reports_an_address_it_cannot_use()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Fetch "ftp://example.com/file"
                    Sink
            """);

        await Assert.That(((ErrorMessage)run.At("Sink").Single()).Error).IsEqualTo("Fetch needs an http or https address, not ftp://example.com/file");
    }

    [Test]
    public async Task Read_emits_a_files_text()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meep-read-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "hello from a file");
        try
        {
            var run = await TestRun.Start($$"""
                Read '{{path}}'
                    Sink
                """);

            var file = (FileText)run.At("Sink").Single();
            await Assert.That(file.Text).IsEqualTo("hello from a file");
            await Assert.That(file.Path).IsEqualTo(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Save_appends_by_default_and_can_overwrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"meep-save-{Guid.NewGuid():N}");
        try
        {
            await TestRun.Start($$"""
                const Folder = '{{folder}}'
                Interval 10ms count=3
                    Where msg.Count > 0
                        WriteLine "line {msg.Count}"
                    Save "{Folder}/all.txt"
                    Save "{Folder}/last.txt" mode=overwrite
                """);

            await Assert.That((await File.ReadAllTextAsync(Path.Combine(folder, "all.txt"))).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(3);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(folder, "last.txt"))).Contains("Count = 3");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task WriteLine_without_a_template_writes_each_message_as_text()
    {
        await using var server = Server();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Fetch "{{server.Url}}/departures"
                    Extract "$.departures[0].route"
                        WriteLine
            """);

        await Assert.That(run.Output.ToString().Trim()).IsEqualTo("7");
    }

    [Test]
    [Arguments("$.a", "1")]
    [Arguments("$['a']", "1")]
    [Arguments("$.list[*]", "10,20,30")]
    [Arguments("$.list[1]", "20")]
    [Arguments("$.list[-1]", "30")]
    [Arguments("$.list[0:2]", "10,20")]
    [Arguments("$.list[::-1]", "30,20,10")]
    [Arguments("$.list[0,2]", "10,30")]
    [Arguments("$..name", "\"x\",\"y\"")]
    [Arguments("$.nested.*", "\"x\",{\"name\":\"y\"}")]
    [Arguments("$.missing", "")]
    public async Task JsonPath_supports_the_common_selectors(string query, string expected)
    {
        using var document = JsonDocument.Parse("""{"a":1,"list":[10,20,30],"nested":{"name":"x","deeper":{"name":"y"}}}""");

        var matches = JsonPath.Parse(query).Select(document.RootElement);

        await Assert.That(Wait.Join(matches.Select(m => m.GetRawText()))).IsEqualTo(expected);
    }

    [Test]
    public async Task JsonPath_explains_what_it_doesnt_support()
    {
        await Assert.That(() => JsonPath.Parse("$.list[?@.price < 10]"))
            .Throws<FormatException>()
            .WithMessage("Filters ([?…]) aren't supported; use Where on the results instead in the JSONPath query $.list[?@.price < 10] (at character 8).");
    }
}
