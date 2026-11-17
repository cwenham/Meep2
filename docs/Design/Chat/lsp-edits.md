# `meep lsp`: applying edits, previews, and whether the daemon watches a file

Written 17 November 2026. The fourth of Synesthesia's asks (prompt 4 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`, for Synesthesia's step 5): `meep/applyEdits`, so each gesture on the canvas becomes Meep2's own edits, made by `SourceEditor` and answered with the changes to the text for the editor to apply; previews of what a list of edits would make, for ghost tiles; and whether the daemon is watching a pipeline's file. This covers what was built, what a client has to do, what it cost, the decisions I made while building it, and questions numbered from 193 with answer lines. Nothing is committed; the work is on the branch `lsp-edits`, made from `lsp-graph` at 810c2fc.

## What's built

### `meep/applyEdits`

A request for an open document:

```json
{
  "textDocument": { "uri": "file:///…/site.meep", "version": 4 },
  "edits": [
    { "module": "m3", "kind": "insert", "value": "Where msg.StatusCode < 300" },
    { "module": "m2", "kind": "set", "name": "timeout", "value": "5s" }
  ],
  "preview": false
}
```

- **The edits** are the ones `POST /pipelines/{name}/edits` takes, in the same JSON (`EditItem`), read by the same code (`Reloading.EditOf`): `set`, `unset`, `set-modifier`, `remove-modifier`, `rename`, `add`, `insert`, `remove` and `remove-branch` for a module, and `set-default`, `unset-default`, `set-const`, `unset-const`, `add-plugin` and `remove-plugin` for the rest of the file. `SourceEditor` makes each, on the server's copy of the text, so comments and layout stay as they were.
- **The version** must be the server's latest, from the last `didOpen` or `didChange`. Any other, behind or ahead, is refused with LSP's `ContentModified` (-32801), and `data.version` is the server's.
- **The text must load as it is.** When it doesn't, the request is refused with `RequestFailed` (-32803), saying the first problem and its line (Q193).
- **Nothing changes on the server.** It doesn't write to disk, its copy of the text stays as it was, and no `meep/graphChanged` is sent. The client applies the answer and sends `didChange` as usual.

The answer is an LSP `TextDocumentEdit`, so it can go straight into a `WorkspaceEdit`, with what the canvas needs beside it:

```json
{
  "textDocument": { "uri": "file:///…/site.meep", "version": 4 },
  "edits": [
    { "range": { "start": { "line": 5, "character": 18 }, "end": { "line": 5, "character": 20 } }, "newText": "5" },
    { "range": { "start": { "line": 9, "character": 0 }, "end": { "line": 10, "character": 0 } },
      "newText": "            Where msg.StatusCode < 300\n                WriteLine \"up\"\n" }
  ],
  "added": [ { "edit": 0, "key": "m7" } ]
}
```

- **`edits`** are the `TextEdit`s that change that version of the text into the new one, touching as little as they can (`TextChanges` in `src/Meep2.Meeplang/Editing`). Lines are compared with Myers' algorithm. Where lines replace as many lines, each changed line is a change of its own, cut down to the characters that differ, so a new value replaces just the value. Where lines are added or removed, the change is whole lines, as a diff shows them. Ranges are in the version sent, count from 0, count characters in UTF-16 as LSP does by default, and don't overlap, so the editor applies them together as one step it can undo.
- **`added`** has, for each module an `add` or `insert` writes, which edit wrote it (counting from 0) and the key it will have. The graph the server makes after the client's `didChange` gives it that key (below). A template's line is copied into each use of it, so an `add` into a template gives one entry for each use.

**All or nothing.** The edits are made in order, each on the text the ones before it left, and the list is one change: if any edit can't be made, the request is refused with `RequestFailed` and nothing is changed. `data.edit` says which edit, counting from 0, and the message says why, in words a person can read:

