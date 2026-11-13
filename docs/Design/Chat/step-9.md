# Step 9: the rest

Written 10 November 2026. Round 3's order of work ends with "the rest": loading pipelines from URLs, Git, IPC and stdin; templates and `#include`; Distribute, Zip and Batch; and the VSCode extension. This covers what step 9 built, the decisions I made while building it, benchmark numbers, and questions numbered from 128 with answer lines.

## What's built

### Templates and `#include`

```
// shared/transit.meep
const Api = "https://api.busline.example/v1"
template Departures(Stop, Limit = 10)
    Fetch "{Api}/schedule/{Stop}"
        Extract "$.departures[*]"
            Where msg.minutesAway < Limit

// nextbus.meep
#include "shared/transit.meep"
Interval 1m
    Departures "12" limit=30 as Board
        WriteLine "{msg.route} in {msg.minutesAway} min"
```

- **A template** is `template Name(Parameter, Parameter = default)` at the margin, with its modules indented under it. A module line naming it is replaced by a copy of those modules before the pipeline is built (round 2), with the line's values in place of the parameters: the first as the value after the name, the rest by name.
- **The modules under a use** go under the template's last module, so a template reads like any other module in a chain.
- **`as Name` on a use** names the template's first module. Names inside a template are kept (Q128).
- **Values can use `msg`**, since they're copied in as written, and templates can use other templates. A template that uses itself, a missing or unknown parameter, a template named like a verb, and one with nothing indented under it are each reported.
- **`#include "file.meep"`** brings in another file's templates and constants, relative to the including file. Includes can include, once each; a loop is reported. An included file holds only templates and constants.
- **A mistake inside an included template is reported where it's written**, in that file, at its line. To do this, spans now carry their file when it isn't the pipeline's own.
- **Live changes understand templates:** they're matched and compared on the copied-in modules, so changing a use's value changes the copy's module. `--watch` also follows the files a pipeline includes.

### Batch, Zip and Distribute

```
Interval 10ms
    Batch 100 within=1s
        WriteLine "{msg.Count} readings"

+ Listen "http://0.0.0.0:8080/orders"
+ Listen "http://0.0.0.0:8080/payments"
Zip by msg.OrderId
    Respond "paired {msg.Left.OrderId}"

+ Listen "http://0.0.0.0:8080/quote"
    Distribute 4 by msg.CustomerId
        PriceLookup
Respond "{msg.Price}"
```

- **Batch** gathers so many messages, or whatever has come within a while of the first, into a `Batched` message whose items are its parents (round 3, section 5). A part-filled batch goes out when the pipeline has nothing else left to do.
- **Zip** pairs a message from its first input with one from its second, in order or by a key, into a `Zipped` message with `Left` and `Right`. When the pipeline has nothing else left to do, each message still waiting becomes an error saying nothing came to pair with it.
- **Distribute** spreads messages across copies of the branch under it, so a slow step runs side by side. Messages with the same key always take the same copy, so they keep their order (Q53). The loader makes the copies, which share their originals' names.
- **What the runtime does for them:**
  - **Keeping messages:** `context.Keep(message)` holds a message, with its leases open, after its handling has ended. It's let go when the module emits a message made from it, or calls `LetGo`. A batch of queue messages is therefore acknowledged only once the batch has finished, as at-least-once delivery needs.
  - **Flushing:** when nothing is left in flight, modules that keep messages are asked to flush them (`FlushAsync`) before the pipeline halts. Anything still kept after that is let go. `context.FlushAfter(delay)` asks for a flush later, as `Batch within=` does.
  - **Inputs:** `context.Input` says which input a message came in on, numbered in the order the modules feeding it are written. Each link carries its target's input number from when the pipeline is wired, so delivery doesn't look anything up.
  - **Routing:** a module that implements `IRouteMessages` chooses which copy of its branch each message takes. The choice is made on the thread delivering the message, so Distribute never holds messages up.

### Loading pipelines from anywhere (goal 7)

```
meep run ticker.meep                                         # a file
cat ticker.meep | meep run -                                 # standard input
meep run https://example.com/pipes/ticker.meep               # a URL, with ticker.meep.sig beside it
meep run https://example.com/pipes/ticker.meep#sha256=3f9a…  # a URL, pinned
meep run git+https://github.com/you/pipes.git#ticker.meep@main                 # Git, signed
meep run git+https://github.com/you/pipes.git#ticker.meep@4c1f…(40 hex)        # Git, pinned to a commit
meep start git+https://github.com/you/pipes.git#ticker.meep@main
cat ticker.meep | meep start - --name ticker                 # sent over the daemon's socket
meep sign ticker.meep                                        # writes ticker.meep.sig
```

