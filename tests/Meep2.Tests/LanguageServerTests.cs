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
    private static Task<List<JsonNode>> Converse(params JsonObject[] messages) => ConverseWith(null, messages);

    // The same, with a home folder for plugins and the trust store.
    private static async Task<List<JsonNode>> ConverseWith(Meep2.Plugins.PluginOptions? plugins, params JsonObject[] messages)
    {
        var input = new MemoryStream();
        foreach (var message in messages)
        {
            message["jsonrpc"] = "2.0";
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            input.Write(body);
        }
        return await ConverseRaw(input.ToArray(), plugins);
    }

    // Runs the server on bytes exactly as given, framing and all, and reads back what it sent.
    private static async Task<List<JsonNode>> ConverseRaw(byte[] bytes, Meep2.Plugins.PluginOptions? plugins = null)
    {
        var input = new MemoryStream(bytes);
        var output = new MemoryStream();
        await Wait.For(new LanguageServer(input, output, plugins).RunAsync(CancellationToken.None));

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

    private static byte[] Framed(string body) =>
        [.. Encoding.ASCII.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n"), .. Encoding.UTF8.GetBytes(body)];

    [Test]
    public async Task Malformed_messages_are_answered_with_errors_and_the_server_carries_on()
    {
        var replies = await ConverseRaw([
            .. Framed("""{"jsonrpc":"2.0","id":1,"method":"textDocument/hover","params":{}}"""),
            .. Framed("not json"),
            .. Framed("""{"jsonrpc":"2.0","id":2,"method":"textDocument/didOpen"}"""),
            .. Framed("""{"jsonrpc":"2.0","method":"textDocument/didChange"}"""),
            .. Framed("""[1, 2]"""),
            .. Framed("""{"jsonrpc":"2.0","id":3,"method":"shutdown"}"""),
            .. Encoding.ASCII.GetBytes($"Content-Length: {JsonRpcChannel.MaxMessage + 1}\r\n\r\n"),
        ]);

        await Assert.That(replies.Count).IsEqualTo(6);
        await Assert.That(replies[0]["id"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(replies[0]["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32602);
        await Assert.That(replies[0]["error"]!["message"]!.GetValue<string>()).StartsWith("meep lsp couldn't handle textDocument/hover: ");
        await Assert.That(replies[1]["id"]).IsNull();
        await Assert.That(replies[1]["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32700);
        await Assert.That(replies[2]["id"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(replies[2]["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32602);
        await Assert.That(replies[3]["id"]).IsNull(); // [1, 2] isn't a JSON-RPC message; the didChange notification had nobody to answer
        await Assert.That(replies[3]["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32600);
        await Assert.That(replies[4]["id"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(replies[4]["result"]).IsNull();
        await Assert.That(replies[5]["error"]!["message"]!.GetValue<string>()).IsEqualTo($"meep lsp skipped a message of {JsonRpcChannel.MaxMessage + 1} bytes: messages can be at most {JsonRpcChannel.MaxMessage}.");
    }

    [Test]
    public async Task A_message_without_a_length_it_can_follow_stops_the_server_saying_why()
    {
        var replies = await ConverseRaw([
            .. Encoding.ASCII.GetBytes("Content-Length: lots\r\n\r\n{}"),
            .. Framed("""{"jsonrpc":"2.0","id":1,"method":"shutdown"}"""),
        ]);

        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]["error"]!["message"]!.GetValue<string>()).IsEqualTo("meep lsp read a message without a Content-Length it could follow, so it has stopped.");
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
    public async Task It_completes_and_describes_permission_gates()
    {
        const string Uri = "file:///tmp/gated.meep";
        var replies = await Converse(
            Open(Uri, "Interval 1s\n    WriteLine \"x\" @permission=ReadNotes @permission=\"WriteNotes:~/notes/**\" @"),
            At(1, "textDocument/completion", Uri, 1, 77),
            At(2, "textDocument/hover", Uri, 1, 22));

        var labels = replies.Single(r => r["id"]?.GetValue<int>() == 1)["result"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>());
        await Assert.That(labels).Contains("@permission");
        var hover = replies.Single(r => r["id"]?.GetValue<int>() == 2)["result"]!["contents"]!["value"]!.GetValue<string>();
        await Assert.That(hover).StartsWith("**@permission** (modifier): a gate.");
        await Assert.That(hover).EndsWith("Needs here: `ReadNotes`, `WriteNotes:~/notes/**`");
    }

    [Test]
    public async Task It_completes_and_describes_proof_and_says_Authorize_is_Authorise()
    {
        const string Uri = "file:///tmp/proven.meep";
        var replies = await Converse(
            Open(Uri, "Interval 1s\n    Authorize permission=\"ReadNotes\" @proof=signed @"),
            At(1, "textDocument/completion", Uri, 1, 55),
            At(2, "textDocument/hover", Uri, 1, 40),
            At(3, "textDocument/hover", Uri, 1, 6));

        var labels = replies.Single(r => r["id"]?.GetValue<int>() == 1)["result"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>());
        await Assert.That(labels).Contains("@proof");
        var proof = replies.Single(r => r["id"]?.GetValue<int>() == 2)["result"]!["contents"]!["value"]!.GetValue<string>();
        await Assert.That(proof).StartsWith("**@proof** (modifier):");
        var verb = replies.Single(r => r["id"]?.GetValue<int>() == 3)["result"]!["contents"]!["value"]!.GetValue<string>();
        await Assert.That(verb).StartsWith("**Authorize** (step), another name for **Authorise**");
        await Assert.That(verb).Contains("Always needs `AuthorisePermission`");
    }

    [Test]
    public async Task It_warns_about_a_permission_the_trust_store_doesnt_define()
    {
        using var home = new TemporaryFolder();
        var folder = Meep2.Security.TrustFolder.In(home.Path);
        folder.Create();
        File.WriteAllText(folder.Permissions, "#permission ReadNotes\n");
        const string Uri = "file:///tmp/gated.meep";

        var replies = await ConverseWith(new Meep2.Plugins.PluginOptions { Home = home.Path },
            Open(Uri, "#permission Fresh\nInterval 1s\n    WriteLine \"x\" @permission=ReadNotes @permission=Raednotes\n"));

        var diagnostics = replies.Single(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray();
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d!["severity"]!.GetValue<int>() == 2)).IsTrue();
        await Assert.That(diagnostics[0]!["message"]!.GetValue<string>()).StartsWith("Fresh isn't defined in this machine's trust store");
        var misspelt = diagnostics[1]!;
        await Assert.That(misspelt["message"]!.GetValue<string>()).StartsWith("Raednotes isn't defined in this machine's trust store (permissions.meep), so any trusted authority confers it");
        await Assert.That(misspelt["range"]!["start"]!["line"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(misspelt["range"]!["start"]!["character"]!.GetValue<int>()).IsEqualTo(40);
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
        await Assert.That(hover).Contains("**Interval** (source)");
        await Assert.That(hover).Contains("`Interval.Interval`: Emits a Tick straight away");
        await Assert.That(hover).Contains("- `period` duration, after the verb, required. How long between ticks.");
        await Assert.That(hover).Contains("- `count-start` number, default `1`. The number of the first tick");
        await Assert.That(hover).Contains("- `payload` text. Text to put in each tick, for telling intervals apart.");
    }

    [Test]
    public async Task It_completes_book_names_in_a_value_that_takes_a_book()
    {
        const string Uri = "file:///tmp/books.meep";
        var replies = await Converse(
            Open(Uri, "Interval 1s\n    Enumerate \"Common P\n    WriteLine \"Co"),
            At(1, "textDocument/completion", Uri, 1, 24),
            At(2, "textDocument/completion", Uri, 2, 17));

        var books = replies.Single(r => r["id"]?.GetValue<int>() == 1)["result"]!["items"]!.AsArray();
        var passwords = books.Single(i => i!["label"]!.GetValue<string>() == "Common Passwords")!;
        var edit = passwords["textEdit"]!;
        string[] notBooks = [.. replies.Single(r => r["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>())];
        await Assert.That(books.Select(i => i!["label"]!.GetValue<string>())).Contains("ISO 3166 Countries");
        await Assert.That(edit["newText"]!.GetValue<string>()).IsEqualTo("Common Passwords");
        await Assert.That(edit["range"]!["start"]!["character"]!.GetValue<int>()).IsEqualTo(15);
        await Assert.That(edit["range"]!["end"]!["character"]!.GetValue<int>()).IsEqualTo(24);
        await Assert.That(notBooks).DoesNotContain("Common Passwords");
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
