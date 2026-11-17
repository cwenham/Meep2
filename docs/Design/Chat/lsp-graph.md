# `meep lsp`: the graph, keys that agree with the daemon's, semantic tokens and the verb catalogue

Written 16 November 2026. The third of Synesthesia's asks (prompt 3 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`, refreshed for Synesthesia's step 4): an open file's pipeline as the same graph the daemon gives, with keys that last while it's typed and agree with the running pipeline's; semantic tokens from Meep's own parser, used by the VS Code extension; and a catalogue of every verb and inflection. This covers what was built, what a client has to do, what it cost, the decisions I made while building it, and questions numbered from 183 with answer lines. Nothing is committed; the work is on the branch `lsp-graph`.

## What's built

### `meep/graph`

A request for an open document. It answers:

```json
{
  "uri": "file:///…/site.meep", "version": 3, "current": false,
  "graph": { "name": "site", "path": "/…/site.meep", "version": 2, "modules": [ … ], "links": [ … ] },
  "builtFrom": 2,
  "problems": [ { "uri": "file:///…/site.meep", "range": { "start": { "line": 4, "character": 22 }, … }, "message": "This string isn't closed with a \"." } ],
  "changed": { "start": { "line": 4, "character": 0 }, "end": { "line": 5, "character": 0 } },
  "renamed": { "m5": "m6", "m6": "m5" },
  "base": { "pipeline": "site", "version": 4 }
}
```

- **`graph`** is a `PipelineGraph`, built by the daemon's own `Graphs.Describe` from the same contract in `Contracts.cs`, so it's the daemon's graph byte for byte: a test checks the JSON is identical for `watchdog.meep`, `merge.meep`, `classify.meep` and the template-in-an-include example. Its spans count from 1, as the daemon's do. Its `version` counts up from 1 when anything in it changes, as the daemon's does, so text that's changed and changed back gives the same version.
- **`version`** is the document's version, from `didOpen` and `didChange`; **`builtFrom`** is the version of the text the graph was built from, and **`current`** says whether that's the text as it is now.
- **When the text doesn't load,** `graph` is the last one that did, rather than nothing. `problems` are the errors that stop it loading now (with LSP's ranges, counting from 0), and `changed` is the lines that differ from the text the graph was built from. The graph's spans before and after those lines are moved to where those lines are now, so pointing at a line further down still finds its tile; modules inside `changed` may have moved. A file that has never loaded has `graph: null` and its problems (Q188).
- **Includes** are read from the editor when they're open there, unsaved changes and all, and from disk when they aren't. Editing an included file checks the open files that include it again, so a template edited in `shared.meep` changes the graph of the pipeline that uses it.
- **`subscribe: true`** on the request opts in to `meep/graphChanged` notifications, which carry the same answer, sent whenever it changes (in practice, after each change to the text, since spans move as you type). `subscribe: false` stops them; closing the document does too.
- **`renamed`** maps keys from the graph's last version to this one, for modules whose key changed, and is left out when empty. With no base it's always empty; see below for when it isn't.
- `initialize` advertises them as `capabilities.experimental.meep: { graph, setBase, verbs }`.

### Keys that last while typing

`PipelineDraft` (in `src/Meep2.Meeplang/Editing`) loads each new text as far as the daemon's loader does (`PipelineLoader.Define`, which builds nothing and starts nothing) and matches its modules with the last text's by `ModuleMatcher`, as a reload does: a module matched with one keeps its key, and a new one gets a key the document hasn't used. So in the test that inserts a `Where` under a `Fetch`, everything else keeps its key though the WriteLine under it is renumbered, and the Where gets `m6`. The shape is built by the same code as the daemon's (`PipelineShape.Of`, moved out of `LivePipeline.Shape()` so both use it).

### Keys that agree with the daemon's: `meep/setBase`

Numbering a file afresh doesn't give the daemon's keys once the daemon has reloaded it: in the test, a Where was taken out and put back, so the daemon calls it `m4` where a fresh load says `m2`. And matching each keystroke against the last isn't what the daemon does either: on a reload it matches the text it ran against the saved text in one step. So the two can drift apart.

`meep/setBase` gives the document a **base**: the running pipeline's text and its keys.