- **Files and stdin are yours**, so they run as they are.
- **A pipeline from a URL or Git needs a signature from a key you trust,** made with `meep sign ticker.meep`. It's an SSH signature in the namespace `meep-pipeline`, beside the file as `ticker.meep.sig` (Q36). Alternatively the address can pin it: `#sha256=` on a URL, or a commit's full SHA as Git's ref, since a commit's SHA covers every file in it, as a GitHub plugin's pin does (Q102).
- **Includes come from the same place,** under the same rule: from the same commit in Git, and beside the file for a URL. Each included file needs its own signature unless the Git commit is pinned.
- **Git** is fetched over HTTPS (Q66) into Meep's cache, then read at one commit, without a checkout. `git+ssh://` and `git+git@…` addresses are refused, with a note to use the repository's https:// address. A repository on this machine works too, which is how the tests use it.
- **The daemon** reads URLs and Git itself. `meep start -` sends standard input as text over the socket, named with `--name`. A pipeline from anywhere but a file can't be watched, and edits to it can't be saved. One from stdin or text can't be reloaded, and each case says why.

### The VSCode extension and `meep lsp`

- **`meep lsp`** is a Language Server Protocol server over standard input and output, built into the meep command.
  - It checks a file as it's edited with Meeplang's own parser and loader, so an editor shows the same mistakes, in the same words, that `meep run` would. Mistakes in included files are shown in those files.
  - It completes verbs and keywords, modifiers after `@`, message types after `is`, and settings under `defaults`.
  - Hovering over a verb shows its inflections and their parameters.
  - A file with `#plugin` lines is only parsed, since plugins aren't installed while editing (Q138).
- **`editors/vscode`** is the extension: a TextMate grammar for colouring, the language's comment and bracket settings, and a client that starts `meep lsp`. `meeplang.meepPath` says where meep is if it isn't on your PATH. It isn't published (Q137). Two tests check it:
  - **`npm test`** colours a sample pipeline with VS Code's own TextMate engine and checks 51 parts of it. That found two things the first grammar missed: verbs on lines that start with `+`, and raw strings in single quotes.
  - **`npm run test:editor`** runs the extension in the VS Code installed here, with a profile of its own, so your settings and extensions aren't touched. It checks that a misspelt verb is shown where it is and cleared once fixed, that verbs are offered as you type, and that hovering describes one.

**Tests:** 444 TUnit tests, 34 of them new, and the extension's two tests. They include real Git repositories, a test HTTP server for URLs, the meep program reading stdin, and a conversation with `meep lsp`. The suite passed 8 runs in a row.

## Decisions I made while building

1. **Templates are copied in before loading,** as round 2 settled, so everything after (binding, type checks, wiring, live changes) sees ordinary modules. A copy remembers the use it came from.
2. **A template's parameters are substituted in its syntax:** a name in an expression becomes the use's expression, so values can use `msg`. `fam.Name` isn't touched, since it names a module.
3. **What goes under a use goes under the template's single last module.** A template that branches into several ends can't have anything under its use, since there'd be no telling which end it follows.
4. **Spans carry their file** only for included files, so the pipeline's own diagnostics and spans are as they were.
5. **Matching and fingerprints now print expressions from the syntax** rather than reading text through spans, since a template's copy has values that aren't written where its spans point.
6. **Kept messages don't count as in flight,** so the runtime can tell when only kept messages are left. It then flushes once, and halts if that doesn't help. That stops a Zip still waiting for a partner from holding the pipeline open forever.
7. **Distribute's copies are made by the loader** as copies of the syntax, so each copy is an ordinary module with its own loop and state. Its key is `by=`, a named parameter, since Meeplang's parameters are `name=value` (Q130).
8. **Zip's key is the same expression on both sides,** and keys are compared as text, so 42 from JSON and 42 from a record match.
9. **Pipeline signatures reuse plugin signing:** the same SSH formats, `meep keys trust` and allowed-signers file, with a namespace of their own so a pipeline's signature can't pass for a plugin's.
10. **Developer mode doesn't relax pipelines from URLs or Git:** it's for unsigned plugins on this machine (Q133).
11. **The language server lives in the meep command,** so the extension needs nothing but meep, and always checks with the same code that runs pipelines.
12. **New types:**
    - SDK: `context.Input`, `Keep`, `LetGo` and `FlushAfter`, `AInflection.FlushAsync`, and `IRouteMessages`.
    - Core modules: `Batch`, `Zip`, `Distribute`, `Batched` and `Zipped`.
    - Meeplang: `TemplateBlock`, `IncludeDirective`, `IIncludeReader`, `PipelineText`, `ExpressionText` and `MeeplangSettings`.
    - Plugins: `PipelineSignature`.
    - Git: `GitSnapshot`.
    - Daemon: `PipelineAddress`, `PipelineSources` and `ReadPipeline`.

