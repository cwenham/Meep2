# The pipeline graph, and verb roles

Written 15 November 2026. The first of Synesthesia's asks (prompt 1 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`): the pipeline as a graph over the daemon's API, with a key for each module that lasts through reloads, notices when the graph changes, and a role for every verb. This covers what was built, what it cost, the decisions I made while building it, and questions numbered from 171 with answer lines. Nothing is committed.

## What's built

### `GET /pipelines/{name}/graph`

Over HTTP and over `/ws` like the rest of the API, with the observe capability. For `examples/watchdog.meep` it answers (shortened):

```json
{
  "name": "watchdog", "path": "/…/examples/watchdog.meep", "version": 1,
  "modules": [
    { "key": "m1", "number": 1, "module": "Interval #1", "verb": "Interval", "role": "source",
      "inflections": ["Interval"],
      "parameters": [{ "text": "30s", "span": { "file": "/…/watchdog.meep", "startLine": 9, "startColumn": 10, "endLine": 9, "endColumn": 13 } }],
      "modifiers": [], "span": { "file": "/…/watchdog.meep", "startLine": 9, "startColumn": 1, "endLine": 9, "endColumn": 13 }, "feeder": false },
    { "key": "m2", "number": 2, "module": "Check", "verb": "Fetch", "role": "step", "inflections": ["HTTP"],
      "parameters": [{ "text": "Site", "span": … }, { "name": "timeout", "text": "10s", "span": … }],
      "span": { …, "startLine": 10, "startColumn": 5, "endLine": 11, "endColumn": 22 }, "name": "Check", "under": "m1" },
    …
    { "key": "m4", "number": 4, "module": "Tap #4", "verb": "Tap", "role": "tap",
      "modifiers": [{ "name": "errors", "span": { …, "startLine": 14, "startColumn": 11, "endLine": 14, "endColumn": 18 } }], … }
  ],
  "links": [
    { "key": "m1>m2", "from": "m1", "to": "m2", "input": 1, "port": "main", "types": ["ErrorMessage", "Tick"], "undeclared": false },
    { "key": "m2>m4", "from": "m2", "to": "m4", "input": 1, "port": "errors", "types": ["ErrorMessage"], "undeclared": false },
    …
  ]
}
```