- An edit that can't be made: `Edit 2 of 2 (remove m9) can't be made. There's no module with the key m9 after the edits before it. The keys are m1 (Interval #1), m2 (Check), … Nothing was changed.`
- An edit that leaves text that doesn't parse: `Edit 2 of 2 (set m3's value to msg.StatusCode ==) can't be made. It would leave text Meeplang can't read: … (line 9: Where msg.StatusCode ==)`. The client hasn't seen that text, so each problem quotes its line.
- A list whose text doesn't load at the end, as the daemon's edits refuse (Q194). The edit blamed is the last one after which the text stopped loading: `Edit 2 of 3 (insert Fetch after m3) leaves text that doesn't load, and no edit after it mends it: Fetch needs a value after its name, for its url. (line 10: Fetch)`. The text may stop loading part way through, as when a module that uses a plugin is added before the plugin's line, as long as it loads at the end.
- An edit that isn't one at all (`"kind": "replace"`) is refused with `InvalidParams` (-32602), with `data.edit`.

### Modules named by key

An edit's `module` can be the module's key from `meep/graph` (`m3`), as well as its name, its number, its label (`Where #3`) or a verb only one module has, as `SourceEditor` already took. Keys are what the canvas has, and the only names that are always unique.

- **A key names the module that has it after the edits before it**, keyed as the server would key that text. So in the test, an `insert` at the top renumbers every module below it, and `m3` in the next edit is still the Where it named. (Inside, each key is turned into the module's number in that text, which `SourceEditor` takes.)
- **A key wins over a name.** `m3` names the module with the key m3 if there is one, and a module called `m3` with `as` only if no module has that key (Q196).
- **An edit after one that leaves the text not loading can't name a module by key**, since that text has no keys. The error blames the edit that broke it: `Edit 2 of 3 (insert Fetch after m3) leaves text that doesn't load: … So edit 3 of 3 (name m4 Up) can't find the module with the key m4. Name it by its name or number, or put the edits in an order that keeps the text loading.`

### New modules' keys

The graph after a `didChange` is keyed by `PipelineDraft.Update`, which matches the new text's modules with the last text's (and the base's, after `meep/setBase`). `PipelineDraft.Preview` is new: it keys a text exactly as `Update` would if it were sent now, without changing the draft. The server keys the text the edits make with it, so the keys in the answer are the keys the graph will have once the client sends that text, as long as nothing else changes between (which the version check and the client's own ordering ensure).

**Which module an edit added.** `SourceEditor.Change` is new: it makes one edit and says the line an `add` or `insert` wrote the module on. After each edit, the server loads the text, finds the modules written on that line (one per template use), and follows them through the edits after it with `ModuleMatcher`, as keys are followed. At the end, each one's key is read from the final text's keys. New keys go in the order of the file, so in the test where the first edit adds a WriteLine at the bottom and the second inserts a Where at the top, the answer is `0:m8 1:m7`.

`added` names the module the edit wrote, whatever key matching gives it. That matters in one case: a module inserted above one with the same verb further down. `ModuleMatcher` pairs the new module with the old one, so the new one takes the old one's key (as a reload gives it the old one's state), and the old one gets a new key. Dropping a Where onto the wire above a Where gives `added: [{ "edit": 0, "key": "m3" }]`, and m3's tile shows the new condition while the old Where appears below as m7. The canvas selects the right tile, but the tiles' identities move (Q195).

### Previews

`preview: true` gives the same answer, with the graph the text will have once the client sends it, still without changing anything:

```json
{ "textDocument": { … }, "edits": [ … ], "added": [ … ],
  "graph": { "name": "site", "path": "/…/site.meep", "version": 2, "modules": [ … ], "links": [ … ] },
  "renamed": { … } }
```

- **`graph`** is the `PipelineGraph` `meep/graph` will give after the `didChange`, byte for byte: the same keys, spans counting from 1, and the version it will have (the same if nothing in the graph changes, one more if anything does). A test checks this against `meep/graph` and the `meep/graphChanged` after applying the preview's own `edits`. Modules whose keys are already on the canvas are the same modules; those in `added` are the ghosts.
- **`renamed`** is there when the change would rename keys, as `meep/graph` has it (only with a base).
- To accept a preview, apply its `edits` while the document is still at its version. If the text changed meanwhile, ask again: the old answer's version will be refused.

### Templates and includes