## Benchmark numbers

Measured with BenchmarkDotNet on 10 November. The chain benchmarks used its medium run, on step 8's commit and on step 9 one after the other. This Mac ran faster today than when step 8 was measured, so compare within this table rather than with step 8's.

| Chain, per message | Step 8 | Step 9 |
|---|---:|---:|
| 1 step, passing messages on | 0.79 µs, 99 B | 0.82 µs, 109 B |
| 1 step, a new message each step | 0.99 µs, 193 B | 1.03 µs, 215 B |
| 4 steps, passing messages on | 1.04 µs, 105 B | 1.02 µs, 121 B |
| 4 steps, a new message each step | 1.55 µs, 459 B | 1.62 µs, 479 B |

- **Times are the same, within the runs' noise.** The last row's step 9 run varied by ±0.11 µs.
- **Allocation rose by 10–22 bytes per message.** I checked where it goes by counting the chain's allocations by type on both commits.
  - The messages cost the same on both. The rise was all in the modules' buffers: `Message[]` arrays on step 8, `Arrival[]` on step 9.
  - Each place in a buffer now holds the input a message came in on as well as the message, so it takes 16 bytes instead of 8 (see Inputs, above).
  - The chain benchmark sizes every buffer to hold all 100,000 messages, so its buffers keep growing as it runs, and the growth shows up as bytes per message.
  - In a running pipeline, a buffer grows to its size once (1,024 messages by default) and is then reused. So it allocates nothing more per message, and takes 16 KB per module where it took 8 KB (Q140).