```json
{ "textDocument": { "uri": "file:///…/chain.meep" }, "pipeline": "chain",
  "graph": { …the daemon's PipelineGraph… }, "text": "…the daemon's source text…" }
```

The server loads the base's text, checks that each of its modules is the daemon's module of that number and verb (refusing the base if not, say because the graph and text were fetched either side of a reload), and takes the daemon's keys for them. From then on, each new text is matched:

1. **with the base**, for modules the base has: exactly the matching the daemon will do when the text is saved and reloaded, so each of these modules has the key it has in the daemon now, and will still have after the reload. These are the ones with counts.
2. **with the last text**, for modules the base doesn't have: a module being written keeps its key while it's written.
3. A module that's in neither gets a new key, numbered after the base's highest.

The answer to `meep/setBase` is the same as `meep/graph`'s, with `renamed` for modules whose key changed, as the Where's `m2` → `m4` in the test.

**The one place they can still differ** is modules that aren't running yet. The daemon numbers new modules in the order they're in the file; the editor numbers them in the order they were written. In the test, a WriteLine is typed at the end (`m5`), then a Batch above the Where (`m6`); after saving, the daemon calls the Batch `m5` and the WriteLine `m6`. When the client sets the base again from the reloaded graph, the server re-keys and says `"renamed": { "m5": "m6", "m6": "m5" }`. Nothing had counts under those keys yet, so only the tiles' identities need carrying over. (Q183, Q185.)

#### What a client has to do

1. Open the document with `didOpen`, as any editor does.
2. If the file is running in a daemon (match the graph's `path` with the document's), fetch `GET /pipelines/{name}/graph` and `GET /pipelines/{name}/source`, and send `meep/setBase` with both. Use the answer's graph.
3. Ask for `meep/graph` with `subscribe: true`, and redraw from each `meep/graphChanged`. Key the canvas by module key; when an answer has `renamed`, move each tile from its old key to its new one before redrawing.
4. On each `graph` event from the daemon's stats stream (sent after a reload or an edit, with a new version), fetch the graph and source again and send `meep/setBase` again. Don't send it on `restarts`; those don't change keys.
5. Draw counts by the daemon's link and module keys. Every module the daemon runs has the same key in both, from step 2 until the document is closed, including while the text is broken and after a save.
6. If the pipeline stops, send `meep/setBase` with `graph: null`; keys stay as they are and new ones carry on from them.

`DaemonClient` has `GraphAsync`, `SourceAsync` and `GraphChangesAsync` for C# clients; Synesthesia does the same over HTTP or `/ws`.

### Semantic tokens

`textDocument/semanticTokens/full` and `/full/delta`, from `Highlighter` (in `src/Meep2.Meeplang/Editing`). Each line is lexed with Meep's own lexer, then what the parser made of the file is laid over that, so a word is coloured for what it is in that place:

| What | Token type |
|---|---|
| Verbs, and a template's name where it's used | `function`, `macro` |
| An inflection (`Fetch.HTTP`), a plugin's name (`Mine.Shout`) | `method`, `namespace` |
| A template's name and parameters where they're declared | `macro`, `parameter` with `declaration` |
| A module's name after `as` | `variable` with `declaration` |
| Named parameters | `parameter`; settings under `defaults` are `property` |
| Constants | `variable` with `readonly` (and `declaration` where declared) |
| `msg` and `fam` | `variable` with `readonly` and `defaultLibrary` |
| Members and method calls | `property`, `method` |
| The message type after `is` or `is not` | `type` |
| `@errors`, `@buffer=5` | `decorator` (the value is coloured as a value) |
| Keywords, `#include`, `#plugin`, `as`, `is`, `and`, `true` … | `keyword` |
| Strings, numbers and durations, operators, comments | `string`, `number`, `operator`, `comment` |

The expressions in a string's `{…}` holes are coloured inside the string, so `"{msg.Created.ToLocalTime():HH:mm} {Site} up"` shows `msg`, `Created`, `ToLocalTime` and the constant `Site` in their own colours. A line that doesn't parse is still coloured as far as the lexer goes, and the lines after it as usual. The names are LSP's own, so themes colour them without knowing Meeplang.

`/delta` keeps the last result for each document and sends one edit, the stretch between what's the same at the start and at the end. It was cheap: about thirty lines.