- **Modules** have their key, number, label, verb, name, plugin, role, the inflections the loader found their parameters fit (`Fetch Site` with an `https://` constant can only be `HTTP`), their parameters and modifiers as written, the key of the module they're indented under, whether the line starts with `+`, and for a Distribute copy, the key of its original.
- **Spans** say where each module is written: from its `+` or verb to the end of its last parameter line, without the modules indented under it. Each parameter and modifier has its own span too (`name=value` whole, brackets and all), and its text exactly as written. Lines and columns count from 1, columns in UTF-16 code units (as C#, LSP and Swift's `utf16` view do, with a tab as one), and the end is just after the last character.
- **Templates and includes.** A module a template made has its span in the template, in whichever file that's in, and `templates`: the use of the template it's written in, then the use that one is in, out to the pipeline's own line. Each parameter whose value the template filled in also has `value`, as in `"text": "msg.Count <= Times", "value": "msg.Count <= 1"`. A module written in an included file has `include`: the `#include` line that brought the file in, and where it is.
- **Links** have from, to (both keys), the input number at the receiving end (as Zip numbers them), the port, and the message types the loader works out for its type checks, by the names `is` takes. `undeclared` says a module before it doesn't declare what it emits, so other types may come too.
- **The port** is `errors` for a link into a module with `@errors` (a `Tap … @errors`, or an `@errors` module indented under another), `no-errors` for a link into a module with `@no-errors`, and `main` otherwise. Meep2 has no separate error output, so the port belongs to the receiving end; see Q177.
- **The version** counts up from 1 whenever anything in the graph changes, spans included.

### Keys that last

Each module has a key, `m1`, `m2` and so on by number when the pipeline loads. When its text changes, by a reload (a watched file's included) or an edit, `ModuleMatcher` pairs the new modules with the old as it already did to keep their state, and a module paired with an old one takes its key; a new module gets a key the pipeline hasn't used. So in the test that inserts a `Where`, Interval and WriteLine stay `m1` and `m2` though WriteLine becomes #3, and Where is `m3`. A restart keeps the keys too, and template copies made again on restart are matched back to their keys. Link keys are `from>to`, which the per-link counts in prompt 2 can use as they are.

### Change notices

On the stats stream (`GET /pipelines/{name}/stats`, and the same over `/ws`), which a client drawing particles has open anyway. When the graph changes, a `graph` event comes straight away, without waiting for the next tick, followed by fresh stats:

```
event: graph
data: {"version":2,"reason":"reload"}
```

`reason` is `reload`, `edit` or `restart`, and a `graph` event always means a new version. A restart that leaves the graph as it was (as restarts do today) sends none: it shows as `restarts` counting up in the stats, with the counts starting again. A reload that changes nothing sends nothing either. Every stats event also carries `graphVersion`, so a client that missed a notice notices on the next tick. `DaemonClient` has `GraphAsync` and `GraphChangesAsync` for C# clients.

### Verb roles

| Role | What the verb does | Worked out, or declared | Core verbs | Synesthesia's shape |
|---|---|---|---|---|
| `source` | Makes messages itself. | Any source not declared otherwise. | Interval, Read | circle |
| `inlet` | A source whose messages come in from outside. | Declared. | Listen | triangle pointing left |
| `filter` | Passes on some of what it receives, unchanged, and drops the rest. | Declared. | Where | inverted triangle |
| `outlet` | Sends messages out of the pipeline. | Declared. | Send, Save, WriteLine | triangle pointing right |
| `tap` | Receives another module's messages without being indented under it. | Declared. | Tap | not settled: perhaps a wire from the tapped module |
| `router` | Spreads messages over copies of the branch beneath it. | Any module that implements `IRouteMessages`. | Distribute | not settled |
| `step` | Anything else. | Any other module that handles messages. | Fetch, Extract, Enumerate, Batch, Zip, Learn, Classify, Forget | rectangle |

`[Role(VerbRole.Inlet)]` goes beside `[Verb]` in [Attributes.cs](../../../src/Meep2.SDK/Modules/Attributes.cs), so plugins can declare their own. The registry refuses a role that doesn't fit the class (an inlet that isn't a source, a filter without `[EmitsInput]`, a router that doesn't implement `IRouteMessages`) and inflections of one verb that disagree, as it already did for sources and passthroughs. The role is in the graph, in `ModuleRegistration.Role`, and in `meep lsp`'s hovers (`**Interval** (source)`) and completions; the catalogue in prompt 3 will carry it too. [verbs.md](../../verbs.md) has a Role column and [writing-verbs.md](../../writing-verbs.md#roles) a section on roles.

Where the code is: the contract in [Contracts.cs](../../../src/Meep2.Daemon/Contracts.cs), the graph built from it in [Graphs.cs](../../../src/Meep2.Daemon/Graphs.cs), versions and notices in [HostedPipeline.cs](../../../src/Meep2.Daemon/HostedPipeline.cs), keys and `LivePipeline.Shape()` in [LivePipeline.cs](../../../src/Meep2.Meeplang/Editing/LivePipeline.cs) and [PipelineShape.cs](../../../src/Meep2.Meeplang/Editing/PipelineShape.cs), and the tests in [GraphTests.cs](../../../tests/Meep2.Tests/GraphTests.cs). The graph is built in the daemon from a shape that Meeplang gives, so `meep lsp` can build the same contract from a document for prompt 3.

## Tests

Ten in `GraphTests` (the tenth, for `no-errors` links, added after your answers), and one assertion added to the hover test:

- `examples/merge.meep`: a `+` chain, two inputs numbered 1 and 2, exact spans for each parameter.
- `examples/watchdog.meep`: the `Tap Check @errors` branch on the errors port carrying only `ErrorMessage`, Fetch's span running over its `timeout` line but not the WriteLine under it.
- `examples/classify.meep`: two `+` chains that end at the Learns under the Reads, feeding Batch's two inputs.
- A template in an `#include`d file, used by a template in the pipeline: spans in the included file, both template uses in order, the `#include` line, and parameters as written and as filled in.
- Keys through a restart (`LivePipeline.Rebuild`), with templates copied in again.
- The daemon end to end: the graph over HTTP and over `/ws`, a reload inserting a module (keys kept, numbers changed, version 2, a `graph` event with reason `reload`), a reload of the same text (no notice, version unchanged), an edit (version 3, reason `edit`), and `graphVersion` in the stats.
- Every core verb's role, and a plugin's declared roles: accepted, refused when they don't fit, and refused when inflections disagree.

All 510 tests pass, though not yet in a single run; see the end of this report.

## What it cost

- **Nothing on the hot path.** The runtime isn't touched: no change to how messages are delivered or counted. The graph is built when it's asked for, and once after each reload, edit or restart to work out its version.
- **Building a graph** walks the modules and links once and serialises the result to compare it with the last, so it grows with the size of the pipeline, not its traffic. The loader now keeps what it already worked out for type checks (inflections, upstream modules, output types) instead of dropping it, a few small lists per module.
- **The parser** records two more spans per module and one per parameter, and template copies carry the list of uses that made them.
- **Code:** about 600 lines added across 20 files, a third of them documentation comments, plus three new files (the contract builder, the shape, the tests).

## Decisions I made while building

- **Keys are opaque, `m` and a number**, rather than names or verbs, so nobody mistakes them for something to write in Meeplang. They're `m1`… by number when a pipeline loads, so the same file started again has the same keys.
- **Links point at keys, not numbers,** so a client keyed by module key never has to translate. The number is on each module for the rest of the API.
- **The version counts changes rather than hashing them,** so a client can tell newer from older. It compares the graph's whole JSON with the last, so it can't miss a change and doesn't count a no-op reload. Asking for the graph mid-change returns the version that matches what's returned.
- **The stats stream carries the notices** rather than a new stream: clients drawing the graph want the counts anyway, and prompt 5's single subscription will take both over.
- **Errors appear in each link's types** (`ErrorMessage`), since any module can fail and errors go down every link except into `@no-errors` modules. A link into an `@errors` module carries only error types.
- **Parameters as written come from the original line,** so a template's parameter reads as the template has it (`Times`), with `value` for what the use filled in. Writing Meeplang for a template-made module means editing the template, which changes every use: `templates` is there so Synesthesia can tell.
- **Spans exclude the modules underneath,** since a move or merge rewrites a subtree from its children's spans, and a module's own span is what an edit of it replaces.
- **A graph is served for a pipeline that has ended,** showing what it ran, as `GET /pipelines/{name}` keeps its final counts.
- **`[EmitsInput]` doesn't make a filter.** Five core verbs emit their input and only Where is a filter; Tap, Learn, Forget and Distribute pass on everything. So filter is declared, and an `[EmitsInput]` verb that doesn't declare it is a step.
- **An outlet is declared** (changed after Q172): WriteLine, Save and every Send inflection carry `[Role(VerbRole.Outlet)]`, and a passthrough that doesn't is a step.

Noticed in passing, not changed: `SourceEditor` replaces a value by its expression's span, which leaves out brackets around a whole value, so setting `Where (a or b)` to `c` gives `Where (c)`. It works, but the new `PositionalSpan` and `ArgumentSyntax.Span` would make it exact. It belongs with prompt 4's edits.

## Questions

171. The roles are `source`, `inlet`, `filter`, `outlet` and `step`. Are those the names you want, or would you rather `receive`/`send` for the two triangles, or `transform` for the rectangle (though Tap and Learn don't transform anything)?

     **Answer:** Those names are fine.

172. Every passthrough is an outlet unless it declares otherwise. A future passthrough that doesn't send messages out (one that counts or logs them, say) would need `[Role(VerbRole.Step)]`. Is that the right default, or should outlet always be declared, leaving passthroughs as steps?

     **Answer:** Outlet needs to be declared.

173. Tap and Distribute are steps. Tap only relays another module's messages, so Synesthesia might draw it as a wire from that module rather than a box, and Distribute fans out to copies of a branch. Should either have a role of its own (`tap`, `router`), or is that the canvas's business, from the verb and the links?

     **Answer:** Lets add `tap` and `router` as roles.

174. Keys last through reloads, edits and restarts, but not through stopping and starting the pipeline (or the daemon): it starts again at `m1`…, by number. After edits that inserted modules, the same module may then have another key. Should keys be kept on disk with the daemon's state (say `~/.meep/daemon/keys/<pipeline>.json`, matched against the file when it starts), or is it enough that Synesthesia matches its saved layout against a fresh graph by verb, name and place, as `ModuleMatcher` does?

     **Answer:** No.

175. The version starts at 1 again when a pipeline is started again, so a client has to compare `started` too. Should the version be unique for good (a random start, or the start time folded in), or is the pair fine?

     **Answer:** The pair is fine.

176. A restart sends a `graph` notice though the graph is the same, because the counts start again. Would you rather restarts were only seen through `restarts` in the stats, so a `graph` event always means a new version?

     **Answer:** Yes.

177. Meep2 has no error output: errors go down every link, and a module with `@errors` takes only those. So `port` is `errors` when the receiving module has `@errors`, and `main` otherwise, including into an `@no-errors` module, which takes none. Is that the drawing you want, or should a link into `@no-errors` say so too (a third port value, or an `errors: false` flag)?

     **Answer:** A link into `@no-errors` should say so.

178. A parameter line can be written after the modules indented under its module, at the module's own indentation. Then the module's span runs over those modules. Should the parser refuse that (it's legal now, and no example does it), or should a span be a list of ranges?

     **Answer:** Can you explain this further with examples?

     **More on 178.** A parameter can be written on a line of its own, at the indentation of the modules under its module, as `timeout = 10s` is in `watchdog.meep`. The parser gives the line to whichever module owns that indentation, so it can also come after those modules:

     ```
     Interval 30s
         Fetch Site as Check
             WriteLine "{Site} is up"
             timeout = 10s
     ```

     Here `timeout = 10s` (line 4) belongs to Fetch, not WriteLine. A module's span runs from its verb to the end of its last parameter, so Fetch's span is lines 2 to 4 and takes in the WriteLine on line 3. It can take in a whole branch:

     ```
         Fetch Site as Check
             Extract "$.status"
                 WriteLine "{msg}"
             timeout = 10s
     ```

     That matters in two places. Synesthesia, highlighting Fetch's text when Fetch is selected, would highlight the Extract and WriteLine too. And an edit that rewrites a module by its span (prompt 4: changing Fetch's line, deleting Fetch but keeping what's under it, moving it) would overwrite or duplicate the modules inside the span. Every span today is still right as a range; it's only that it isn't Fetch's text alone.

     The choices:

     - **(a) Refuse it.** The parser says "Write Fetch's parameters before the modules under it," so a module's own lines always come together and one span covers them exactly. No example writes a parameter after a child, and the line reads as if it might belong to the module above it.
     - **(b) Make a span a list of ranges:** `[2:5–2:24, 4:9–4:22]`. Nothing written today stops working, but every client and every edit has to handle a module in pieces.
     - **(c) Allow it, but end the span before the first child** and leave the late parameter reachable only through its own span. Simpler for clients, but an edit of the module's span misses that parameter.

     I'd choose (a).

     **Answer:** Yes, refuse it.

## After your answers

Done, 16 November 2026, still uncommitted:

- **171, 174, 175:** nothing to change.
- **172:** an outlet is now declared. WriteLine, Save and the five Send inflections carry `[Role(VerbRole.Outlet)]`, and a passthrough that doesn't is a step.
- **173:** two new roles, `tap` and `router`. Tap declares `tap`; `router` is worked out for any module that implements `IRouteMessages` (Distribute), and refused on one that doesn't. Neither has a shape yet in the brief Synesthesia gave, so that's the canvas's call.
- **176:** a restart that leaves the graph as it was sends no `graph` event, so an event always means a new version; restarts show in the stats' `restarts`.
- **177:** a link into a module with `@no-errors` has the port `no-errors`, and its types already leave out `ErrorMessage`.
- **178:** the parser now refuses a parameter line written after the modules under its module, saying "Write Fetch's parameters before the modules under it.", so a module's span never takes in another module. A parser test covers it, and `writing-verbs.md` says so where it shows parameter lines.
- Tests: the watchdog and core-roles tests now expect `tap` and `router`; a new test checks a `no-errors` link beside a `main` one; the plugin-roles test checks that an undeclared passthrough is a step, a declared one an outlet, and a router without `IRouteMessages` is refused. `verbs.md`, `writing-verbs.md` and the contract's comments say the same.

One name to be aware of: the SDK already has an `Outlet` type (the handle a module emits through from any thread), and `VerbRole.Outlet` now means something else. They don't clash in code, but `writing-verbs.md` uses both words.

## The test run

Before any change, all 501 tests passed in one run. With the changes, the full suite of 510 ran while the machine's load average was between 190 and 265 (other work was running). The nine new tests passed, and six older ones failed: two Detach tests, `A_pipeline_can_come_from_standard_input`, `Sends_to_one_address_share_a_connection`, `It_checks_plugins_verbs_from_the_cache_without_downloading_anything` and `Listen_takes_https_with_a_certificate`. All six start processes or connections under time limits, none touches the graph or roles, and each passed when run on its own straight afterwards. A clean full run on a quiet machine is still worth doing before committing.

After the changes from your answers (16 November), the full suite of 511 ran with the load average around 40: 510 passed. The one failure, `Zip_gives_up_on_a_message_past_its_limit_its_time_or_its_deadline`, is a timing test in code these changes don't touch, and it passed three times out of three when run on its own. The six that failed in the loaded run all passed this time.

After refusing late parameter lines (Q178), the full suite of 512 ran with the load average around 7: 511 passed, and `Zip_gives_up_on_a_message_past_its_limit_its_time_or_its_deadline` failed again. It fails the same way in a full run of the unchanged commit (505aa54, 500 of 501), and passes when the Zip tests run alone, so it was already timing-sensitive before this work.
