# Refactoring, phase 4: big classes and long functions

Written 22 November 2026. Phase 4 of the plan in `refactoring.md`: section 7's classes to extract, largest first, breaking up each class's long functions as they moved (section 6), then the smaller ones, the dispatchers and the other functions over 40 lines. The work is on the branch `refactor-classes`, made from `main` at ea815b9, with a commit for each extraction or small group of related functions, not merged or pushed. This covers what was extracted and broken up, how many long functions are left, what the benchmarks say, what I skipped and why, what I found in passing, the decisions I made while building, and questions numbered from Q244.

## Results

- 25 commits: 23 for extractions and long functions, one tidying commit, and this document.
- The build has no warnings, with warnings as errors.
- The whole suite passes: 694 of 694, the same tests as on `main`, since these are moves and need no new ones.
- Every commit was built and tested on its own, in a scratch worktree, in order: all 24 build, and all 24 pass 694 of 694. None of the known flaky tests failed in any of those runs, nor in the dozen full runs I made while working.
- Every verb's inflections come in the same order as on `main`, checked against the built `Meep2.Modules.dll`. No inflection type or file moved; the new types in Modules (`TreeStore`, and records nested in Listen.HTTP and Send.HTTP) are internal or private, so `GetExportedTypes` doesn't return them.
- No function I wrote or moved takes more than five parameters, not counting a cancellation token. Four on `main` still take six (`SourceEditor.Set`, `Pipeline.EmitLeased`, Fetch's and Send's `SendAsync`), none of them in a class taken apart here.

## What was extracted

The commits are in the plan's order. Each new class has a file of its own (with its record, where it has one), and each extracted function can be used by another caller as it is: none takes more than five parameters, and where one returns several things, it returns a record rather than a tuple.

| Commit | Class | Became |
|---|---|---|
| 690364d | `Subscription` | `SubscriptionClock`, `SentState`, `PipelineRead`, `ModuleReads`, `Update`, `HostPart`, `PipelinePart`, `Hello` |
| ecf198d | the registry lookup in `meep lsp` and the daemon | `PluginRegistries` |
| d021163 | `MeepDaemon` | `ControlSocket`, `DaemonAccess`, `DaemonApi`, `PipelineBootstrap` |
| b85c647 | `Load` | `VerbResolver`, `OptionsBuilder`, `Wiring`, `TypeFlow` |
| 5afe85f | `LanguageServer` | `JsonRpcChannel`, `LspWorkspace`, `LspDocument`, `LspCompletion`, `LspHover`, `LspGraphs`, `LspEdits`, `LspTokens` |
| 2220103 | `Node` | `NodeCounters`, `TapList`, `PeerCounts` |
| 1faa587 | `Telemetry` | `Tracing`, `Metrics` |
| 3952d55 | `BayesTree` | `TreeStore` |
| 7e40071 | `PublicKey` | `AllowedSignersLine` |
| 7cf4998 | `HostIdentity` | `MachineAddresses`, `DaemonClock` |
| d0a654d | `DaemonCommands` | `EditCommand` |
| d547bb9 | the CLI's dispatchers | `CommandContext`, `FileRun` |
| 2bb3b41 | `PluginInstaller.InstallNuGetAsync` | `NuGetResolver` |
| 3fb318b | `ModuleRegistry.AddPlugin` | a method for each step |
| 63bf66b | `DraftEditor.ApplyAsync` | `DraftEditRun` |
| aed4628 | `SourceEditor.ApplyToFile` | a method for each edit |
| f67f289 | `ModuleMatcher.Match` | `ModuleMatching`, a method for each pass |
| 0dac154 | `MessageJson.WriteValue`, `ExpressionText.Write` | a method for each kind of value |
| b6abdc8 | `DaemonConfig.Load` | `DaemonConfigReader` |
| 8c43369 | `KeysCommands.SignAsync`, `Detaching.StartAsync` | a method for each step |
| 57cb4df | Listen.WebSocket, Listen.HTTP, Fetch.HTTP, Send.HTTP | a method for each step |
| d5a414e | `LineParser.ParseDirective`, `ParseModuleLine` | `ParsePlugin`, and a method for each part of a module line |
| e0bfd37 | `Pipeline.ApplyCoreAsync` | `ChangePlan` |
| 000b1e5 | tidying | `DraftEditRun` takes five parameters, not six; usings the moves left unused |

### The big classes

**`Subscription`** keeps what the client asked for, its replies, and `RunAsync`, which is now a short loop of named steps.