The VS Code extension turns semantic highlighting on for Meeplang (`configurationDefaults`) and maps the types to TextMate scopes (`semanticTokenScopes`) for themes that don't style semantic tokens. The client library already asks for them once the server offers them, so `extension.ts` didn't change. The TextMate grammar stays, for the moment before the server answers. `npm run test:editor` now checks the tokens in VS Code itself.

### `meep/verbs`

Every verb and inflection the document can use: the core modules', and the plugins its `#plugin` lines load. Without a `textDocument`, the core modules'. Each entry:

```json
{
  "verb": "Fetch", "inflection": "HTTP", "role": "step", "kind": "inflection",
  "parameters": [
    { "name": "url", "type": "text", "positional": true, "required": true, "perMessage": true,
      "description": "The address to fetch, worked out for each message." },
    { "name": "timeout", "type": "duration", "positional": false, "required": false, "perMessage": false,
      "default": "1m40s", "description": "How long to wait for the whole response. 100 seconds unless set." },
    …
  ],
  "takes": ["Message"], "emits": [{ "type": "HttpResponse" }],
  "roleDeclared": false, "emitsInput": false, "reconfigurable": false,
  "description": "Fetch's HTTP inflection: fetches a web address for each message it receives, …",
  "class": "Meep2.Modules.HttpFetch",
  "source": { "file": "/Users/…/src/Meep2.Modules/Fetch.cs", "line": 19,
              "url": "https://raw.githubusercontent.com/cwenham/Meep2/ee030d7…/src/Meep2.Modules/Fetch.cs" }
}
```

