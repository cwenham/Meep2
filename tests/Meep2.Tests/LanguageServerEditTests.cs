using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>
/// <c>meep/applyEdits</c>: Meep2's own edits made to an open document's text, answered with the changes to the
/// text, for an editor that applies them itself (Synesthesia's canvas), and previews of what they'd make.
/// </summary>
public class LanguageServerEditTests
{
    private const string Uri = "file:///tmp/site.meep";

    // Keys: m1 Interval, m2 Check, m3 Where, m4 WriteLine "up", m5 Tap, m6 WriteLine "down".
    private const string Site = """
        // Checks the site every 30 seconds.
        const Site = "https://example.com"

        Interval 30s
            Fetch Site as Check   // the request
                timeout = 10s

                // Only good answers.
                Where msg.StatusCode == 200
                    WriteLine "up"

        Tap Check @errors
            WriteLine "down"   // to the log
        """;

    private const string SiteKeys = "m1=Interval #1 m2=Check m3=Where #3 m4=WriteLine #4 m5=Tap #5 m6=WriteLine #6";

    private static JsonObject Edit(string? module, string kind, string? name = null, string? value = null)
    {
        var edit = new JsonObject { ["kind"] = kind };
        if (module is not null)
            edit["module"] = module;
        if (name is not null)
            edit["name"] = name;
        if (value is not null)
            edit["value"] = value;
        return edit;
    }

