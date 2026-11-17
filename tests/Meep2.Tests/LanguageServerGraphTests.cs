using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>
/// What meep lsp adds for Synesthesia: an open file's graph as the daemon gives it, with keys that last while it's
/// typed and agree with the running pipeline's, semantic tokens from Meep's parser, and the verb catalogue.
/// </summary>
public class LanguageServerGraphTests
{
    private static readonly string s_examples = Path.Combine(RepositoryRoot(), "examples");

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Meep2.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Couldn't find the repository from " + AppContext.BaseDirectory);
    }

    private static string UriOf(string path) => new Uri(path).ToString();

    // The graph the daemon gives for a file, as GraphTests builds it.
    private static async Task<PipelineGraph> DaemonGraphOf(string path)
    {
        var syntax = MeeplangParser.Parse(new SourceFile(path, await File.ReadAllTextAsync(path)));
        var (live, diagnostics) = LivePipeline.Load(syntax, new ModuleRegistry().AddAssembly(CoreModules.Assembly));
        if (live is null)
            throw new InvalidOperationException(string.Join("\n", diagnostics));
        await using var pipeline = live.Pipeline;
        return Graphs.Describe(Path.GetFileNameWithoutExtension(path), path, live, version: 1);
    }

    private static PipelineGraph GraphIn(JsonNode answer) => answer["graph"]!.Deserialize(DaemonJson.Default.PipelineGraph)!;

    private static string Json(PipelineGraph graph) => JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph);

    private static string Keys(PipelineGraph graph) => string.Join(" ", graph.Modules.Select(m => $"{m.Key}={m.Module}"));

    private static JsonObject GraphRequest(string uri, bool? subscribe = null)
    {
        var request = new JsonObject { ["textDocument"] = LspSession.Document(uri) };
        if (subscribe is { } value)
            request["subscribe"] = value;
        return request;
    }

    [Test]
    [Arguments("watchdog.meep")]
    [Arguments("merge.meep")]
    [Arguments("classify.meep")]
    public async Task An_open_files_graph_is_the_graph_the_daemon_gives(string example)
    {
        var path = Path.Combine(s_examples, example);
        await using var lsp = new LspSession();
        await lsp.OpenAsync(UriOf(path), await File.ReadAllTextAsync(path), version: 7);

        var answer = (await lsp.RequestAsync("meep/graph", GraphRequest(UriOf(path))))!;

        await Assert.That(answer["version"]!.GetValue<int>()).IsEqualTo(7);
        await Assert.That(answer["current"]!.GetValue<bool>()).IsTrue();
        await Assert.That(answer["builtFrom"]!.GetValue<int>()).IsEqualTo(7);
        await Assert.That(answer["problems"]!.AsArray()).IsEmpty();
        await Assert.That(Json(GraphIn(answer))).IsEqualTo(Json(await DaemonGraphOf(path)));
    }

    [Test]
    public async Task A_template_in_an_included_file_follows_the_included_file_as_its_typed()
    {
        using var folder = new TemporaryFolder();
        const string Shared = """
            template Shout(Text, Times = 1)
                Where msg.Count <= Times
                    WriteLine Text
            """;
        var shared = folder.Write("shared.meep", Shared);
        var loud = folder.Write("loud.meep", """
            #include "shared.meep"

            template Loudly(Words)
                Shout Words

            Interval 10ms count=3
                Loudly "{msg.Count}!"
            """);
        await using var lsp = new LspSession();
        await lsp.OpenAsync(UriOf(loud), await File.ReadAllTextAsync(loud));
        await lsp.OpenAsync(UriOf(shared), Shared);
        var first = (await lsp.RequestAsync("meep/graph", GraphRequest(UriOf(loud), subscribe: true)))!;

        // The template gets a second WriteLine in the editor, not yet saved.
        await lsp.ChangeAsync(UriOf(shared), Shared + "\n        WriteLine \"again\"", version: 2);
        await lsp.SyncAsync();
        var changed = lsp.Notifications("meep/graphChanged").Single();

        var before = GraphIn(first);
        var after = GraphIn(changed);
        await Assert.That(Json(before)).IsEqualTo(Json(await DaemonGraphOf(loud)));
        await Assert.That(Keys(before)).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3");
        await Assert.That(Keys(after)).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3 m4=WriteLine #4");
        await Assert.That(after.Version).IsEqualTo(2);
        await Assert.That(after.Modules[3].Span).IsEqualTo(new GraphSpan(shared, 4, 9, 4, 26));
        await Assert.That(after.Modules[3].Include!.Path).IsEqualTo("shared.meep");
        await Assert.That(after.Modules[3].Templates!.Select(t => t.Template)).IsEquivalentTo(["Shout", "Loudly"]);
        await Assert.That(changed["uri"]!.GetValue<string>()).IsEqualTo(UriOf(loud));
    }

    [Test]
    public async Task Keys_last_while_typing_and_a_line_that_breaks_keeps_the_last_graph()
    {
        const string Uri = "file:///tmp/site.meep";
        const string Start = """
            Interval 30s
                Fetch "https://example.com" as Check
                    timeout = 10s
                    WriteLine "up"

            Tap Check @errors
                WriteLine "down"
            """;
        const string Filtered = """
            Interval 30s
                Fetch "https://example.com" as Check
                    timeout = 10s
                    Where msg.StatusCode == 200
                        WriteLine "up"

            Tap Check @errors
                WriteLine "down"
            """;
        var broken = Filtered.Replace("            WriteLine \"up\"", "            WriteLine \"{msg.\n            WriteLine \"up\"", StringComparison.Ordinal);
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Start);
        var first = (await lsp.RequestAsync("meep/graph", GraphRequest(Uri, subscribe: true)))!;

        await lsp.ChangeAsync(Uri, Filtered, version: 2);
        await lsp.ChangeAsync(Uri, broken, version: 3); // a line part way through that doesn't parse
        await lsp.ChangeAsync(Uri, Filtered, version: 4); // mended
        await lsp.SyncAsync();
        var notices = lsp.Notifications("meep/graphChanged");

        await Assert.That(Keys(GraphIn(first))).IsEqualTo("m1=Interval #1 m2=Check m3=WriteLine #3 m4=Tap #4 m5=WriteLine #5");
        await Assert.That(notices.Count).IsEqualTo(3);

        // A Where inserted: the others keep their keys, though WriteLine's number changes.
        var filtered = GraphIn(notices[0]);
        await Assert.That(Keys(filtered)).IsEqualTo("m1=Interval #1 m2=Check m6=Where #3 m3=WriteLine #4 m4=Tap #5 m5=WriteLine #6");
        await Assert.That(filtered.Version).IsEqualTo(2);
        await Assert.That(notices[0]["version"]!.GetValue<int>()).IsEqualTo(2);

        // Broken: the last graph that loaded, its spans after the new line moved down by one, and the problem marked.
        var stale = notices[1];
        var staleGraph = GraphIn(stale);
        await Assert.That(stale["current"]!.GetValue<bool>()).IsFalse();
        await Assert.That(stale["version"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(stale["builtFrom"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(Keys(staleGraph)).IsEqualTo(Keys(filtered));
        await Assert.That(staleGraph.Version).IsEqualTo(2);
        var problem = stale["problems"]!.AsArray().Single()!;
        await Assert.That(problem["range"]!["start"]!["line"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(problem["message"]!.GetValue<string>()).Contains("isn't closed");
        await Assert.That(stale["changed"]!["start"]!["line"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(stale["changed"]!["end"]!["line"]!.GetValue<int>()).IsEqualTo(5);
        await Assert.That(staleGraph.Modules[2].Span.StartLine).IsEqualTo(4); // Where, above the new line: where it was
        await Assert.That(staleGraph.Modules[3].Span.StartLine).IsEqualTo(6); // WriteLine "up", below it: moved down
        await Assert.That(staleGraph.Modules[4].Span.StartLine).IsEqualTo(8); // Tap
        await Assert.That(staleGraph.Modules[4].Modifiers.Single().Span.StartLine).IsEqualTo(8);

        // Mended: the same graph as before it broke, so the same version.
        var mended = notices[2];
        await Assert.That(mended["current"]!.GetValue<bool>()).IsTrue();
        await Assert.That(Json(GraphIn(mended))).IsEqualTo(Json(filtered));
        await Assert.That(mended["problems"]!.AsArray()).IsEmpty();
        await Assert.That(mended["changed"]).IsNull();
    }

    [Test]
    public async Task A_module_typed_above_an_unchanged_one_with_the_same_verb_leaves_it_its_key()
    {
        const string Uri = "file:///tmp/above.meep";
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, "Interval 1s\n    WriteLine \"b\"\n");
        var first = GraphIn((await lsp.RequestAsync("meep/graph", GraphRequest(Uri)))!);

        await lsp.ChangeAsync(Uri, "Interval 1s\n    WriteLine \"c\"\n    WriteLine \"b\"\n", version: 2);
        var after = GraphIn((await lsp.RequestAsync("meep/graph", GraphRequest(Uri)))!);

        await Assert.That(Keys(first)).IsEqualTo("m1=Interval #1 m2=WriteLine #2");
        await Assert.That(after.Modules.Single(m => m.Parameters[0].Text == "\"b\"").Key).IsEqualTo("m2");
        await Assert.That(after.Modules.Single(m => m.Parameters[0].Text == "\"c\"").Key).IsEqualTo("m3");
    }

    [Test]
    public async Task A_file_that_never_loaded_has_no_graph_yet_but_says_why()
    {
        const string Uri = "file:///tmp/broken.meep";
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, "Interval 1s\n    Where (msg.Count\n    WriteLine \"x\"\n");

        var answer = (await lsp.RequestAsync("meep/graph", GraphRequest(Uri)))!;

        await Assert.That(answer["graph"]).IsNull();
        await Assert.That(answer["current"]!.GetValue<bool>()).IsFalse();
        await Assert.That(answer["problems"]!.AsArray().Single()!["range"]!["start"]!["line"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(await lsp.RequestAsync("meep/graph", GraphRequest("file:///tmp/not-open.meep"))).IsNull();
    }

    [Test]
    public async Task Keys_agree_with_the_daemons_through_typing_saving_and_reloading()
    {
        await using var lab = await DaemonLab.StartAsync();
        const string Text = """
            Interval 20ms
                Where msg.Count > 0
                    WriteLine "a"
            """;
        var file = lab.Write("chain.meep", Text);
        await lab.Client.StartAsync(file);

        // The daemon's history gives Where a key that numbering the file afresh wouldn't: it was removed, then put back.
        await File.WriteAllTextAsync(file, "Interval 20ms\n    WriteLine \"a\"\n");
        await lab.Client.ReloadAsync("chain");
        await File.WriteAllTextAsync(file, Text);
        await lab.Client.ReloadAsync("chain");
        var running = await lab.Client.GraphAsync("chain");
        var source = await lab.Client.SourceAsync("chain");

        await using var lsp = new LspSession();
        var uri = UriOf(file);
        await lsp.OpenAsync(uri, Text);
        var unmatched = GraphIn((await lsp.RequestAsync("meep/graph", GraphRequest(uri)))!);
        var based = (await lsp.RequestAsync("meep/setBase", new JsonObject
        {
            ["textDocument"] = LspSession.Document(uri),
            ["pipeline"] = "chain",
            ["graph"] = JsonSerializer.SerializeToNode(running, DaemonJson.Default.PipelineGraph),
            ["text"] = source.Text,
        }))!;

        // Typing: a WriteLine added at the end, then a Batch above the Where, before saving.
        var withB = Text + "\n    WriteLine \"b\"";
        var withBAndC = "Interval 20ms\n    Batch 2\n" + Text["Interval 20ms\n".Length..] + "\n    WriteLine \"b\"";
        await lsp.ChangeAsync(uri, withB, version: 2);
        await lsp.ChangeAsync(uri, withBAndC, version: 3);
        var typed = GraphIn((await lsp.RequestAsync("meep/graph", GraphRequest(uri)))!);

        // Saved, and the daemon reloads it; the client sets the base again from the daemon's new graph.
        await File.WriteAllTextAsync(file, withBAndC);
        await lab.Client.ReloadAsync("chain");
        var reloaded = await lab.Client.GraphAsync("chain");
        var rebased = (await lsp.RequestAsync("meep/setBase", new JsonObject
        {
            ["textDocument"] = LspSession.Document(uri),
            ["pipeline"] = "chain",
            ["graph"] = JsonSerializer.SerializeToNode(reloaded, DaemonJson.Default.PipelineGraph),
            ["text"] = (await lab.Client.SourceAsync("chain")).Text,
        }))!;
        var after = GraphIn(rebased);

        await Assert.That(Keys(running)).IsEqualTo("m1=Interval #1 m4=Where #2 m3=WriteLine #3");
        await Assert.That(Keys(unmatched)).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3");
        await Assert.That(Keys(GraphIn(based))).IsEqualTo(Keys(running));
        await Assert.That(based["renamed"]!["m2"]!.GetValue<string>()).IsEqualTo("m4");
        await Assert.That(based["base"]!["pipeline"]!.GetValue<string>()).IsEqualTo("chain");
        await Assert.That(GraphIn(based).Name).IsEqualTo("chain");

        // While typing, the modules the daemon runs have its keys; the new ones have keys of their own, in the order written.
        await Assert.That(Keys(typed)).IsEqualTo("m1=Interval #1 m6=Batch #2 m4=Where #3 m3=WriteLine #4 m5=WriteLine #5");
        // The daemon gives new modules keys in the order it numbers them, so the two new ones swap keys when the base is set again.
        await Assert.That(Keys(reloaded)).IsEqualTo("m1=Interval #1 m5=Batch #2 m4=Where #3 m3=WriteLine #4 m6=WriteLine #5");
        foreach (var module in reloaded.Modules.Where(m => m.Key is "m1" or "m3" or "m4"))
            await Assert.That(typed.Modules.Single(m => m.Number == module.Number).Key).IsEqualTo(module.Key);
        await Assert.That(Keys(after)).IsEqualTo(Keys(reloaded));
        await Assert.That(rebased["renamed"]!.AsObject().Select(r => $"{r.Key}>{r.Value}")).IsEquivalentTo(["m5>m6", "m6>m5"]);
        await Assert.That(after.Links.Select(l => l.Key)).IsEquivalentTo(reloaded.Links.Select(l => l.Key));
    }

    [Test]
    public async Task A_base_that_isnt_the_texts_graph_is_refused()
    {
        const string Uri = "file:///tmp/chain.meep";
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, "Interval 1s\n    WriteLine \"x\"\n");
        var graph = new PipelineGraph("chain", "/tmp/chain.meep", 1,
            [new GraphModule("m1", 1, "Interval #1", "Interval", "source", ["Interval"], [], [], new GraphSpan("/tmp/chain.meep", 1, 1, 1, 12))], []);

        var answer = await lsp.SendRequestAsync("meep/setBase", new JsonObject
        {
            ["textDocument"] = LspSession.Document(Uri),
            ["pipeline"] = "chain",
            ["graph"] = JsonSerializer.SerializeToNode(graph, DaemonJson.Default.PipelineGraph),
            ["text"] = "Interval 1s\n    WriteLine \"x\"\n",
        });

        await Assert.That(answer["error"]!["message"]!.GetValue<string>()).Contains("no module 2 that's a WriteLine");
    }

    // The semantic tokens as "text:type" or "text:type.modifier", read back from LSP's relative numbers.
    private static List<string> Decode(JsonNode result, string text, JsonNode legend)
    {
        var lines = text.Split('\n');
        var types = legend["tokenTypes"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
        var modifiers = legend["tokenModifiers"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
        var data = result["data"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        var tokens = new List<string>();
        int line = 0, character = 0;
        for (var i = 0; i < data.Count; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            var mods = string.Concat(modifiers.Where((_, bit) => (data[i + 4] & (1 << bit)) != 0).Select(m => "." + m));
            tokens.Add($"{lines[line].Substring(character, data[i + 2])}:{types[data[i + 3]]}{mods}");
        }
        return tokens;
    }

    [Test]
    public async Task Semantic_tokens_colour_meeplang_from_its_own_parser_even_part_way_broken()
    {
        const string Uri = "file:///tmp/colours.meep";
        const string Text = """
            // Colours
            #include "shared.meep"
            defaults
                buffer = 100
            const Site = "https://example.com"
            template Loudly(Words, Times = 2)
                WriteLine Words
            Interval 30s count=3 // ticks
                Fetch.HTTP Site as Check
                    timeout = 10s
                    Where msg is not ErrorMessage and msg.StatusCode == 200
                        WriteLine "{msg.Created.ToLocalTime():HH:mm} {Site} up" @buffer=5
                        Loudly "hi"
                    WriteLine "unfinished
                    WriteLine 'after the broken line'
            Tap Check @errors
                WriteLine "down"
            """;
        await using var lsp = new LspSession();
        var initialize = (await lsp.RequestAsync("initialize", new JsonObject()))!;
        var legend = initialize["capabilities"]!["semanticTokensProvider"]!["legend"]!;
        await lsp.OpenAsync(Uri, Text);

        var result = (await lsp.RequestAsync("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = LspSession.Document(Uri) }))!;
        var tokens = Decode(result, Text, legend);

        await Assert.That(initialize["capabilities"]!["semanticTokensProvider"]!["full"]!["delta"]!.GetValue<bool>()).IsTrue();
        string[] expected =
        [
            "// Colours:comment", "#include:keyword", "\"shared.meep\":string", "defaults:keyword", "buffer:property", "100:number",
            "const:keyword", "Site:variable.declaration.readonly", "template:keyword", "Loudly:macro.declaration",
            "Words:parameter.declaration", "Times:parameter.declaration", "2:number", "Words:parameter",
            "Interval:function", "30s:number", "count:parameter", "// ticks:comment",
            "Fetch:function", "HTTP:method", "Site:variable.readonly", "as:keyword", "Check:variable.declaration",
            "timeout:parameter", "10s:number",
            "is:keyword", "not:keyword", "ErrorMessage:type", "and:keyword", "msg:variable.readonly.defaultLibrary", "StatusCode:property", "==:operator",
            "\"{:string", "msg:variable.readonly.defaultLibrary", "Created:property", "ToLocalTime:method", "} up\":string", "@buffer:decorator", "5:number",
            "Loudly:macro", "\"hi\":string",
            "'after the broken line':string",
            "Tap:function", "@errors:decorator", "\"down\":string",
        ];
        foreach (var token in expected)
            await Assert.That(tokens).Contains(token);
        await Assert.That(tokens).Contains("WriteLine:function"); // the line after the one that doesn't parse
        await Assert.That(tokens.Count(t => t == "WriteLine:function")).IsGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task Semantic_tokens_send_only_what_changed_when_asked_for_a_delta()
    {
        const string Uri = "file:///tmp/delta.meep";
        const string Before = "Interval 1s\n    WriteLine \"{msg.Count}\"\n";
        const string After = "Interval 1s\n    Where msg.Count > 2\n    WriteLine \"{msg.Count}\"\n";
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Before);
        var full = (await lsp.RequestAsync("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = LspSession.Document(Uri) }))!;
        await lsp.ChangeAsync(Uri, After, version: 2);

        var delta = (await lsp.RequestAsync("textDocument/semanticTokens/full/delta", new JsonObject
        {
            ["textDocument"] = LspSession.Document(Uri),
            ["previousResultId"] = full["resultId"]!.GetValue<string>(),
        }))!;
        var unknown = (await lsp.RequestAsync("textDocument/semanticTokens/full/delta", new JsonObject
        {
            ["textDocument"] = LspSession.Document(Uri),
            ["previousResultId"] = "no such result",
        }))!;
        var fresh = (await lsp.RequestAsync("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = LspSession.Document(Uri) }))!;

        var data = full["data"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        var edit = delta["edits"]!.AsArray().Single()!;
        data.RemoveRange(edit["start"]!.GetValue<int>(), edit["deleteCount"]!.GetValue<int>());
        data.InsertRange(edit["start"]!.GetValue<int>(), edit["data"]!.AsArray().Select(n => n!.GetValue<int>()));
        await Assert.That(delta["resultId"]!.GetValue<string>()).IsNotEqualTo(full["resultId"]!.GetValue<string>());
        await Assert.That(edit["data"]!.AsArray().Count).IsLessThan(full["data"]!.AsArray().Count);
        await Assert.That(data).IsEquivalentTo(fresh["data"]!.AsArray().Select(n => n!.GetValue<int>()).ToList());
        await Assert.That(unknown["data"]).IsNotNull(); // an id it doesn't have gets the whole set
    }

    [Test]
    public async Task The_verb_catalogue_describes_each_inflection()
    {
        await using var lsp = new LspSession();

        var verbs = (await lsp.RequestAsync("meep/verbs"))!["verbs"]!.AsArray();
        JsonNode Find(string verb, string inflection) => verbs.Single(v => v!["verb"]!.GetValue<string>() == verb && v["inflection"]!.GetValue<string>() == inflection)!;
        JsonNode Parameter(JsonNode entry, string name) => entry["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == name)!;

        var fetch = Find("Fetch", "HTTP");
        await Assert.That(fetch["role"]!.GetValue<string>()).IsEqualTo("step");
        await Assert.That(fetch["kind"]!.GetValue<string>()).IsEqualTo("inflection");
        await Assert.That(fetch["description"]!.GetValue<string>()).IsNotEmpty();
        var url = fetch["parameters"]![0]!;
        await Assert.That(url["name"]!.GetValue<string>()).IsEqualTo("url");
        await Assert.That(url["positional"]!.GetValue<bool>()).IsTrue();
        await Assert.That(url["required"]!.GetValue<bool>()).IsTrue();
        await Assert.That(url["perMessage"]!.GetValue<bool>()).IsTrue();
        await Assert.That(url["type"]!.GetValue<string>()).IsEqualTo("text");
        await Assert.That(url["description"]!.GetValue<string>()).IsEqualTo("The address to fetch, worked out for each message.");
        await Assert.That(Parameter(fetch, "timeout")["type"]!.GetValue<string>()).IsEqualTo("duration");
        await Assert.That(Parameter(fetch, "timeout")["default"]!.GetValue<string>()).IsEqualTo("1m40s");
        await Assert.That(Parameter(fetch, "max-size")["default"]!.GetValue<string>()).IsEqualTo("16777216");
        await Assert.That(Parameter(fetch, "stream")["default"]!.GetValue<string>()).IsEqualTo("false");
        await Assert.That(fetch["emits"]!.AsArray().Select(e => e!["type"]!.GetValue<string>())).Contains("HttpResponse");

        var source = fetch["source"]!;
        var file = source["file"]!.GetValue<string>();
        var line = source["line"]!.GetValue<int>();
        await Assert.That(Path.GetFileName(file)).IsEqualTo("Fetch.cs");
        await Assert.That((await File.ReadAllLinesAsync(file))[line - 1]).Contains("class " + fetch["class"]!.GetValue<string>().Split('.')[^1]);

        var interval = Find("Interval", "Interval");
        await Assert.That(interval["role"]!.GetValue<string>()).IsEqualTo("source");
        await Assert.That(interval["takes"]!.AsArray()).IsEmpty();
        await Assert.That(interval["emits"]!.AsArray().Select(e => e!["type"]!.GetValue<string>())).IsEquivalentTo(["Tick"]);
        await Assert.That(Parameter(interval, "count-start")["default"]!.GetValue<string>()).IsEqualTo("1");
        await Assert.That(interval["reconfigurable"]!.GetValue<bool>()).IsTrue();

        var where = Find("Where", "Where");
        await Assert.That(where["role"]!.GetValue<string>()).IsEqualTo("filter");
        await Assert.That(where["roleDeclared"]!.GetValue<bool>()).IsTrue();
        await Assert.That(where["emitsInput"]!.GetValue<bool>()).IsTrue();
        await Assert.That(where["parameters"]![0]!["type"]!.GetValue<string>()).IsEqualTo("bool");
        await Assert.That(where["parameters"]![0]!["perMessage"]!.GetValue<bool>()).IsTrue();

        var tcp = Find("Listen", "TCP");
        await Assert.That(tcp["role"]!.GetValue<string>()).IsEqualTo("inlet");
        await Assert.That(tcp["emits"]!.AsArray().Single(e => e!["type"]!.GetValue<string>() == "TcpConnected")!["when"]!.GetValue<string>()).IsEqualTo("connections");

        var send = verbs.Where(v => v!["verb"]!.GetValue<string>() == "Send").ToList();
        await Assert.That(send.All(v => v!["role"]!.GetValue<string>() == "outlet" && v["kind"]!.GetValue<string>() == "passthrough")).IsTrue();
        await Assert.That(verbs.All(v => v!["plugin"] is null)).IsTrue();
    }

    [Test]
    public async Task The_verb_catalogue_has_a_files_plugins_verbs()
    {
        using var lab = new PluginLab();
        lab.Options = lab.Options with { NuGetSources = [lab.SignedFeed()] };
        const string Shout = """
            #plugin nuget:Meep2.TestPlugin 1.0.0
            Interval 10ms count=1
                Shout "tick"
            """;
        await lab.RunAsync(Shout, new PluginCatalog()); // meep run installs it into the cache
        var uri = UriOf(Path.Combine(lab.Path, "shout.meep"));
        await using var lsp = new LspSession(lab.Options);
        await lsp.OpenAsync(uri, Shout);

        var withPlugin = (await lsp.RequestAsync("meep/verbs", new JsonObject { ["textDocument"] = LspSession.Document(uri) }))!["verbs"]!.AsArray();
        var core = (await lsp.RequestAsync("meep/verbs"))!["verbs"]!.AsArray();
        var graph = GraphIn((await lsp.RequestAsync("meep/graph", GraphRequest(uri)))!);

        var shout = withPlugin.Single(v => v!["verb"]!.GetValue<string>() == "Shout")!;
        await Assert.That(shout["plugin"]!.GetValue<string>()).IsNotEmpty();
        await Assert.That(shout["parameters"]![0]!["positional"]!.GetValue<bool>()).IsTrue();
        await Assert.That(shout["emits"]!.AsArray().Select(e => e!["type"]!.GetValue<string>())).Contains("Shouted");
        await Assert.That(withPlugin.Count).IsEqualTo(core.Count + 1);
        await Assert.That(core.Any(v => v!["verb"]!.GetValue<string>() == "Shout")).IsFalse();
        await Assert.That(graph.Modules[1].Plugin).IsEqualTo(shout["plugin"]!.GetValue<string>());
    }
}