- **The role** is the graph's, with `roleDeclared` saying whether `[Role]` declared it. `kind` is `source`, `passthrough` or `inflection`.
- **Parameters** are in kebab case as Meeplang writes them, the positional one first. `type` is in Meeplang's words (`text`, `number`, `duration`, `bool`, `time`, `book`, `module`, `choice` with `choices`, `path`, `bytes`, `any`, `list of …`, `map of …`), and `perMessage` says it can be an expression worked out per message (`Computed<T>`). `default` is the value a parameters record starts with, as Meeplang would write it (`1m40s`, `"text"`, `false`, `16777216`); `required` ones have none (Q190).
- **Message types** by the names `is` takes. `emits` gives the `bool` parameter that turns a type on, as `{ "type": "TcpConnected", "when": "connections" }` on `Listen.TCP`; `emitsInput` says it passes on what it takes.
- **Descriptions** come from the XML documentation beside the assembly. `Meep2.Modules` now writes its documentation file (`GenerateDocumentationFile`), so all 24 core inflections and their parameters have one. A plugin has them if it ships its XML; [writing-verbs.md](../../writing-verbs.md#in-a-plugin) now says how.
- **Source locations** weren't a large piece of work, so they're in. `SourceLocator` reads the portable PDB beside the assembly, or embedded in it, with `System.Reflection.Metadata` (part of .NET, no new package): the first line of the class's code, then back up the file to the line that declares it, when the file is on this machine. SourceLink in the PDB, which the .NET SDK adds for a GitHub repository, gives `url`, a raw GitHub address pinned to the commit the assembly was built from (Q191). All 24 core inflections have both. Plugins have them if they ship their PDB; the test plugin doesn't, so its entries have no `source`. The cache that holds each assembly's PDB doesn't keep a plugin's assembly from being unloaded.

All of it is a public `VerbCatalogue.Describe(ModuleRegistry)` in `Meep2.Meeplang`, so the daemon could serve it too when the language server moves there for the iPad (prompt 8).

## Tests

Twelve in [LanguageServerGraphTests.cs](../../../tests/Meep2.Tests/LanguageServerGraphTests.cs), run against the server through a new `LspSession` helper that talks to it as an editor does (requests by id, notifications kept in order):

- **The daemon's graph**, byte for byte, for `watchdog.meep`, `merge.meep` and `classify.meep`, with the document's version.
- **A template in an `#include`d file**, with both files open: the graph matches the daemon's; adding a WriteLine to the template in the editor, unsaved, sends a `meep/graphChanged` for the pipeline that includes it, with a new module whose span is in the included file, its `#include` line, and both template uses. The other keys stay.
- **Typing, then a broken line part way through, then mending it**, subscribed: inserting a Where keeps every other key; a line with an unclosed string in the middle gives the last graph (`current: false`, `builtFrom` the last good version), the problem's range, `changed` on that line, and the spans below it moved down a line; mending it gives the same graph and the same version as before it broke. Three notifications, one for each change.
- **A file that never loaded** has `graph: null` and says why; a document that isn't open has no answer.
- **Keys agree with a real daemon** (`DaemonLab`) through typing, saving and reloading: the daemon's history gives Where `m4`; numbering afresh says `m2`; after `meep/setBase` the editor says `m4` (`renamed: m2 → m4`). Typing two new modules, each of the three running modules keeps the daemon's key; after saving, reloading and setting the base again, every key and link key is the daemon's, and `renamed` swaps the two new modules' keys.
- **A base that isn't the text's graph** is refused, saying which module doesn't match.
- **Semantic tokens** on a file with a comment, an `#include`, `defaults`, a constant, a template with parameters and a default, `Fetch.HTTP … as Check`, a parameter line, `is not ErrorMessage`, a string with holes and a format, `@buffer=5`, a template use, a line with an unclosed string part way through and lines after it: 43 tokens checked by text and type.
- **Semantic token deltas**: the one edit, applied to the old data, gives what a fresh request gives, and is shorter; an unknown previous id gets the whole set.
- **The verb catalogue** for the core modules: Fetch.HTTP's parameters, types, defaults, descriptions and emitted types; its source file and that the line given declares the class; Interval's role, empty `takes` and `Tick`; Where as a declared filter that emits its input; Listen.TCP's `when`; every Send inflection an outlet and a passthrough.
- **A plugin's verbs** in the catalogue for a document that loads it (the test plugin, from the cache as `meep run` installed it), and not without the document.

`npm test` (the grammar) and `npm run test:editor` pass; the editor test now also checks that VS Code gets semantic tokens for the verb, the duration and the member.

## What it cost

- **Per keystroke.** The server already loaded the text on every change for its diagnostics. It now loads it as far as `Define` (one step short of what it did before) and then matches the modules and builds the graph. Timed from a separate process sending 300 changes to the Release build of each, twice, load average 9 to 15:

  | File | `main` | This branch | This branch, subscribed |
  |---|---|---|---|
  | `watchdog.meep` (5 modules) | 12.8, 12.0 ms | 12.5, 12.5 ms | 13.0, 12.7 ms |
  | `classify.meep` (7 modules) | 4.3, 3.9 ms | 5.3, 4.9 ms | 6.0, 5.7 ms |

  So about 1 ms more per change at most, and half a millisecond more to send the graph to a subscriber. The matching grows with the square of the number of modules with the same verb, as a reload's does. (Most of watchdog's 12 ms is in loading it at all, before and after.)
- **Memory** per open document: the last good text's loaded modules and graph, the base's if set, and the last semantic tokens.
- **The daemon and runtime** aren't touched, except that `LivePipeline.Shape()` now calls the shared `PipelineShape.Of`, and the daemon lets `meep` see its JSON contract (`InternalsVisibleTo`).
- **The build**: `Meep2.Modules` writes a 140 KB XML documentation file, and a public member without a doc comment there now fails the build (there were none).
- **Code:** about 600 lines changed in ten files, and four new files in `src` (`PipelineDraft`, `Highlighter`, `VerbCatalogue`, `SourceLocator`, 940 lines, about a third comments), plus 610 lines of tests.

## Decisions I made while building