The combining modules, per message from the sources (BenchmarkDotNet's short run, so a rougher guide):

| Benchmark | Time | Allocated |
|---|---:|---:|
| Batch, 100 at a time | 0.52 µs | 133 B |
| Zip, pairing two sources in order | 0.98 µs | 240 B |
| Distribute across 4 copies of a step that makes a new message | 0.81 µs | 204 B |

Distribute's pipeline has one more module in the way than the chain's "1 step, a new message each step", but runs faster than it (0.81 against 1.03 µs), since four copies share the work.

## Questions

128. Names inside a template are kept, so a template whose modules have `as` names can be used once per pipeline; a second use is reported as a duplicate name. Should they take the use's name as a prefix instead, such as `Board_Schedule`, so a template can be used several times and `fam` and `Tap` still find each copy?

     **Answer:** Yes.

129. With templates, the running pipeline numbers its copied-in modules, while `meep edit` numbers the lines in the file, so `Where #5` can mean different modules to each. Names and verbs mean the same to both. Should edits use the running pipeline's numbers, editing a template's definition when a module copied from it is named?

     **Answer:** Yes.

130. Distribute's key is written `by=msg.CustomerId`, a named parameter, where round 3 wrote `by msg.CustomerId`. Is that all right, or should `by` be a keyword, as `as` is?

     **Answer:** As a keyword, without the =.

131. A Zip keeps every message waiting for a partner, so in a long-running pipeline where one side is much busier, the waiting messages grow without limit. Should Zip have a limit on how many wait, or a time after which a waiting message becomes an error?

     **Answer:** Yes, that should be a parameter plus a default. If there are equivalent defaults used elsewhere, then use those. Otherwise let's see what testing reveals.

132. Batch and Zip keep messages, and with them their ancestry, as round 3 noted. There are no limits yet; as agreed in Q7, I'd measure before adding any. Is that still right?

     **Answer:** Yes.

133. Pipelines from URLs or Git need a signature or a pin, and `--dev` doesn't change that. Should developer mode let an unsigned pipeline from a URL on this machine (localhost) run, for trying out a pipeline you're serving yourself?

     **Answer:** Yes.

134. Git commits can carry SSH signatures, and write-back's now do (Q126). Should a pipeline from Git be trusted if its commit is signed by a key you trust, without a `.sig` file beside it?

     **Answer:** Yes.

135. `http://` URLs are allowed, still needing a signature or pin. Should plain HTTP be refused, except for localhost?

     **Answer:** Yes.

136. A pin on a URL covers only that one file, so its includes each need a signature. Should there be a way to pin a pipeline and its includes together, such as pinning a list of files and their SHAs?

     **Answer:** Yes.

137. The VSCode extension isn't published: it's built with `npx @vscode/vsce package` and installed from the .vsix. Should it go on the Marketplace, and under which publisher name? I've used `cwenham` in its manifest.

     **Answer:** Not yet.

138. `meep lsp` only parses a file with `#plugin` lines. Should it load plugins that are already in the cache, without downloading anything, so their verbs are checked as you type too?

     **Answer:** Yes.

139. `pipeline` blocks, several pipelines in one file, are still reported as not supported yet. Templates cover reuse within a pipeline. Do you still want them?

     **Answer:** No.

140. Each place in a module's buffer now holds the input a message came in on, so buffers take twice the memory: 16 KB per module at the default size of 1,024. Only modules with more than one input need it. Is that all right, or should modules with one input keep buffers of plain messages, at the cost of two kinds of buffer in the runtime?

     **Answer:** That's alright.

141. Step 9 ends round 3's order of work. What would you like next? Some candidates:
     - the secret store for `secret()`
     - permissions for pipelines (Q40)
     - more core modules, such as Template, Merge, Route, Throttle, Retry, Cache, Count and Exec
     - mutual TLS for the daemon
     - the source generator for AOT (Q115)
     - packaging the SDK (goal 9)

     **Answer:** I'll save those for other sessions in the future.

## After your answers

Applied on 10 November 2026. 451 tests pass, 7 of them new, and the suite passed 10 runs in a row. The extension's two tests pass, the second in VS Code again.

Twelve runs before those turned up two failures in the tests themselves. A test read the output a pipeline was still writing from another thread, which .NET 11's `StringBuilder` now catches. The tests now write to `SharedText`, which takes the same lock to read. One more failure, in a GitHub plugin test, left no details and didn't come back in 22 runs.

- **Q128, template names:** a named use now puts its name before the names inside the template, so a template can be used more than once:

  ```
  template Counted(Text)
      Where msg.Count > 0
          Batch 10 as Ten
              WriteLine "{Text}: {msg.Count}"
  Interval 100ms
      Counted "a" as A     // its modules run as A, A_Ten and WriteLine
      Counted "b" as B     // and as B, B_Ten and WriteLine
  ```

  - `as A` names the template's first module A, as before, and the other named modules become A_Name. References to them inside the template, by `fam.Ten` or as a parameter, are renamed to match.
  - The first module can now have a name of its own. The use's name takes its place.
  - A use inside a template takes the prefix of the template's use too, so names nest: Board_X_Ten.
  - A use without a name keeps the names as written, so it can be used once. A second use says: "There's already a module called Ten. It's named inside the template Counted: give each use of it a name, as in Counted as First, and the names inside start with that name (First_Ten)."
- **Q129, edits number modules as the running pipeline does:** `meep edit` finds a module by its name, label or number as the running pipeline gives them, after templates are copied in and Distribute's branches are copied.
  - An edit to a module copied from a template changes the template's definition, so every use changes. An edit to one of Distribute's copies changes the branch they were copied from.
  - Renaming a module whose name its use gave renames the use: `Counted "a" as A` becomes `as First`. A prefixed name such as A_Ten is made from two lines, so an edit can't rename it, and says to rename it in the template or rename the use.
  - A use's own values can still be edited by naming its template, when the template is used once. When it's used more than once, the edit lists the lines that use it and says to change the file.
  - A module copied from a template in an included file can't be edited yet, since edits only change the pipeline's own file. The edit says which file to change.
- **Q130, `by`:** it's a keyword now: `Zip by msg.OrderId`, `Distribute 4 by msg.CustomerId`.
  - `by=` is refused with "Write by without =, as in by msg.Key.", and so is a `by =` parameter line.
  - An edit that sets `by` writes it the same way, and VS Code colours it as a keyword.
- **Q131, Zip's limits:** Zip takes `limit=` and `within=`, with defaults that already exist elsewhere:
  - **`limit`** is how many messages can wait for a partner at once. When one more comes, the one that has waited longest becomes an error. The default is the pipeline's buffer size: 1,024 unless `buffer` under `defaults` says otherwise. So the setting that limits how many messages a module holds limits Zip too. Modules can read it as `context.BufferSize`.
  - **`within`** is how long a message waits before it becomes an error. It has no default, but a message's own deadline counts (`@expires`, or `expires` under `defaults`). So Zip honours the expiry used everywhere else, whichever comes first.
  - The errors say which it was: "Nothing came on Zip's input 2 to pair with this message within 50ms.", "… before it expired." and "… before 2 messages were waiting, Zip's limit."
  - So that Zip can tell a timed flush it asked for from the last flush, a module can now read `context.Finishing` while flushing.
  - Testing: a limit of 2 gave up on the oldest messages in turn, and the timed and expiring messages gave up on time. The test ran ten times in a row.
  - Cost: run side by side with the Zip from before, on BenchmarkDotNet's short run, pairing in order takes 14% longer per message (1.76 µs against 1.54) and 28 bytes more (272 against 244). That's the record kept for each waiting message, so it can give up by age, time or deadline.
- **Q132:** kept: no limits on kept ancestry until they're measured.
- **Q133, developer mode on this machine:** `--dev` runs an unsigned pipeline from a URL on this machine. Without it, the error says it would: "As it's on this machine, --dev would run it unsigned." A signature, if there is one, still has to be right.
- **Q134, signed commits:** a pipeline from Git is trusted when its commit is signed with SSH by a key you trust, and so is everything it includes from that commit, with no `.sig` files.
  - Meep checks the commit's signature itself, as it checks `.sig` files.
  - GPG signatures aren't checked, so a commit signed that way still needs `.sig` files.
  - A commit signed by a key you don't trust gets an error naming its key: "Its commit is signed with SHA256:…, which isn't a key you trust."
- **Q135, plain HTTP:** refused, except on this machine. The error: "Meep fetches pipelines over HTTPS, so it won't fetch http://example.com/ticker.meep: http:// is only for this machine (localhost). Use https://." The same goes for includes. Git was already HTTPS only.
- **Q136, pinning a pipeline with its includes:** rather than a separate list of files and SHAs, each `#include` line can pin its file, as a `#plugin` line does:

  ```
  #include "shared/transit.meep" sha256:3f9a…
  ```

  - A pinned include needs no signature of its own: the file that includes it is trusted, and pins it. So a pipeline pinned by `#sha256=` on its URL pins everything it includes, however deep, with that one pin.
  - A new command, `meep pin nextbus.meep`, writes the pins, the deepest files' first, and prints the pipeline's own SHA-256 for its URL. Run it again after changing an included file.
  - A pin that doesn't match is an error, on this machine too: "Couldn't include shared.meep: it has the SHA-256 …, not the … the #include line pins, so it has changed."
- **Q137:** not published. The manifest keeps `cwenham` as its publisher.
- **Q138, plugins in the editor:** `meep lsp` now loads the plugins a file names from Meep's cache, so their verbs, parameters and message types are checked, offered and described as you type.
  - Nothing is downloaded: a plugin that isn't in the cache is reported on its `#plugin` line, saying to run the pipeline once to install it.
  - Only plugins `meep run` would trust without `--dev` are loaded, so opening a file in an editor can't load code you haven't trusted. An unsigned plugin on this machine gets the error `meep run` would give.
  - A file's plugins stay loaded while its `#plugin` lines are unchanged, and are let go when it closes. Lock files aren't written while editing.
  - `PluginOptions.Offline` installs only from the cache, for anything else that shouldn't download.
- **Q139:** `pipeline` isn't a keyword any more. A line that starts with it is read as a module, as any other line is, and VS Code no longer colours it.
- **Q140:** kept.
- **Q141:** for later sessions.

**Tried in VS Code:** the extension's test, again, with the grammar's `by` and the language server's plugins.

**Left for later:** edits to templates in included files (Q129), which need write-back to save more than one file.