    private static JsonObject Request(string uri, int version, bool preview, params JsonObject[] edits)
    {
        var request = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["edits"] = new JsonArray([.. edits.Select(e => e.DeepClone())]),
        };
        if (preview)
            request["preview"] = true;
        return request;
    }

    // Applies an answer's TextEdits as an editor does: each placed in the text as it was, the last first.
    private static string Applied(string text, JsonNode answer)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }
        int Offset(JsonNode position) => lineStarts[position["line"]!.GetValue<int>()] + position["character"]!.GetValue<int>();
        var edits = answer["edits"]!.AsArray().Select(e => (Start: Offset(e!["range"]!["start"]!), End: Offset(e["range"]!["end"]!), Text: e["newText"]!.GetValue<string>())).ToList();
        foreach (var (start, end, newText) in edits.OrderByDescending(e => e.Start))
            text = text[..start] + newText + text[end..];
        return text;
    }

    private static PipelineGraph GraphIn(JsonNode answer) => answer["graph"]!.Deserialize(DaemonJson.Default.PipelineGraph)!;

    private static string Json(PipelineGraph graph) => JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph);

    private static string Keys(PipelineGraph graph) => string.Join(" ", graph.Modules.Select(m => $"{m.Key}={m.Module}"));

    private static string Added(JsonNode answer) => string.Join(" ", answer["added"]!.AsArray().Select(a => $"{a!["edit"]}:{a["key"]}"));

    private static async Task<PipelineGraph> GraphAsync(LspSession lsp, string uri = Uri) =>
        GraphIn((await lsp.RequestAsync("meep/graph", new JsonObject { ["textDocument"] = LspSession.Document(uri) }))!);

    // Asks for edits, applies what comes back as the editor would, and sends the new text.
    private static async Task<(string Text, JsonNode Answer)> EditAsync(LspSession lsp, string text, int version, params JsonObject[] edits)
    {
        var answer = (await lsp.RequestAsync("meep/applyEdits", Request(Uri, version, preview: false, edits)))!;
        var next = Applied(text, answer);
        await lsp.ChangeAsync(Uri, next, version + 1);
        return (next, answer);
    }

    private static async Task<JsonNode> RefusedAsync(LspSession lsp, JsonObject request)
    {
        var answer = await lsp.SendRequestAsync("meep/applyEdits", request);
        return answer["error"] ?? throw new InvalidOperationException("It wasn't refused: " + answer.ToJsonString());
    }

    [Test]
    [Arguments("set", "m2", "timeout", "5s", "timeout = 10s", "timeout = 5s", SiteKeys, "")]
    [Arguments("set", "m1", null, "1m", "Interval 30s", "Interval 1m", SiteKeys, "")]
    [Arguments("unset", "m2", "timeout", null, "        timeout = 10s\n", "", SiteKeys, "")]
    [Arguments("set-modifier", "m4", "buffer", "5", "WriteLine \"up\"", "WriteLine \"up\" @buffer=5", SiteKeys, "")]
    [Arguments("remove-modifier", "m5", "errors", null, "Tap Check @errors", "Tap Check", SiteKeys, "")]
    [Arguments("rename", "m4", null, "Up", "WriteLine \"up\"", "WriteLine \"up\" as Up", "m1=Interval #1 m2=Check m3=Where #3 m4=Up m5=Tap #5 m6=WriteLine #6", "")]
    [Arguments("add", "m1", null, "WriteLine \"tick\"",
        "            WriteLine \"up\"\n", "            WriteLine \"up\"\n    WriteLine \"tick\"\n",
        "m1=Interval #1 m2=Check m3=Where #3 m4=WriteLine #4 m7=WriteLine #5 m5=Tap #6 m6=WriteLine #7", "0:m7")]
    [Arguments("insert", "m3", null, "Where msg.StatusCode < 300",
        "            WriteLine \"up\"", "            Where msg.StatusCode < 300\n                WriteLine \"up\"",
        "m1=Interval #1 m2=Check m3=Where #3 m7=Where #4 m4=WriteLine #5 m5=Tap #6 m6=WriteLine #7", "0:m7")]
    [Arguments("insert", "m1", null, "Where msg.Count > 0",
        "Interval 30s\n    Fetch Site as Check   // the request\n        timeout = 10s\n\n        // Only good answers.\n        Where msg.StatusCode == 200\n            WriteLine \"up\"",
        "Interval 30s\n    Where msg.Count > 0\n        Fetch Site as Check   // the request\n            timeout = 10s\n\n            // Only good answers.\n            Where msg.StatusCode == 200\n                WriteLine \"up\"",
        "m1=Interval #1 m7=Where #2 m2=Check m3=Where #4 m4=WriteLine #5 m5=Tap #6 m6=WriteLine #7", "0:m7")]
    [Arguments("remove", "m3", null, null,
        "        // Only good answers.\n        Where msg.StatusCode == 200\n            WriteLine \"up\"", "        WriteLine \"up\"",
        "m1=Interval #1 m2=Check m4=WriteLine #3 m5=Tap #4 m6=WriteLine #5", "")]
    [Arguments("remove-branch", "m3", null, null,
        "        // Only good answers.\n        Where msg.StatusCode == 200\n            WriteLine \"up\"\n", "",
        "m1=Interval #1 m2=Check m5=Tap #3 m6=WriteLine #4", "")]
    public async Task Each_kind_of_edit_names_its_module_by_key_and_keeps_comments_and_blank_lines(
        string kind, string module, string? name, string? value, string find, string replace, string keys, string added)
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site, version: 4);
        await Assert.That(Keys(await GraphAsync(lsp))).IsEqualTo(SiteKeys);

        var (text, answer) = await EditAsync(lsp, Site, 4, Edit(module, kind, name, value));
        var after = await GraphAsync(lsp);

        await Assert.That(text).IsEqualTo(Site.Replace(find, replace, StringComparison.Ordinal));
        await Assert.That(answer["textDocument"]!["version"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(Keys(after)).IsEqualTo(keys);
        await Assert.That(Added(answer)).IsEqualTo(added);
        await Assert.That(after.Version).IsEqualTo(2);
    }

    [Test]
    public async Task Modules_can_be_named_by_name_and_number_too_and_a_key_follows_its_module_through_the_list()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);

        // A WriteLine added at the bottom, then a Where inserted at the top, which renumbers everything under it,
        // then the Where that was #3 changed by its key, and Check and WriteLine "down" by name and number.
        var (text, answer) = await EditAsync(lsp, Site, 1,
            Edit("m6", "add", value: "WriteLine \"logged\""),
            Edit("m1", "insert", value: "Where msg.Count > 0"),
            Edit("m3", "set", value: "msg.StatusCode == 201"),
            Edit("Check", "set", "timeout", "5s"),
            Edit("7", "set-modifier", "buffer", "2"));
        var after = await GraphAsync(lsp);

        await Assert.That(text).IsEqualTo("""
            // Checks the site every 30 seconds.
            const Site = "https://example.com"

            Interval 30s
                Where msg.Count > 0
                    Fetch Site as Check   // the request
                        timeout = 5s

                        // Only good answers.
                        Where msg.StatusCode == 201
                            WriteLine "up"

            Tap Check @errors
                WriteLine "down" @buffer=2 // to the log
                    WriteLine "logged"
            """);
        await Assert.That(Keys(after)).IsEqualTo("m1=Interval #1 m7=Where #2 m2=Check m3=Where #4 m4=WriteLine #5 m5=Tap #6 m6=WriteLine #7 m8=WriteLine #8");

        // New keys go in the order of the file, so the module the first edit added has the later one.
        await Assert.That(Added(answer)).IsEqualTo("0:m8 1:m7");
    }

    [Test]
    public async Task An_edit_can_name_the_module_an_earlier_edit_added()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);

        // A Where inserted, a WriteLine added under it, @buffer on that, and the Where's condition changed: a branch
        // built in steps, as Claude proposes one.
        var (text, answer) = await EditAsync(lsp, Site, 1,
            Edit("m3", "insert", value: "Where msg.StatusCode < 300"),
            Edit("+0", "add", value: "WriteLine \"fast\""),
            Edit("+1", "set-modifier", "buffer", "2"),
            Edit("+0", "set", value: "msg.StatusCode < 400"));
        var after = await GraphAsync(lsp);

        await Assert.That(text).IsEqualTo(Site.Replace("""
                    Where msg.StatusCode == 200
                        WriteLine "up"
            """, """
                    Where msg.StatusCode == 200
                        Where msg.StatusCode < 400
                            WriteLine "up"
                            WriteLine "fast" @buffer=2
            """, StringComparison.Ordinal));
        await Assert.That(Added(answer)).IsEqualTo("0:m7 1:m8");
        await Assert.That(Keys(after)).IsEqualTo("m1=Interval #1 m2=Check m3=Where #3 m7=Where #4 m4=WriteLine #5 m8=WriteLine #6 m5=Tap #7 m6=WriteLine #8");
    }

    [Test]
    public async Task Plus_n_names_only_a_module_an_earlier_edit_added_and_that_is_still_there()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);

        var itself = await RefusedAsync(lsp, Request(Uri, 1, preview: false, Edit("m3", "insert", value: "Where msg.StatusCode < 300"), Edit("+1", "remove")));
        var notAdded = await RefusedAsync(lsp, Request(Uri, 1, preview: false, Edit("m2", "set", "timeout", "5s"), Edit("+0", "remove")));
        var removed = await RefusedAsync(lsp, Request(Uri, 1, preview: false,
            Edit("m3", "insert", value: "Where msg.StatusCode < 300"),
            Edit("+0", "remove"),
            Edit("+0", "set", value: "msg.StatusCode < 400")));

        await Assert.That(itself["data"]!["edit"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(itself["message"]!.GetValue<string>()).IsEqualTo("Edit 2 of 2 (remove +1) can't be made. It names +1, the module edit 2 adds, but only an edit before it can add a module it names. Nothing was changed.");
        await Assert.That(notAdded["message"]!.GetValue<string>()).IsEqualTo("Edit 2 of 2 (remove +0) can't be made. It names +0, the module edit 1 adds, but that edit (set m2's timeout to 5s) doesn't add a module: only add and insert do. Nothing was changed.");
        await Assert.That(removed["data"]!["edit"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(removed["message"]!.GetValue<string>()).IsEqualTo("Edit 3 of 3 (set +0's value to msg.StatusCode < 400) can't be made. It names +0, the module edit 1 added, but an edit after that took it out. Nothing was changed.");
    }

    [Test]
    public async Task Plus_n_for_a_line_added_to_a_template_names_the_line_so_every_use_changes()
    {
        const string Text = """
            template Shout(Text)
                Where msg.Count > 1
                    WriteLine Text

            Interval 10ms count=3
                Shout "a"
                Shout "b"
            """;
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Text);

        // The line goes in the template, so each use has a module; +0 is that line, as an edit to either copy is.
        var (text, answer) = await EditAsync(lsp, Text, 1,
            Edit("m3", "add", value: "WriteLine \"again\""),
            Edit("+0", "set", value: "\"once more\""));
        var after = await GraphAsync(lsp);

        await Assert.That(text).IsEqualTo(Text.Replace("        WriteLine Text\n", "        WriteLine Text\n            WriteLine \"once more\"\n", StringComparison.Ordinal));
        await Assert.That(Added(answer)).IsEqualTo("0:m6 0:m7");
        await Assert.That(after.Modules.Where(m => m.Key is "m6" or "m7").Select(m => m.Parameters[0].Text)).IsEquivalentTo(["\"once more\"", "\"once more\""]);
    }

    // A module inserted above one with the same verb further down: the old one is unchanged, so it keeps its key,
    // though it's now a level further in, and the new one gets a new key (Q195).
    [Test]
    [Arguments("Where msg.StatusCode < 500", "m1=Interval #1 m2=Check m7=Where #3 m3=Where #4 m4=WriteLine #5 m5=Tap #6 m6=WriteLine #7")]
    [Arguments("WriteLine \"checked\"", "m1=Interval #1 m2=Check m7=WriteLine #3 m3=Where #4 m4=WriteLine #5 m5=Tap #6 m6=WriteLine #7")]
    public async Task A_module_inserted_above_one_with_the_same_verb_leaves_it_its_key(string line, string keys)
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);

        var (text, answer) = await EditAsync(lsp, Site, 1, Edit("m2", "insert", value: line));
        var after = await GraphAsync(lsp);

        await Assert.That(text).Contains($"        {line}\n\n            // Only good answers.\n            Where msg.StatusCode == 200");
        await Assert.That(Keys(after)).IsEqualTo(keys);
        await Assert.That(Added(answer)).IsEqualTo("0:m7");
        await Assert.That(after.Modules.Single(m => m.Key == "m7").Span.StartLine).IsEqualTo(7);
    }

    [Test]
    public async Task An_edit_to_a_module_a_template_in_the_file_made_goes_to_the_template()
    {
        const string Text = """
            template Shout(Text)
                Where msg.Count > 1
                    WriteLine Text

            Interval 10ms count=3
                Shout "a"   // first
                Shout "b"
            """;
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Text);
        await Assert.That(Keys(await GraphAsync(lsp))).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3 m4=Where #4 m5=WriteLine #5");

        // The second use's Where, by key: the template's line changes, so both uses do.
        var (set, _) = await EditAsync(lsp, Text, 1, Edit("m4", "set", value: "msg.Count > 2"));
        var afterSet = await GraphAsync(lsp);

        // Under the first use's WriteLine: the template gets the line, so each use gets a module, each with a key.
        var (added, answer) = await EditAsync(lsp, set, 2, Edit("m3", "add", value: "WriteLine \"again\""));
        var afterAdd = await GraphAsync(lsp);

        await Assert.That(set).IsEqualTo(Text.Replace("msg.Count > 1", "msg.Count > 2", StringComparison.Ordinal));
        await Assert.That(afterSet.Modules.Where(m => m.Verb == "Where").Select(m => m.Parameters[0].Text)).IsEquivalentTo(["msg.Count > 2", "msg.Count > 2"]);
        await Assert.That(added).IsEqualTo(set.Replace("        WriteLine Text\n", "        WriteLine Text\n            WriteLine \"again\"\n", StringComparison.Ordinal));
        await Assert.That(Added(answer)).IsEqualTo("0:m6 0:m7");
        await Assert.That(Keys(afterAdd)).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3 m6=WriteLine #4 m4=Where #5 m5=WriteLine #6 m7=WriteLine #7");
        await Assert.That(afterAdd.Modules.Where(m => m.Key is "m6" or "m7").Select(m => m.Span.StartLine)).IsEquivalentTo([4, 4]);
    }

    [Test]
    public async Task An_edit_to_a_module_a_template_in_an_included_file_made_is_refused()
    {
        using var folder = new TemporaryFolder();
        var shared = folder.Write("shared.meep", """
            template Shout(Text)
                Where msg.Count > 1
                    WriteLine Text
            """);
        const string Loud = """
            #include "shared.meep"

            Interval 10ms count=3
                Shout "x"
            """;
        var loud = folder.Write("loud.meep", Loud);
        var uri = new Uri(loud).ToString();
        await using var lsp = new LspSession();
        await lsp.OpenAsync(uri, Loud);
        await Assert.That(Keys(await GraphAsync(lsp, uri))).IsEqualTo("m1=Interval #1 m2=Where #2 m3=WriteLine #3");

        var refused = await RefusedAsync(lsp, Request(uri, 1, preview: false, Edit("m2", "set", value: "msg.Count > 2")));

        // The template's use is in this file, so the values it gives can be changed.
        var answer = (await lsp.RequestAsync("meep/applyEdits", Request(uri, 1, preview: false, Edit("Shout", "set", value: "\"y\""))))!;

        await Assert.That(refused["code"]!.GetValue<int>()).IsEqualTo(-32803);
        await Assert.That(refused["data"]!["edit"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(refused["message"]!.GetValue<string>()).Contains($"Where #2 is copied from the template Shout in {shared}, and edits only change the pipeline's own file so far. Change {shared} itself.");
        await Assert.That(refused["message"]!.GetValue<string>()).EndsWith("Nothing was changed.");
        await Assert.That(Applied(Loud, answer)).IsEqualTo(Loud.Replace("Shout \"x\"", "Shout \"y\"", StringComparison.Ordinal));
    }

    [Test]
    public async Task Edits_for_a_version_that_isnt_the_latest_are_refused()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site, version: 1);
        await lsp.ChangeAsync(Uri, Site + "\n", version: 2);

        var behind = await RefusedAsync(lsp, Request(Uri, 1, preview: false, Edit("m2", "set", "timeout", "5s")));
        var ahead = await RefusedAsync(lsp, Request(Uri, 3, preview: false, Edit("m2", "set", "timeout", "5s")));
        var current = (await lsp.RequestAsync("meep/applyEdits", Request(Uri, 2, preview: false, Edit("m2", "set", "timeout", "5s"))))!;

        await Assert.That(behind["code"]!.GetValue<int>()).IsEqualTo(-32801);
        await Assert.That(behind["message"]!.GetValue<string>()).StartsWith("The edits are for version 1 of the text, and the server has version 2. Nothing was changed.");
        await Assert.That(behind["data"]!["version"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(ahead["code"]!.GetValue<int>()).IsEqualTo(-32801);
        await Assert.That(current["edits"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_list_is_made_as_one_so_a_later_edit_that_fails_stops_it_all()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);
        var before = await GraphAsync(lsp);

        var missing = await RefusedAsync(lsp, Request(Uri, 1, preview: false,
            Edit("m2", "set", "timeout", "5s"),
            Edit("m9", "remove")));
        var unreadable = await RefusedAsync(lsp, Request(Uri, 1, preview: false,
            Edit("m2", "set", "timeout", "5s"),
            Edit("m3", "set", value: "msg.StatusCode ==")));
        var unloadable = await RefusedAsync(lsp, Request(Uri, 1, preview: false,
            Edit("m2", "set", "timeout", "5s"),
            Edit("m3", "insert", value: "Fetch"),
            Edit("m4", "rename", value: "Up")));
        var unknown = await RefusedAsync(lsp, Request(Uri, 1, preview: false,
            Edit("m2", "set", "timeout", "5s"),
            Edit("m3", "replace")));

        await Assert.That(missing["data"]!["edit"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(missing["message"]!.GetValue<string>()).IsEqualTo("Edit 2 of 2 (remove m9) can't be made. There's no module with the key m9 after the edits before it. The keys are m1 (Interval #1), m2 (Check), m3 (Where #3), m4 (WriteLine #4), m5 (Tap #5), m6 (WriteLine #6). Nothing was changed.");
        await Assert.That(unreadable["data"]!["edit"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(unreadable["message"]!.GetValue<string>()).StartsWith("Edit 2 of 2 (set m3's value to msg.StatusCode ==) can't be made. It would leave text Meeplang can't read:");
        await Assert.That(unreadable["message"]!.GetValue<string>()).Contains("(line 9: Where msg.StatusCode ==)");
        await Assert.That(unloadable["data"]!["edit"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(unloadable["message"]!.GetValue<string>()).StartsWith("Edit 2 of 3 (insert Fetch after m3) leaves text that doesn't load: Fetch needs a value after its name, for its url. (line 10: Fetch)");
        await Assert.That(unloadable["message"]!.GetValue<string>()).Contains("So edit 3 of 3 (name m4 Up) can't find the module with the key m4.");
        await Assert.That(unknown["code"]!.GetValue<int>()).IsEqualTo(-32602);
        await Assert.That(unknown["data"]!["edit"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(unknown["message"]!.GetValue<string>()).StartsWith("Edit 2 of 2 isn't one meep lsp can make: There's no edit called replace.");

        // Nothing changed: the server's text and graph are as they were.
        await Assert.That(Json(await GraphAsync(lsp))).IsEqualTo(Json(before));
        var again = (await lsp.RequestAsync("meep/applyEdits", Request(Uri, 1, preview: false, Edit("m2", "set", "timeout", "5s"))))!;
        await Assert.That(Applied(Site, again)).IsEqualTo(Site.Replace("timeout = 10s", "timeout = 5s", StringComparison.Ordinal));
    }

    [Test]
    public async Task Text_that_doesnt_load_takes_no_edits()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site.Replace("WriteLine \"up\"", "WriteLine \"up", StringComparison.Ordinal));

        var refused = await RefusedAsync(lsp, Request(Uri, 1, preview: false, Edit("m2", "set", "timeout", "5s")));

        await Assert.That(refused["code"]!.GetValue<int>()).IsEqualTo(-32803);
        await Assert.That(refused["message"]!.GetValue<string>()).StartsWith("The text doesn't load as it is (line 10: This string isn't closed");
        await Assert.That(refused["data"]!["edit"]).IsNull();
    }

    [Test]
    public async Task Comments_and_blank_lines_stay_and_the_changes_touch_only_what_the_edits_change()
    {
        const string Text = """
            // The site check.
            //
            // Every 30 seconds.

            const Site = "https://example.com"   // where


            Interval 30s   // how often
                // Fetch it.
                Fetch Site as Check
                    // Not too long.
                    timeout = 10s   // or it's down
                    max-size = 1048576

                    WriteLine "up"   // good


            // Errors.
            Tap Check @errors
                WriteLine "down"
            """;
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Text);

        var (text, answer) = await EditAsync(lsp, Text, 1,
            Edit(null, "set-const", "Site", "\"https://example.org\""),
            Edit("m2", "set", "timeout", "1m"),
            Edit("m3", "set-modifier", "buffer", "5"),
            Edit("m5", "add", value: "WriteLine \"logged\""));

        await Assert.That(text).IsEqualTo("""
            // The site check.
            //
            // Every 30 seconds.

            const Site = "https://example.org"   // where


            Interval 30s   // how often
                // Fetch it.
                Fetch Site as Check
                    // Not too long.
                    timeout = 1m    // or it's down
                    max-size = 1048576

                    WriteLine "up" @buffer=5 // good


            // Errors.
            Tap Check @errors
                WriteLine "down"
                    WriteLine "logged"
            """);

        // Each a few characters, but the line added at the end, which is one change of whole lines.
        await Assert.That(answer["edits"]!.ToJsonString()).IsEqualTo("""
            [{"range":{"start":{"line":4,"character":30},"end":{"line":4,"character":33}},"newText":"org"},{"range":{"start":{"line":11,"character":19},"end":{"line":11,"character":21}},"newText":"m "},{"range":{"start":{"line":14,"character":23},"end":{"line":14,"character":24}},"newText":"@buffer=5"},{"range":{"start":{"line":19,"character":20},"end":{"line":19,"character":20}},"newText":"\n        WriteLine \u0022logged\u0022"}]
            """);
        await Assert.That(Added(answer)).IsEqualTo("3:m6");
    }

    [Test]
    public async Task A_previews_graph_is_the_graph_the_text_has_once_the_edits_are_made()
    {
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);
        var first = (await lsp.RequestAsync("meep/graph", new JsonObject { ["textDocument"] = LspSession.Document(Uri), ["subscribe"] = true }))!;
        JsonObject[] edits = [
            Edit("m3", "insert", value: "Where msg.StatusCode < 300"),
            Edit("m2", "set", "timeout", "5s"),
            Edit("m1", "add", value: "WriteLine \"tick\"")];

        var preview = (await lsp.RequestAsync("meep/applyEdits", Request(Uri, 1, preview: true, edits)))!;
        await lsp.SyncAsync();
        var unchanged = await GraphAsync(lsp);
        var noticesBefore = lsp.Notifications("meep/graphChanged").Count;

        await lsp.ChangeAsync(Uri, Applied(Site, preview), version: 2);
        await lsp.SyncAsync();
        var after = (await lsp.RequestAsync("meep/graph", new JsonObject { ["textDocument"] = LspSession.Document(Uri) }))!;
        var plain = (await lsp.SendRequestAsync("meep/applyEdits", Request(Uri, 1, preview: false, edits)))["error"];

        // Previewing changed nothing.
        await Assert.That(noticesBefore).IsEqualTo(0);
        await Assert.That(Json(unchanged)).IsEqualTo(Json(GraphIn(first)));

        // The preview's graph is the graph after the edits, byte for byte, version and all, with the keys it promised.
        await Assert.That(Json(GraphIn(preview))).IsEqualTo(Json(GraphIn(after)));
        await Assert.That(Json(GraphIn(lsp.Notifications("meep/graphChanged").Single()))).IsEqualTo(Json(GraphIn(preview)));
        await Assert.That(GraphIn(preview).Version).IsEqualTo(2);
        await Assert.That(Added(preview)).IsEqualTo("0:m7 2:m8");
        await Assert.That(Keys(GraphIn(after))).IsEqualTo("m1=Interval #1 m2=Check m3=Where #3 m7=Where #4 m4=WriteLine #5 m8=WriteLine #6 m5=Tap #7 m6=WriteLine #8");
        await Assert.That(preview["renamed"]).IsNull();

        // A preview's version is the version it was for, so it can't be applied after another change.
        await Assert.That(plain!["code"]!.GetValue<int>()).IsEqualTo(-32801);
    }

    [Test]
    public async Task With_a_base_new_modules_are_keyed_after_the_daemons_and_the_preview_agrees()
    {
        // The daemon's keys for this text, as though it had been reloaded: m11 to m16.
        var syntax = MeeplangParser.Parse(new SourceFile("/tmp/site.meep", Site));
        var (live, diagnostics) = Meep2.Meeplang.Editing.LivePipeline.Load(syntax, new Meep2.Meeplang.Loading.ModuleRegistry().AddAssembly(CoreModules.Assembly));
        if (live is null)
            throw new InvalidOperationException(string.Join("\n", diagnostics));
        PipelineGraph daemon;
        await using (live.Pipeline)
            daemon = Graphs.Describe("site", "/tmp/site.meep", live, version: 3);
        daemon = daemon with { Modules = [.. daemon.Modules.Select(m => m with { Key = "m1" + m.Key[1..] })] };
        await using var lsp = new LspSession();
        await lsp.OpenAsync(Uri, Site);
        await lsp.RequestAsync("meep/setBase", new JsonObject
        {
            ["textDocument"] = LspSession.Document(Uri),
            ["pipeline"] = "site",
            ["graph"] = JsonSerializer.SerializeToNode(daemon, DaemonJson.Default.PipelineGraph),
            ["text"] = Site,
        });

        var preview = (await lsp.RequestAsync("meep/applyEdits", Request(Uri, 1, preview: true,
            Edit("m13", "insert", value: "Where msg.StatusCode < 300"),
            Edit("m15", "remove-modifier", "errors"))))!;
        await lsp.ChangeAsync(Uri, Applied(Site, preview), version: 2);
        var after = await GraphAsync(lsp);

        await Assert.That(Keys(GraphIn(preview))).IsEqualTo("m11=Interval #1 m12=Check m13=Where #3 m17=Where #4 m14=WriteLine #5 m15=Tap #6 m16=WriteLine #7");
        await Assert.That(Added(preview)).IsEqualTo("0:m17");
        await Assert.That(Json(after)).IsEqualTo(Json(GraphIn(preview)));
    }
}