- **The base comes from the client.** The server doesn't connect to the daemon: Synesthesia already has the daemon's graph and its events, possibly from a daemon on another machine, and the server stays a plain stdio process. It costs the client a fetch of the source and one request per `graph` event (Q184).
- **Modules the daemon runs are matched with the daemon's text, not the last keystroke's.** That's what makes their keys the daemon's after a save. The cost is that in a rare case a key could change while typing where chain matching wouldn't change it (two same-verb siblings whose parameters are both being edited); `renamed` reports it if so (Q183).
- **`renamed` belongs to a graph version**, mapping the version before it to it, rather than to a request, so a notification and a request answer can't disagree about it.
- **A stale graph's spans are moved, not rebuilt.** Lines before and after the changed lines are moved by the number of lines added or removed, found by comparing the two texts' first and last lines that are the same. Spans inside the changed lines are left as they were, and `changed` says where those are (Q189).
- **A graph's version counts changes of its JSON**, as the daemon's does, so typing and undoing doesn't count up, and the version only means "different graph".
- **`meep/graphChanged` carries the whole answer** rather than a nudge to ask, so the canvas follows typing with no round trip (Q187).
- **Semantic tokens colour only what the parser knows.** An unknown word is left to the TextMate grammar rather than guessed. Punctuation in a string's holes (the `.` in `{msg.Count}`) stays string-coloured; colouring it would need the holes' punctuation positions, which the syntax tree doesn't keep.
- **Parameter types are Meeplang's words**, not C# type names, since the catalogue is for pipeline writers and Synesthesia's parameter editors; `class` and `source` are there for the code level.
- **An inflection's description is its class's summary**, which in the core modules starts with the verb and inflection ("Fetch's HTTP inflection: …"), and already includes an example in Meeplang.

Noticed in passing, not changed: `ModuleMatcher` matches by structure top to bottom and takes the first same-verb sibling it finds, so a WriteLine typed above another WriteLine takes the existing one's key, though the existing one's text hasn't changed (Q186). A reload does the same, so it isn't new, but the editor shows it as you type.

## Questions

183. Modules the daemon runs are matched with the daemon's text on every keystroke, so their keys are the daemon's after the save; modules being written are matched with the last keystroke's text. The alternative is to match everything with the last text, as the prompt suggested, and correct the keys with `renamed` after each reload. Mine keeps the counts right throughout; the alternative is steadier while typing in rare cases. Is mine the right way round?

     **Answer:** Yes, yours is the right way round. Matching modules the daemon runs against the daemon's text keeps counts on the right tiles throughout, which matters more than steadiness in rare cases.