- **A template in the file:** an edit to a module the template made goes to the template's line, so every use of the template changes. In the test, setting the second use's Where (`m4`) changes the template's Where, so `m2` and `m4` both get the new condition; adding under the first use's WriteLine (`m3`) puts the line in the template, so each use gets a WriteLine, `m6` and `m7`, both in `added`. This is what `SourceEditor` does for the daemon's edits too (Q129).
- **A template in an `#include`d file:** an edit to a module it made is refused, as the daemon's edits are, because the edit would change another file: `Where #2 is copied from the template Shout in /…/shared.meep, and edits only change the pipeline's own file so far. Change /…/shared.meep itself.` An edit to the template's use, named by the template's name (`Shout`), changes the values the use gives, on its line in this file, and is made (Q197).

### Merges and moves

Nothing was needed. Synesthesia writes that text itself (its design's Q2), and `meep lsp` checks it as it does any other change.

### Whether the daemon watches a file

`PipelineSummary` and `PipelineDetail` have `watching` (`HostedPipeline.Watching`): whether the daemon reloads the pipeline when its file, or a file it includes, changes. It's true for a pipeline started with `watch`, and false once it has ended, since watching stops then. A pipeline that ends before the daemon starts watching it (one that finishes at once) isn't watched at all; before, the watcher was left running. `GET /pipelines`, `GET /pipelines/{name}`, `POST /pipelines`, the stats stream and `/ws` all carry it, as they carry the records.

### What a client has to do

1. Turn the gesture into edits, naming modules by their keys in the graph.
2. Send `meep/applyEdits` with the document's version as last sent in `didChange`.
3. If the answer is `ContentModified`, the text changed meanwhile: send any pending `didChange`, then make the gesture again against the new graph.
4. If it's `RequestFailed`, nothing changed: show the message. `data.edit` says which edit.
5. Otherwise apply `edits` to the text as one step that can be undone, then send `didChange` with the next version, as for any change. The `meep/graphChanged` that follows has each module in `added` under its key: select it.
6. For a preview, send `preview: true`, draw `graph` with the modules in `added` as ghosts, and on accepting, apply its `edits` as in step 5, as long as the version hasn't changed.
7. Undo is the text's own undo: the `didChange` it sends is matched like any other change.

`initialize` advertises it as `capabilities.experimental.meep.applyEdits`. The VS Code extension doesn't use the custom methods or the daemon's records, so it hasn't changed.

## Tests

Twenty-two in [LanguageServerEditTests.cs](../../../tests/Meep2.Tests/LanguageServerEditTests.cs), run against the server through `LspSession`, each applying the answer's `TextEdit`s itself, as an editor would, and sending the result with `didChange`:

- **Each kind of module edit, named by key** (eleven cases: `set` of a parameter and of the value after the verb, `unset`, `set-modifier`, `remove-modifier`, `rename`, `add`, `insert` twice, `remove`, `remove-branch`), on a file with comments, an end-of-line comment, blank lines, a constant and a named module. Each checks the whole new text (so every comment and blank line is where it was), the keys of the graph after `didChange` (every other module keeps its key), and `added`.
- **Names, numbers and keys across a list:** a WriteLine added at the bottom, a Where inserted at the top (renumbering everything), the old Where changed by key, Check by name and WriteLine "down" by its new number, as one request.
- **A module inserted above one with the same verb** (a Where above a Where, a WriteLine above a Where above a WriteLine): the keys matching gives, and that `added` names the line the edit wrote.
- **A template in the file:** a `set` and an `add` on its copies go to the template, as above.
- **A template in an `#include`d file:** an edit to its copy is refused, saying which file to change; an edit to its use is made.
- **A stale version**, behind and ahead, is refused with `ContentModified` and the server's version; the latest is taken.
- **A list where a later edit fails**, four ways: a key that isn't there, an edit that leaves text that doesn't parse, an edit that leaves text that doesn't load (followed by one naming a key), and a kind of edit that doesn't exist. Each says which edit, and afterwards the server's graph is as it was.
- **Text that doesn't load** takes no edits.
- **Comments and blank lines kept**, with four edits as one request (`set-const`, `set`, `set-modifier`, `add`), on a file with a heading of comments, end-of-line comments, comments above modules and parameters, and runs of blank lines: the whole new text, and the four `TextEdit`s exactly, each a few characters but the one that adds a line.
- **A preview's graph** is the graph `meep/graph` and `meep/graphChanged` give after applying the preview's `edits`, byte for byte and version and all; previewing sent no notification and changed no graph; and the preview's version can't be applied after the change.
- **A preview with a base:** with the daemon's keys `m11` to `m16` from `meep/setBase`, the new module gets `m17`, and the graph after the change is the preview's.