- `SubscriptionClock` has the timers on one grid, the heartbeat and the budget, with `Due`, `HeartbeatDue`, `HeldAt`, `Sent` and `WaitAsync`.
- `SentState` has what was last sent, and finds what changed in the list, the host and the runtime, and for each pipeline. `PipelineSent` is what it keeps for one pipeline, and prunes itself when the plan changes.
- `PipelineRead` was the record phase 3 made (D7); the functions that took it as their first argument are now its methods (`Read`, and the counts, timing and places it reads).
- `ModuleReads` reads each pipeline's modules once for an update, and now keeps each pipeline's totals too, so they no longer travel as a separate dictionary.
- `Update`, `HostPart` and `PipelinePart` each write their own sections. `Update.Write` was 139 lines; none of the writers is over 20 now.
- `Hello` writes the first event. Writing a JSON node, named counts and counts that changed by key are `UpdateWriter`'s, which every section uses.
- Counts by key are `CountChange` records, named values `NamedCount`, and the host's Listens `ListenInfo` (the contract the API already had), rather than tuples.

**`MeepDaemon`** keeps the daemon's life and its pipelines: starting, stopping, pruning and logging.

- `ControlSocket` is the HTTP plumbing: Kestrel on the Unix socket, reading a request and its body, writing answers, events and problems, and `/ws`.
- `DaemonAccess` decides what a client may do, from a token or the user the system says is connecting, and `Require` checks a route's capability.
- `DaemonApi` is the API. `RouteAsync` is a table, a line for each route with the capability it needs and the handler that answers it. `EditAsync` reads the request, makes the edits, then saves them to the file, a branch or nowhere, each in a method.
- `PipelineBootstrap` builds and watches the bootstrap pipeline that loads a hosted one, and says whether it loaded and why not. `StartPipelineAsync` was 83 lines; it's now 21, with `Reserve` and `RunBootstrapAsync`.
- **The registry lookup** that `EditByKeyAsync` and `meep lsp` each had (phase 3's "Found in passing") is `PluginRegistries` in `Meep2.Plugins`: a text's plugins installed on a base registry and kept loaded under a key while its `#plugin` lines stay the same, with `LinesOf` to compare texts' plugins and `Reusing` for `DraftEditor`'s lookup. It went in first, as its own commit, so the `DaemonApi` commit is only moves.

**`Load`** runs the steps in order and defines the pipeline.

- `VerbResolver` works out which inflections a line could mean, and binds their parameters. `Resolve` and `BindCandidates` are each now a few named cases.
- `OptionsBuilder` has the constants, the `defaults` block, each module's options from its modifiers, and the error policies. An error policy's text is parsed where it's read (`Policy`), rather than returned as a tuple for each caller to parse.
- `Wiring` connects modules by indentation, `+` lines and names, and checks buffers.
- `TypeFlow` works out which message types reach each module and reports what doesn't fit. `CheckTypes` was 60 lines; it's now a loop over four named checks.

**`LanguageServer`** keeps the dispatcher and checking documents as they change.

- `JsonRpcChannel` reads and writes LSP's framing.
- `LspWorkspace` has the open files, parsing with what's open, and the verbs each can use (through `PluginRegistries`).
- `LspDocument` was a private class; it's now its own, and keeps its own graph (`Record`, `GraphOf`).
- A class for each feature: `LspCompletion`, `LspHover`, `LspGraphs` (`meep/graph`, `meep/setBase`, `meep/graphChanged`), `LspEdits` (`meep/applyEdits`) and `LspTokens`.
- `RunAsync` was 101 lines; its switch is now a table of handlers, and it's under 20.
- The lines two texts differ by, which was a tuple, are a `ChangedLines` record that moves a graph's spans and gives the LSP range.

**`Node`** keeps its spec, links and instances. Its ten counts and its bytes are `NodeCounters`, its taps `TapList`, and where it listens, its counts by place and its origin `PeerCounts`. Callers write `node.Counters.Received()` and `node.Taps.Offer(message)`. The hot loops call the same atomic adds as before, through one more field.

### The smaller ones

- **`Telemetry`** is `Tracing` (spans, and the weak tables that follow a message's trace) and `Metrics` (the meter, its histogram, and the counts it observes).
- **`BayesTree`** keeps the model. `TreeStore` keeps which trees are in memory and who holds them, so the holders, whether a tree was opened for good and whether to save it when dropped are kept beside each tree there rather than in the tree. `BayesTree.Open` and `Hold` stay as the public way in.
- **`PublicKey`** keeps the key; `AllowedSignersLine` reads a line of an allowed-signers file. `Verify` checks each kind of signature in a method, and the constructor reads ECDSA and RSA keys in methods.
- **`HostIdentity`** keeps the ID file. `MachineAddresses` has the machine's names and addresses, and `DaemonClock` the clock in microseconds, whose `Now` replaces the five places that wrote `HostIdentity.Microseconds(DateTimeOffset.UtcNow)`.
- **`DaemonCommands`**: `meep edit` is `EditCommand`, with its options read into an `EditOptions` record.

### The dispatchers

All four are tables now: `LanguageServer.RunAsync` and `MeepDaemon.RouteAsync` (above), and `MeepCommand.RunAsync` and `DaemonCommands.ClientAsync`. Each is a `switch` expression with one line for each command or route, handing it to a method of its own, so the table shows everything that's supported. `MeepCommand`'s handlers share a `CommandContext` record (plugin settings, output, error, Ctrl+C). `meep run`'s pipeline is `FileRun`, whose exit code, watcher and logging were closures inside one 63-line function.

### The other functions over 40 lines

- **Steps in sequence**, each step a method: `PluginInstaller.InstallNuGetAsync` (with `NuGetResolver` for finding and downloading packages), `ModuleRegistry.AddPlugin`, `SourceEditor.ApplyToFile`, `DaemonConfig.Load` (`DaemonConfigReader`), `KeysCommands.SignAsync`, `Detaching.StartAsync`, `Pipeline.ApplyCoreAsync` (`ChangePlan`), and in the modules Listen.WebSocket's `ServeAsync`, Listen.HTTP's `AcceptAsync`, and Fetch.HTTP's and Send.HTTP's `HandleAsync`.
- **Groups of local functions sharing locals**, which Q222 says should be classes: `DraftEditor.ApplyAsync` (`DraftEditRun`, whose seven locals are its fields) and `ModuleMatcher.Match` (`ModuleMatching`, a method for each pass, in the same order).
- **Writers**, a method for each kind: `MessageJson.WriteValue`, `ExpressionText.Write`.
- **Parsers, along the grammar**: `ParseDirective` hands `#plugin` to `ParsePlugin`, beside `ParseInclude`, as the audit suggested; `ParseModuleLine` parses the verb, then each part after the value (`as`, `@modifier`, `by`, `name=value`) with a method that returns where the next part starts.

## Functions over 20 and 40 lines

Counted with a Roslyn script (in my scratch folder, not the repository, as Q227 asked): lines in a method's, constructor's, accessor's or local function's body, leaving out blank lines, comments and lines with only a brace. That's stricter than the audit's count, which is why `main` shows 157 and 42 here rather than 227 and 83; both columns below are counted the same way.

| | `main` (ea815b9) | Branch (000b1e5) |
|---|---|---|
| Functions over 20 lines | 157 | 128 |
| of which over 40 | 42 | 14 |
| 21 to 40 lines | 115 | 114 |

Of `main`'s 42 over 40, 30 were broken up and 12 are left, all named under "Skipped": the two hot loops and ten parsers. Two new ones are over 40 because they're a `switch` with a line or two for each kind of value, which is all that's left of their writers: `MessageJson.TryWriteSimple` (59) and `ExpressionText.Write` (43).

In the classes taken apart, the longest are now `Subscription.RunAsync` 23 (was 60), `PipelineRead.Read` 22 (`Subscription.Build` was 54) and `MeepDaemon.StartPipelineAsync` 21 (was 83). What else there is still over 20 is under 30: `Load.Prepare` (28, the steps in order), `OptionsBuilder.With` (26) and `WithDefault` (22), a case for each modifier and setting, `VerbResolver.ResolveInPlugin` (22), `Load.CopyDistributedBranches` (21) and `LspGraphs.Answer` (21). Nothing in `DaemonApi`, `ControlSocket`, `LanguageServer` or `Node` is over 20.

## Benchmarks

Run on 22 November on this Mac, `main` (ea815b9, in a scratch worktree) first, then the branch with every runtime change (`Node`, `Telemetry`, `ApplyCoreAsync`), one after the other, with nothing else building. BenchmarkDotNet's medium job, a message's time and allocation end to end:

| Benchmark | `main` | Branch |
|---|---|---|
| Chain, 1 step, same message | 1.390 ± 0.043 µs, 140 B | 1.488 ± 0.057 µs, 120 B |
| Chain, 1 step, new message | 1.991 ± 0.081 µs, 219 B | 1.924 ± 0.042 µs, 218 B |
| Chain, 4 steps, same message | 1.873 ± 0.046 µs, 126 B | 1.895 ± 0.047 µs, 128 B |
| Chain, 4 steps, new message | 2.946 ± 0.064 µs, 489 B | 2.946 ± 0.088 µs, 489 B |
| Leases, none | 1.917 ± 0.043 µs, 126 B | 1.917 ± 0.105 µs, 123 B |
| Leases, shared | 2.524 ± 0.048 µs, 157 B | 2.396 ± 0.041 µs, 156 B |
| Leases, each message | 3.118 ± 0.095 µs, 576 B | 3.256 ± 0.118 µs, 587 B |
| Observing nothing | 2.041 ± 0.050 µs, 218 B | 1.856 ± 0.033 µs, 218 B |
| Observing the histogram | 2.059 ± 0.036 µs, 218 B | 1.958 ± 0.066 µs, 218 B |
| Observing with a tap | 3.366 ± 0.050 µs, 219 B | 3.168 ± 0.046 µs, 218 B |
| Observing, sampled | 2.981 ± 0.261 µs, 788 B | 2.803 ± 0.042 µs, 795 B |
| Observing, tracing | 7.870 ± 0.338 µs, 2,482 B | 7.052 ± 0.197 µs, 2,482 B |

Allocation is the same everywhere, to within the few bytes that move from run to run. Times move both ways. The one row slower by more than its error bars, a one-step chain of the same message (7%), I ran again for both, back to back:

| Benchmark | `main` | Branch |
|---|---|---|
| Chain, 1 step, same message | 1.184 ± 0.031 µs | 1.227 ± 0.029 µs |
| Chain, 1 step, new message | 1.656 ± 0.019 µs | 1.662 ± 0.025 µs |
| Chain, 4 steps, same message | 1.674 ± 0.042 µs | 1.699 ± 0.033 µs |
| Chain, 4 steps, new message | 2.582 ± 0.060 µs | 2.711 ± 0.056 µs |

The whole machine was about 15% faster for the second pair, which says how much the runs move. Here the branch is 0–5% slower, within or near the error bars, where in the first run it was as often faster. I read this as no change I can measure, with a possible cost of a few percent from counting through `node.Counters` and offering taps through `node.Taps`, one field load more each. If you'd rather not risk it, `NodeCounters` could be a struct field of `Node`, which removes the extra load; Q247 asks.

**Subscriptions** (`subscriptions --seconds 10`, for the `Subscription` split): every kind of update is the same size to the byte, except those with timing, whose histograms come from measured handling times and were 4% larger on the branch's run. Each update allocates about 4 KB more (0.3%; `SentState`'s reads and the parts it now makes as objects), and every one was made faster on the branch's run (a CBOR update of what changed 3.54 against 2.33 ms), mostly the machine. The live phases' CPU over a run without a client was lower on the branch for every phase (159 against 61 ms a second for the busiest), within how much the baselines themselves moved (544 against 448 ms a second).

## Skipped, and why

- **Hot loops, kept whole on purpose** (section 6): `InflectionNode.RunLoopAsync` (70 lines) and `Pipeline.DeliverTo` (41). The audit said to move out only what's synchronous and keep every `await` in the loop; what's left in them is the work itself, interleaved with the awaits and counts, and splitting it further would only move lines around the hot path for the benchmarks to judge. Their counting calls changed with `Node` (`Counters.Received()` rather than `CountReceived()`), and nothing else.
- **Parsers and number code, kept to their grammar** (section 6): `TemplateParser.Parse` (59), `ExpressionParser.Parse` (52) and `ParsePrefix` (46), `ErrorPolicyParser.ParsePart` (64), `LineParser.ParseTemplateHeader` (51), `BigDecimal.Read` (50), `Numbers.TryConvert` (41), `ParameterBinder.Bind` (43), `Highlighter.Walker.Expression` (44) and `HostSettings.Parse` (53, the command line's options). Each is one rule of its grammar, read top to bottom; I split only `ParseDirective` and `ParseModuleLine`, where one function had several rules in it. Q244 asks whether you want the rest split too.
- **`ModuleContext`** (the SDK's view of the runtime for modules), as the audit said: plugins are written against it, so it waits for the SDK's packaging (goal 9).
- **Two switches over kinds of value**, `MessageJson.TryWriteSimple` (59) and `ExpressionText.Write` (43): each case is a line or two, and splitting the switch would only spread one table over several methods.
- **The 21–40-line functions** that weren't in a class being taken apart, as the audit planned: they're split when they're next changed.

## Found in passing

- **`LanguageServer.Kebab` was never called.** It went with the move.
- **`meep/setBase` compares plugins differently from everything else.** `LspGraphs.RegistryOfBaseAsync` treats two texts as having the same plugins when their `#plugin` lines are the same without their `as` names (`string.Join('\n', own.Plugins)`), where `PluginRegistries.LinesOf`, which the edits and the daemon use, includes them. So a running pipeline whose plugin is named differently with `as` would reuse the document's registry, under the wrong name. I left it as it was; Q245 asks.
- **Where Listens are bound is worked out twice**, by `DaemonApi.Listening` for `GET /` and by `SentState` for a subscription's host part, the second also leaving out a pipeline with no counts yet. `Sending` was already shared. Q246 asks whether to share it.
- **Unused `using` lines from before this phase** in `DaemonClient.cs`, `DaemonCommands.cs`, `KeysCommands.cs`, `ICheckedComputed.cs`, `LivePipeline.cs`, `PipelineDraft.cs`, `NodeContext.cs` and `APassthrough.cs`, found by turning on IDE0005 for a build. I removed only those my moves left behind, and the one in `MeepCommand.cs`, which I was rewriting anyway.
- **`SshReader` is a `ref struct`**, so a method that reads part of a key has to take it by `ref`. The first build of the `PublicKey` split passed it by value, and the ECDSA and RSA tests caught it (five failures, "it has bytes left over"); the committed version passes it by `ref`.

## Decisions I made while building

- **Records rather than tuples where a value crosses a class boundary**: `CountChange`, `NamedCount`, `Slot`, `ModuleRead`, `ChangedLines`, `KeptId`, `ResolvedPackage`, `DirectiveWord`, `ChangePlan.KeptNode`, and others private to one class. Private helpers whose one caller takes a tuple apart on the same line stayed tuples, as the audit allowed: `Subscription.TakeInput` and `LspCompletion.LineBefore`.
- **`PluginRegistries` lives in `Meep2.Plugins`**, beside `Install`, which it calls; both the CLI and the daemon reference it. It keeps each set of plugin lines under its own key for the daemon's edits, as before, and one key per document for `meep lsp`, as before.
- **Public names stayed where callers use them**: `BayesTree.Open` and `Hold`, `PublicKey.Parse(line)` and `Subscription.ModuleCounts` and its friends forward to or stay beside the new classes, so modules, tests and plugins don't change. Internal constants moved with their class (`ControlSocket.MaxBody`, `DaemonApi.MaxPipelineText`, `JsonRpcChannel.MaxMessage`), and the tests now name them there.
- **`Metrics` publishes its instruments when a pipeline starts.** `Telemetry`'s static constructor made the observable counters the first time anything touched it, which was the first message emitted. `Metrics` alone isn't touched until a module handles a message, so a pipeline with only sources would never have published them; `Metrics.Publish()` when a pipeline starts running keeps them visible, a moment earlier than before.
- **Commits that shared files** (`Pipeline.cs`, `InflectionNode.cs` and others had changes for `Node`, `Telemetry` and `ApplyCoreAsync`; `DaemonCommands.cs` for `EditCommand` and the dispatcher) were staged with each commit's own version of the file, built from `main`'s by the same rewrites, so each commit holds only its own change.
- **`EditCommand` and the other new CLI classes** take `CliContext` or the new `CommandContext`, rather than the client, output, error and token separately. `CommandContext` has no daemon client, since most commands don't need one.
- **Benchmarks for `Node` and `Telemetry` together**: both change the runtime's message path, so `main` and the branch's last commit were measured, one after the other, rather than each commit.

## Questions

**Q244.** Ten parsers and number functions are still over 40 lines (listed under "Skipped"), each one rule of its grammar. Split them anyway, say into a method for each alternative of a rule, or leave them until they're next changed? I'd leave them: they read top to bottom as the grammar does.

**Q245.** `meep/setBase` reuses the document's registry when the running text has the same `#plugin` lines, compared without their `as` names, where everywhere else compares them with. Should it compare with the names too (`PluginRegistries.LinesOf`)? It's a one-line change, and it would load a running pipeline's plugins separately when it names one differently from the document. I'd make it.

**Q246.** `GET /` and a subscription's host part each work out where Listens are bound, slightly differently (the subscription leaves out a pipeline that has no counts yet). Share one `DaemonApi.Listening(pipelines)`, as `Sending` is shared? I think the difference is only for a pipeline still loading, which has no Listens anyway, so sharing it changes nothing anyone sees, but I'd like your word before changing what a subscription sends.

**Q247.** Counting through `node.Counters` and offering taps through `node.Taps` costs one field load more for each count than counting on `Node` did. The benchmarks can't tell it apart from noise (0–5% either way), but if you'd rather it cost nothing, `NodeCounters` and `TapList` could be structs held in `Node`'s fields, which keeps the class and removes the load, at the price of structs that must never be copied. Leave them as classes, or make them structs? I'd leave them.