184. The client sends the base (`meep/setBase` with the daemon's graph and source) after each of the daemon's `graph` events. The server could instead take a pipeline name and follow the local daemon itself, so the client only says which pipeline the file is. That's less for the client to do, but only works for the local daemon, and ties `meep lsp` to the daemon's socket and tokens. Keep it with the client?

     **Answer:** Keep it with the client. Synesthesia already follows daemons, including remote ones later, so `meep lsp` shouldn't need the daemon's socket or tokens.

185. New modules can swap keys when the daemon reloads them, since the daemon numbers new modules in file order and the editor in the order they were typed. The server could number them in file order too, as the daemon will, but then a new module's key changes while you type whenever you add another above it. Or the daemon could take the editor's keys for new modules on a reload (`POST /pipelines/{name}/reload` with them), so nothing is ever renamed. Is `renamed` after a reload enough, or would you like either of those?

     **Answer:** `renamed` after a reload is enough. Modules that weren't running have no counts, so carrying their tiles across is all Synesthesia needs. No change to the daemon's reload.

186. `ModuleMatcher` takes the first same-verb sibling it finds, so typing `WriteLine "c"` above an unchanged `WriteLine "b"` gives c the key (and on a reload, the state) of b, and b a new one. A first pass that pairs modules whose text is unchanged, before matching by similarity, would keep b's key and state. It changes reloads as well as the editor, so it belongs in its own change. Should I make it?

     **Answer:** Yes, make it: a first pass that pairs modules whose text is unchanged. Keep it separate from the rest of this work, with its own section in the report, so it can be reviewed and committed on its own. Do it after applying the other answers, and test reloads keeping state as well as the editor's keys.

187. `meep/graphChanged` sends the whole answer after every change: 2.5 to 7 KB for the examples (3 to 11 modules). Is that fine for Synesthesia, or would you like a smaller notice (version and `current` only) as an option, to ask for the graph when it's ready to draw?

     **Answer:** The whole answer is fine. 2.5–7 KB over a local pipe costs Synesthesia nothing; no smaller notice is needed.

188. A file that has never loaded has no graph. The parser often gets most of a broken file, so the server could give a partial graph: modules, nesting and spans, with no roles, inflections or link types. Worth having for a file opened broken, or is "no graph until it loads" clearer?

     **Answer:** No graph until it loads is clearer. No partial graphs.

189. While the text is broken, spans after the changed lines are moved, and spans inside them are left as they were, with `changed` saying where that is. Enough for pointing at lines and lighting tiles while the text is mended?

     **Answer:** Yes, enough.

190. Parameter types are in Meeplang's words, with `perMessage` for those worked out per message, so Where's condition is `bool` with `perMessage: true`. Do those words suit Synesthesia's parameter editors, or would you like the C# type as well?

     **Answer:** Meeplang's words suit Synesthesia's parameter editors. No C# type needed; `class` and `source` already cover the code level.

191. A source location's `url` is pinned to the commit the assembly was built from, so a local build with uncommitted changes points at a commit without them (the `file` and `line` are right for this machine). Is that fine, or should `url` be left out when the build's commit isn't what's in the working tree?

     **Answer:** Fine as it is. On this Mac, Synesthesia uses `file` and `line`, which are right.

192. Hovers and completions still show only a verb's role and parameter names. Now that descriptions, types and defaults are at hand, should hovers show them too (the inflection's summary, each parameter's type, default and description)? It's small.

     **Answer:** Yes, show them in hovers: the inflection's summary, and each parameter's type, default and description.

## Applying the answers

Done, 16 November 2026, still uncommitted:

- **183, 184, 185, 187, 188, 189, 190, 191:** nothing to change.
- **192:** hovers now show, for each inflection, its summary, then each parameter with its type (and choices), whether it's per message, after the verb or required, its default, and its description, all from the verb catalogue (`VerbCatalogue.Describe` now also takes a list of inflections). The hover test checks Interval's summary and its `period`, `count-start` and `payload` lines. The 16 language server tests pass.
- **186:** done as a change of its own; see the next section.

## Unchanged modules are paired first (Q186)

A change of its own, to review and commit apart from the rest: [ModuleMatcher.cs](../../../src/Meep2.Meeplang/Editing/ModuleMatcher.cs) (one pass, 20 lines), a test in [LiveTests.cs](../../../tests/Meep2.Tests/Meeplang/LiveTests.cs) and a test in [LanguageServerGraphTests.cs](../../../tests/Meep2.Tests/LanguageServerGraphTests.cs). Nothing else in the branch depends on it.

**What changed.** After matching by name, and before matching by structure, a new pass goes top to bottom and pairs each module with an old one that has the same verb, name and parameters, under the module its parent was paired with (or both at the left margin), the nearest in order if there are several. Before, the structure pass took the first same-verb sibling it met, so a module written above an unchanged one took the unchanged one's place, and its state on a reload. A module paired this way is reported as `MatchedBy.Structure`, since it is a structural match with identical parameters, so `MatchedBy` didn't change.

It applies wherever `ModuleMatcher` does: reloads and edits in the daemon, restarts, and `meep lsp`'s keys.

**Tests.** Both fail on the old matcher and pass on the new one:

- **A reload keeps state:** a running `Interval → Tally → Sink` changed to put `Tally step=1000` above the unchanged `Tally`. The new Tally is the one added (`Tally #2`); the unchanged one keeps running (`Tally #3` among the unchanged), so it keeps counting where it was: no number reaches Sink twice, and none from the new Tally.
- **The editor's keys:** typing `WriteLine "c"` above an unchanged `WriteLine "b"` leaves b its key, `m2`, and gives c `m3`. Before, c took `m2`.

The existing matching tests (`LiveTests`, the graph and link-count tests, write-back) all pass unchanged, including the one where an inserted `Where` leaves Tally matched by position, since Tally's parent changed there, so this pass doesn't take it.

**Cost.** One more pass over the modules, comparing each with the old modules of the same verb: the same order of work as the structure pass, at load and change time only.

## The test run

The full suite of 533 (521 before, plus these 12) passed in one run, with the load average about 16, and the build has no warnings. The twelve also passed on their own each time I changed them, and the VS Code extension's grammar and editor tests pass.

After applying the answers, including Q186 (17 November): the build has no warnings, and the full suite of 535 (533, plus the two Q186 tests) passed in one run with the load average about 7. The VS Code extension's grammar test and editor test (diagnostics, completion, hover and semantic tokens in VS Code itself) pass.