Twenty in [TextChangesTests.cs](../../../tests/Meep2.Tests/Meeplang/TextChangesTests.cs) for the diff: seventeen pairs of texts (empty texts, lines added and removed at the start and end, with and without a last newline, lines swapped, `\r\n` line endings, and a changed emoji, which keeps its surrogate pair whole) and 200 random ones, each applied by a separate implementation in the test; a changed value is just the value; and changes 300 lines apart in a 400-line file are three changes, not one.

One in [DaemonTests.cs](../../../tests/Meep2.Tests/DaemonTests.cs): a pipeline started with `watch` says so in what starting it returns, in the list and in its detail; one started without doesn't; and once a watched pipeline has finished, it isn't watched, even one that finishes so soon it may end before the daemon starts watching it. That last case found a race: the watcher was started after the pipeline was ready, and was left running when the pipeline had already ended. Now a watcher made after the pipeline has ended is let go at once.

## What it cost

- **Per request.** Each edit in a list loads the text it leaves (`PipelineLoader.Define`, as a `didChange` does), so one edit costs about what a keystroke does, and each further edit adds one load. From a separate process sending 200 of each to the Release build, twice, with the load average 10 to 12:

  | File | `didChange` + `meep/graph` | One `set` | One `insert`, preview | Five edits |
  |---|---|---|---|---|
  | `watchdog.meep` (5 modules) | 11.1, 9.6 ms | 10.8, 9.5 ms | 12.0, 11.3 ms | 43.3, 41.0 ms |
  | `classify.meep` (7 modules) | 2.7, 2.6 ms | 2.1, 2.0 ms | 3.9, 3.6 ms | 13.7, 13.3 ms |

  Answers are 235 bytes for one `set`, 600 to 800 bytes for five edits, and 4.5 to 6.4 KB with a preview's graph. Most of watchdog's time is in loading it at all, as the last report found. Loading only where needed would make long lists cheaper (Q200).
- **Memory:** nothing kept between requests. A text whose plugins differ from the document's (after `add-plugin`) loads them under a key of its own, let go when the request is answered.
- **The daemon and runtime** aren't touched, apart from `watching`. `SourceEditor`'s edits are as they were; `Change` is a second way into them that also says where an added module is written.
- **Code:** two new files in `src/Meep2.Meeplang/Editing` (`DraftEditor`, which makes a list of edits to a draft and follows its new modules, and `TextChanges`, the diff), 460 lines, a fifth of them comments; 250 lines changed in five files (`LanguageServer`, `PipelineDraft`, `SourceEditor`, the contracts and `HostedPipeline`); and 600 lines of tests.

## Decisions I made while building

- **The planning is in `Meep2.Meeplang`**, as `DraftEditor` and `TextChanges`, with only the LSP's JSON in `LanguageServer`, so the daemon can serve `meep/applyEdits` when the language server moves there for the iPad (prompt 8).
- **The answer is a `TextDocumentEdit`** (`{ textDocument, edits }`) with `added` and the preview beside it, so a client that speaks LSP can apply it as it applies any workspace edit. `edits` in the answer are LSP's `TextEdit`s; `edits` in the request are Meep's.
- **The keys promised are the keys `Update` will give**, worked out by keying the new text exactly as a `didChange` will, not by keeping the answer's keys and handing them over when the text arrives. So nothing is kept between requests, a preview that's never accepted costs nothing, and a client that applies the text some other way gets the same keys if the text is the same.
- **A module the edit added is found by the line it's written on**, not by which key is new, so `added` is right when matching gives the new module an old key.
- **Each step of a list is keyed as though it were the text sent**, so a key always means what it would mean if the edits before it had been sent on their own.
- **A list is refused if its text doesn't load at the end**, as the daemon's edits are; part way through it may (Q194).
- **Errors quote the line of each problem**, since the problem is in a text the client hasn't seen.
- **`TextEdit`s are cut to characters only where lines replace as many lines.** Elsewhere they're whole lines, so an insert that indents the lines under it is one readable change rather than a few characters spread across lines. Cutting to characters can give odd-looking changes when `SourceEditor` keeps an end-of-line comment in its column: `timeout = 10s   // …` to `timeout = 1m    // …` is `0s` replaced by `m `. Both are the smallest change and apply the same.
- **The text must load before edits are made** (Q193): its keys, and the numbers edits use, are those of the text as it is.

