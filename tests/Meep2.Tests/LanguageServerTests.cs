using System.Text;
using System.Text.Json.Nodes;
using Meep2.Cli;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>meep lsp, the language server for editors (step 9).</summary>
public class LanguageServerTests
{
    // Frames messages as an editor sends them, runs the server until they run out, and reads back what it sent.
    private static async Task<List<JsonNode>> Converse(params JsonObject[] messages)
    {
        var input = new MemoryStream();
        foreach (var message in messages)
        {
            message["jsonrpc"] = "2.0";
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            input.Write(body);
        }
        input.Position = 0;
        var output = new MemoryStream();
        await Wait.For(new LanguageServer(input, output).RunAsync(CancellationToken.None));

        var replies = new List<JsonNode>();
        var text = Encoding.UTF8.GetString(output.ToArray());
        var at = 0;
        while (at < text.Length)
        {
            var header = text.IndexOf("\r\n\r\n", at, StringComparison.Ordinal);
            var length = int.Parse(text[(at + "Content-Length: ".Length)..header], System.Globalization.CultureInfo.InvariantCulture);
            replies.Add(JsonNode.Parse(text.Substring(header + 4, length))!);
            at = header + 4 + length;
        }
        return replies;
    }

    private static JsonObject Open(string uri, string text) => new()
    {
        ["method"] = "textDocument/didOpen",
        ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "meeplang", ["version"] = 1, ["text"] = text } },
    };

    private static JsonObject At(int id, string method, string uri, int line, int character) => new()
    {
        ["id"] = id,
        ["method"] = method,
        ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } },
    };

    [Test]
    public async Task It_reports_the_mistakes_meep_run_would_find_where_they_are()
    {
        var replies = await Converse(
            new JsonObject { ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() },
            Open("file:///tmp/ticker.meep", "Interval 1s\n    WriteLin \"{msg.Count}\"\n"),
            new JsonObject { ["id"] = 2, ["method"] = "shutdown" },
            new JsonObject { ["method"] = "exit" });

        var capabilities = replies.Single(r => r["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!;
        var published = replies.Single(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!;
        var diagnostic = published["diagnostics"]![0]!;
        await Assert.That(capabilities["hoverProvider"]!.GetValue<bool>()).IsTrue();
        await Assert.That(published["uri"]!.GetValue<string>()).IsEqualTo("file:///tmp/ticker.meep");
        await Assert.That(diagnostic["message"]!.GetValue<string>()).IsEqualTo("There's no module called WriteLin. Did you mean 'WriteLine'?");
        await Assert.That(diagnostic["range"]!["start"]!["line"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(diagnostic["range"]!["start"]!["character"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(diagnostic["severity"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task It_completes_verbs_modifiers_and_message_types_and_describes_verbs()
    {
        const string Uri = "file:///tmp/edit.meep";
        var replies = await Converse(
            Open(Uri, "Interval 1s\n    Where msg is \n    WriteLine \"x\" @\nInter"),
            At(1, "textDocument/completion", Uri, 3, 5),
            At(2, "textDocument/completion", Uri, 2, 19),
            At(3, "textDocument/completion", Uri, 1, 17),
            At(4, "textDocument/hover", Uri, 0, 3));

        string[] Labels(int id) => [.. replies.Single(r => r["id"]?.GetValue<int>() == id)["result"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>())];
        var hover = replies.Single(r => r["id"]?.GetValue<int>() == 4)["result"]!["contents"]!["value"]!.GetValue<string>();
        await Assert.That(Labels(1)).Contains("Interval");
        await Assert.That(Labels(1)).Contains("template");
        await Assert.That(Labels(2)).Contains("@bypass");
        await Assert.That(Labels(3)).Contains("Tick");
        await Assert.That(hover).Contains("**Interval**");
        await Assert.That(hover).Contains("period, count, payload");
    }

    [Test]
    public async Task It_checks_plugins_verbs_from_the_cache_without_downloading_anything()
    {
        using var lab = new PluginLab();
        lab.Options = lab.Options with { NuGetSources = [lab.SignedFeed()] };
        const string Shout = """
            #plugin nuget:Meep2.TestPlugin 1.0.0
            Interval 10ms count=1
                Shout "tick"
            """;
        var installed = await lab.RunAsync(Shout, new PluginCatalog()); // meep run installs it into the cache
        var server = new LanguageServer(Stream.Null, Stream.Null, lab.Options);
        var path = Path.Combine(lab.Path, "shout.meep");

        var good = await server.CheckAsync("file:///shout.meep", path, Shout, CancellationToken.None);
        var misspelt = await server.CheckAsync("file:///shout.meep", path, Shout.Replace("Shout \"", "Shuot \""), CancellationToken.None);
        var missing = await server.CheckAsync("file:///other.meep", path, "#plugin nuget:Meep2.Absent 1.0.0\nInterval 1s\n    WriteLine \"x\"\n", CancellationToken.None);

        await Assert.That(installed.AllErrors).IsEmpty();
        await Assert.That(good.Select(d => d.Message)).IsEmpty();
        await Assert.That(misspelt.Select(d => d.Message)).Contains("There's no module called Shuot. Did you mean 'Shout'?");
        await Assert.That(missing.Select(d => d.Message)).Contains("nuget:Meep2.Absent 1.0.0 isn't in Meep's cache, and plugins aren't downloaded while editing. Run the pipeline once to install it.");
        await Assert.That(missing[0].Span.Line).IsEqualTo(1); // on its #plugin line
    }
}