Noticed in passing, not changed:

- `ModuleMatcher` gives a module inserted above one with the same verb the old one's key and state, even when the old one is further down the chain (Q195).
- `remove` and `remove-branch` leave the comment written right above the module (Q201).
- `set` with no parameter name replaces the value's expression, inside any brackets around it, so `Where (a > b)` set to `c` is `Where (c)`. It loads the same; `ModuleSyntax.PositionalSpan` would make it exact. (From the last session's notes.)

## Questions

193. Edits are refused while the text doesn't load, since its keys and numbers are the last text's that did. Edits that don't name a module (defaults, constants, plugins), and ones naming a module by name, could still be made on the broken text, though their result wouldn't load either until it's mended. Is refusing everything until it loads right?

     **Answer:** Yes, keep refusing every edit while the text doesn't load. Synesthesia will tell the person to mend the text first.

194. A list whose text doesn't load at the end is refused, so a verb dropped on the canvas has to come with its required values (Synesthesia has them from `meep/verbs`: `required`, `type`, `default`), or a placeholder. The alternative is to take it, with the problems in the answer, so dropping a bare `Fetch` puts it in the text and the text shows the error until the url is typed. But then the new module has no key until the text loads, so the canvas can't select it. Keep refusing?

     **Answer:** Keep refusing a list whose text doesn't load at the end. Synesthesia will ask for a verb's required values, from `meep/verbs`, before inserting it.

195. `ModuleMatcher` pairs a module inserted above one with the same verb with that one: a Where dropped above a Where, or a WriteLine dropped above a Where whose WriteLine is under it. The new module takes the old one's key, and on a reload its state, and the old one gets a new key. A pass after Q186's, pairing a module whose verb, name and parameters are unchanged with the old one wherever it is now (not only under the module its parent was paired with), before matching by structure, would keep modules that an insert pushed down a level. It changes reloads as Q186 did. Make it, as a change of its own, as Q186 was?

     **Answer:** Yes, make it: the pass that pairs a module whose verb, name and parameters are unchanged with the old one wherever it now is. Otherwise a tile, and on a reload a module's state, moves to the wrong module. Keep it a change of its own, as Q186 was: its own section in the report, separable from the rest. Test that a reload keeps state, and that the editor keys modules, as for Q186. Do it after the other answers.

196. A key wins over a name: `m3` names the module with the key m3, and a module called `m3` with `as` only when no module has that key. Fine, or would you rather keys came in a field of their own (`"key": "m3"`)?

     **Answer:** Fine as it is: a key wins over a name. It keeps the edit format the same as the daemon's.

197. An edit to a module made by a template in an `#include`d file is refused, as the daemon's are. `meep lsp` could instead answer with a `WorkspaceEdit` changing both documents, with the included one at its open version, since an editor that has both open could apply both as one step. Worth doing, now or later?

     **Answer:** Later. Synesthesia doesn't open included files yet. Note it in the report as a possible addition.

198. A later edit in a list can name a module an earlier one adds only by a name written in its line (`as Gate`), since its key isn't known until the answer. Claude's proposals that build a branch in several steps would need that name in the text. Should an edit be able to name "the module edit N added", say `"module": "+0"`?

     **Answer:** Yes: let an edit name the module an earlier edit in the same list added, as `"module": "+0"` (counting from 0, as `added` does). Claude's proposals that build a branch in several steps need it. Where an earlier edit added more than one module (a template's line, copied into each use), say what `+N` means, or refuse it, and test it.

199. The daemon's `POST /pipelines/{name}/edits` doesn't take keys. It has them, so it could, the same way. Synesthesia edits through `meep lsp`, so it doesn't need it, but a client editing a running pipeline directly would. Add it?

     **Answer:** Yes: let the daemon's `POST /pipelines/{name}/edits` take keys, the same way, since it has them. Synesthesia will edit other hosts' pipelines through the daemon in its step 7. Test it.

200. Each edit in a list loads the text it leaves, to find keys and follow new modules: five edits cost five loads (41 ms for `watchdog.meep`, 13 ms for `classify.meep`). Loading only before an edit that names a key, and at the end, would make long lists cheaper, but follows new modules less exactly. Leave it as it is?

     **Answer:** Leave it as it is: 13 to 41 ms for five edits is fine for a gesture.

201. `remove` and `remove-branch` leave a comment written right above the module (in the test, `// Only good answers.` stays after its Where goes). `SourceEditor` already treats a comment right above a module as describing it, when placing settings. Should removing a module take such a comment with it? It would change the daemon's edits too.

     **Answer:** Yes for a comment written directly above the module, with no blank line between: `remove` and `remove-branch` take it with the module, in the daemon's edits too. A comment with a blank line after it stays. Test both cases.

## The test run

The build has no warnings. The full suite of 578 (535 before, plus these 43) passed in one run, with the load average about 7. The 22 `meep/applyEdits` tests also passed on their own each time I changed them.

## Applying the answers

Done, 17 November 2026, still uncommitted:

- **193, 194, 196, 200:** nothing to change.
- **197:** left for later. A possible addition: answer an edit to a module made by a template in an open `#include`d file with a `WorkspaceEdit` that changes both documents, the included one at its open version, for the editor to apply as one step.
- **198:** an edit can name the module an earlier edit in the same list added as `"module": "+N"`, counting from 0 as `added` does, in `meep/applyEdits` and the daemon's edits alike (`DraftEditor`). `+N` names the line edit N wrote. When that line is in a template, each use has a copy of it, and an edit to any copy changes the line, so `+N` means the line, and every use changes, as an edit to either copy by key would. It's refused, saying why, when edit N comes at or after the edit naming it, isn't an `add` or `insert`, or wrote a module that a later edit took out. Three tests: a branch built in four steps (a Where inserted, a WriteLine added under `+0`, `@buffer` on `+1`, the Where's condition changed through `+0`), with `added` and the keys after; the three refusals; and `+0` after an `add` into a template used twice, which changes the template's new line, so both uses' copies.
- **199:** `POST /pipelines/{name}/edits` takes keys and `+N`, the same way. When an edit names a module either way, the daemon makes the list with `DraftEditor`, against a draft whose base is the running pipeline's text and keys, so each key names the module the daemon's graph gives it, after the edits before it. Texts whose `#plugin` lines differ from the running one's have their plugins installed for the purpose, and let go after. Lists that name modules only by name or number go through `SourceEditor` as before, so their cost and messages haven't changed. A test in [GraphTests.cs](../../../tests/Meep2.Tests/GraphTests.cs): after a reload that leaves WriteLine as `m2` and a Where as `m3` (not the file's order), a list inserts a Where after `m3`, sets `m2`'s value (now module 4), sets `+0`'s condition and puts `@buffer` on `m3`; the text and the graph's keys after are checked, and a key that isn't there is refused with a 400 listing the keys.
- **201:** `remove` and `remove-branch` take a comment written right above the module, with no blank line between: the whole block of comment lines, if there are several. A comment with a blank line after it stays. It's in `SourceEditor`, so the daemon's edits do it too. A test in [EditorTests.cs](../../../tests/Meep2.Tests/Meeplang/EditorTests.cs) removes a Where, and its branch, with a comment right above (it goes), a Where whose comment has a blank line after it (it stays), and the first module, whose two comment lines go while the file's heading, above a blank line, stays. The `remove` and `remove-branch` cases of the `meep/applyEdits` test now expect `// Only good answers.` to go with its Where.
- **195:** done as a change of its own, in the commit after this one, with its own section here.

## After applying the answers

The build has no warnings. The full suite of 583 (578, plus five: three for `+N`, one for the daemon's keys, and one for removing comments) passed in one run. The `meep/applyEdits` tests are now 25. The VS Code extension uses none of this, so it hasn't changed.

