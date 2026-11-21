# Refactoring: an audit and a plan

Written 21 November 2026. An audit of Meep2 as it is on `main` at `a5de139`, for the priorities you gave: security vulnerabilities, duplicate code, functions longer than 20 lines that can be broken into reusable ones, functions that belong together, memory leaks, inefficient memory use, poor class hierarchies, performance, and files with more than two classes. Each finding has an ID (S for security, M for memory leaks, E for inefficient memory use, P for performance, D for duplicates, H for hierarchies, T for tests) so the plan at the end can refer to it. Questions are numbered from 222, and are now answered (21 November); what each answer changed is in "After your answers", before the appendix, and the sections below are updated to match. Nothing in the code has changed, and this file isn't committed.

## How I looked

- **Measured**, with a throwaway Roslyn script kept outside the repo, over the 135 files in `src` (33,235 lines): each function's length in lines of code inside its body, its branches, the types in each file, stretches of code that repeat (exactly, for 60 tokens or more, or with names and literals changed, for 90 or more), and which members of each class share fields and calls (LCOM4, and a version that ignores the fields nearly every member uses).
- **Read by hand** where the risks are: the daemon's API, tokens and sockets; installing, checking and loading plugins; Git sources and commit signing; `meep keys` and `meep sign`; the network modules; the runtime's core; subscriptions; `meep lsp` and the VS Code extension.
- **Ran the tests first:** 615 tests, 614 pass. The one that failed passes every time on its own; it's a race in the test, not the code (T1).
- **"Functions with high cohesion"** I took to mean groups of functions that share data and call each other but live inside a bigger class that does other things too, so they'd make a class of their own (section 7). Q222 checks that's what you meant.

| | |
|---|---|
| Functions over 20 lines | 227, in 75 files: 94 of 21–30 lines, 84 of 31–50, 42 of 51–100, 7 over 100 |
| Files with more than two classes, records or structs | 62 of 135 |
| Repeated code | about 15 places, none longer than 32 lines |
| Largest classes | `Subscription` 1,154 lines, `Pipeline` 1,015, `MeepDaemon` 952, `Load` 909, `LanguageServer` 897, `LineParser` 698, `SourceEditor` 645 |

The code is in good health: little is repeated, the hot paths are written with care for allocation, and the security-sensitive code (tokens, signatures, zips, peer credentials) is mostly right. The problems gather in a few places: paths built from text that a pipeline or a message controls, state the daemon keeps longer than it needs, and a handful of very large classes.

## 1. Security

**S1. A `#plugin` line can point the plugin cache outside it, at a folder that's then deleted (high impact, unlikely).** The parser only checks that a GitHub plugin's name has two non-empty parts and that a NuGet ID has no `/` (`MeeplangParser.cs:399–456`). `PluginInstaller` builds the cache folder from those words and the version as written (`PluginInstaller.cs:170`, `:215`), and `Publish` deletes that folder, recursively, before moving the new copy in (`:463–467`). So `#plugin github.com/../.. 1.0/../../…` resolves outside `~/.meep/plugins`. I couldn't build a practical attack, since the SHA pin and the shape of GitHub's download addresses get in the way, but text in a pipeline file shouldn't choose what Meep deletes, and pipeline files can come from URLs. *Fix:* in the parser, check owner and repo against GitHub's own rules (letters, digits, `-`, `_` and `.`, but not `.` or `..`), versions against `[0-9A-Za-z.+-]`, and NuGet IDs with NuGet's own validator; in the installer, refuse any folder whose full path isn't inside the cache. *Tests:* each bad line is refused with a diagnostic; the installer refuses a folder outside the cache.

**S2. Computed paths in Save and Fetch.File can be steered by the message (high, for pipelines that do it).** Behind a Listen, `Save "~/inbox/{msg.Query['name']}.txt"` writes wherever `name=../../.ssh/authorized_keys` points, and `Fetch "{msg.Text}"` reads any file you can (`Output.cs:67–90`, `Fetch.cs:338–372`, `Paths.Expand` in `Sources.cs:111–120`). That's a write anywhere the daemon's user can write, for anyone who can reach the Listen. *Fix (Q223: both):* a `root=` parameter that keeps paths inside a folder, and refusing `..`, absolute paths and `~` in what a `{…}` hole produces unless the module sets `any-path=true`. *Tests:* each refusal, and that a path staying inside is still saved.

**S3. Fetch.File's size limit can be passed (medium).** It checks the file's length, then reads it whole with `File.ReadAllTextAsync` (`Fetch.cs:357–363`). A file that grows in between, or a device such as `/dev/zero`, whose length is 0, is read until memory runs out, and with S2 a request can name one. *Fix:* read through the bounded reader the HTTP modules use (`Bodies.ReadAsync` with `max-size`), and refuse anything that isn't a regular file.

**S4. A slow `/ws` client can grow the daemon's memory without limit (medium).** Over the WebSocket API, a tap's or a stats stream's events go into the session's outgoing queue, which is unbounded, without waiting for the socket (`SocketSession.cs:81`, `:233–234`, `:355–370`). A client that reads slowly, such as a dashboard on a poor connection, piles up frames, and the tap's own drop-the-oldest buffer never drops, because the queue empties it at once. Over HTTP the same streams wait for each write, and subscriptions already wait (`QueueAsync`). *Fix:* send stream events through `QueueAsync` too, so a stream waits for the socket and its tap drops as designed. *Test:* a tap whose client stops reading: the queue stays bounded and `dropped` climbs.

**S5. Downloads have no size limit (low).** A pipeline from a URL, and its signature, are read whole into memory (`PipelineSources.cs:226–240`), and a GitHub plugin's zip is written to disk however large it is (`PluginInstaller.cs:407–425`). *Fix:* a limit for each, say 4 MiB for pipeline text and signatures and 256 MiB for a plugin, with a clear error.

**S6. Git sources allow `git://` (low).** `GitSnapshot.Fetch` turns away `ssh://`, scp-style and `http://` addresses, so anything else gets through, including unencrypted `git://` (`GitSnapshot.cs:47–50`), though Q66 says HTTPS only. A pin or a signature still protects what's read, so this is about privacy. *Fix:* allow only `https://`, `file://` and paths on this machine.

**S7. Meep's own folder is readable by other users (low).** `~/.meep` is made with the default permissions, 755 on this Mac, and so are `logs/` (`Detaching.cs:44–45`), `trees/` (`Bayes.cs:71–76`, `:253`) and `allowed_signers` (`KeysCommands.cs:261–265`); only `daemon/` is kept to you. Detached pipelines' logs and learned trees can hold private text. The tokens file is created first and narrowed after (`Capabilities.cs:93–95`), a short window that `daemon/` covers. *Fix:* one helper that makes Meep's folder and its subfolders 700, and new files 600, as they're created, used everywhere; and, as OpenSSH does, don't trust an `allowed_signers` file that others can write to.

**S8. The VS Code extension runs whatever `meeplang.meepPath` says, and a workspace can set it (low).** (`editors/vscode/package.json:45`, `extension.ts:8`.) VS Code's Workspace Trust disables extensions that don't declare support in folders you haven't trusted, which covers it, but saying so explicitly is safer. *Fix:* give the setting `"scope": "machine-overridable"`, and declare `capabilities.untrustedWorkspaces` with the setting restricted.

**S9. The daemon reads a request's body before checking the route's capability (low).** Any client, even one whose token only observes, can make the daemon read up to Kestrel's default of 30 MB for each request (`MeepDaemon.cs:493–499`, `:540–548`). *Fix:* set the control socket's body limit to 1 MiB, as `/ws` frames have.

**S10. `meep lsp` stops on a malformed message (low).** Missing fields hit `!` operators, and a bad `Content-Length` throws, which ends the server (`LanguageServer.cs:56–175`, `:844–862`). The editor is trusted, so this is about robustness. *Fix:* catch for each message and answer with a JSON-RPC error; cap `Content-Length`.

**S11. Public Listens tell anyone the daemon's lasting ID (a question).** A Listen answers `Meep-Host: ?` from any client (`HttpListen.cs:224–225`), as designed in Q213 and Q214, and Fetch and Send give their origin, which names the pipeline and module, to any host that says it's Meep. On a Listen open to the internet, that's an ID that follows the machine across restarts, and pipeline names that may say more than you'd like. *Fix (Q224):* answer `Meep-Host` only to loopback and private addresses unless a setting allows anyone; give origins only to hosts in `meep-hosts`, this machine and private addresses; and say in the docs that flow evidence is what the other end claims, not proof.

**Checked and sound:** tokens are 256 random bits, kept as SHA-256 and compared in constant time; the socket's folder is yours alone, and the peer's user is checked on macOS and Linux; zips can't write outside their folder; plugin signatures check the namespace, the key, every file's hash and what's left uncovered; RSA with SHA-1 is refused; `meep lsp` turns developer mode off and doesn't download; Listen bodies, TCP messages and WebSocket frames have limits; the arrivals table is bounded and origins are capped in length; service files are escaped for launchd and systemd; programs are started with argument lists, never through a shell. One thing worth saying in `meep daemon token`'s help: a token with `control` can start any pipeline, plugins included, so it's as powerful as your shell.

## 2. Memory leaks

**M1. An ended pipeline in the daemon keeps everything it ran with, plugins included (medium).** `HostedPipeline` keeps the built pipeline, with its module instances, registry and live syntax, and the bootstrap pipeline, for as long as it stays listed: 24 hours unless `keep-finished` says otherwise (`HostedPipeline.cs:16`, `:164`, `:406–419`). Its plugins' holds are let go when it ends, so their load contexts are marked to unload, but they can't unload while those objects refer to their types. And ended pipelines are only pruned when something asks the daemon for its list, through the API or a subscription (`MeepDaemon.cs:103`, `:447–455`, `:576`), so with nobody asking they stay. *Fix:* when it ends, keep only what's listed and shown (the final counts, which `End` already keeps, its text and its graph) and let go of the rest; prune on a timer too. *Test:* the plugin unloading test (`PluginTests.cs:437`), through the daemon.

**M2. Learned trees stay in memory for the life of the process (medium).** Every tree any pipeline opens is kept in a static dictionary and never let go (`Bayes.cs:33`, `:99–110`), so a daemon that has run many pipelines holds all their vocabularies. *Fix:* count the modules using each tree, and when the last lets go, save it and drop it.

**M3. Client names are never forgotten (low).** `ClientNames` keeps up to 4,096 addresses and never removes one, so a public Listen fills it and later clients get no name (`ClientNames.cs:15–43`). *Fix:* when it's full, drop names older than ten minutes.

**M4. Folder watchers outlive the includes they were for (low).** When a pipeline stops including a file in another folder, that folder's watcher stays (`PipelineWatcher.cs:67–90`). *Fix:* in `Refresh`, dispose watchers no watched file needs.

**M5. Temporary key files aren't deleted (low).** A `user.signingkey` of the form `key::…` is written to a new file in the temp folder for every commit, and left there (`CommitSigning.cs:26–31`). *Fix:* delete it after signing.

**M6. Programs left running (low).** When `meep sign` is cancelled, `ssh-keygen` keeps running, perhaps waiting for a passphrase (`KeysCommands.cs:249–259`), and so do `launchctl` and `systemctl` in `DaemonService` (`DaemonService.cs:173–190`). `CommitSigning` kills on its timeout; the others should too (D10).

**M7. `Pipeline` never disposes `_stopSources` (trivial)** (`Pipeline.cs:48`).

**M8. The fam chain (unchanged, Q7).** You decided to measure before trimming ancestry. I'd add the measurements now so Q7 can be decided with numbers: memory kept for Tick → Fetch (5 MB) → Split into 10,000; for a WebSocket connection open for a day; and for a loop that makes each message from the one before, which grows a chain without end. Q228: yes, as a benchmark in phase 1, with the numbers going to you to decide Q7.

## 3. Inefficient memory use

**E1. Graphs are versioned by serialising them (medium).** `HostedPipeline.Graph()` serialises the whole graph to JSON to see whether it changed (`HostedPipeline.cs:187–201`), and every detail, which a stats stream asks for on each tick for each client, works out the link counts afresh (`:437–461`, `MeepDaemon.cs:864`). *Fix:* count a version up where the graph changes (reload, edit, restart) instead of comparing text, and keep the links' keys for each version.

**E2. Subscriptions find changes by building strings (low).** Each update formats a line for each pipeline, Listen and Send to compare with the last (`Subscription.cs:646`, `:695`, `:702`). *Fix:* compare the values themselves.

**E3. A new buffer for every frame and event (low).** `SocketSession.Write` and the daemon's event writer each make an `ArrayBufferWriter`, then copy it (`SocketSession.cs:357–369`, `MeepDaemon.cs:972–975`, `:989–994`). *Fix:* a pooled writer for each session.

**E4. NuGet packages are copied twice in memory (low)** (`PluginInstaller.cs:264–270`, `:293`): `MemoryStream.ToArray()`, then a new stream over the copy. *Fix:* use the stream's own buffer.

**E5. Routing allocates when message types alternate (low, but on the hot path).** `InflectionNode.RouteFor` makes a new `Route` whenever a message's type differs from the last one's (`InflectionNode.cs:335–343`). *Fix:* keep `Route`s in the cache and reuse them.

**E6. `meep lsp` reads headers a byte at a time (low)**, one asynchronous read per byte (`LanguageServer.cs:864–877`). *Fix:* a buffered reader.

**E7. TCP framing searches from the start again (medium, for large messages).** When a message arrives in pieces, each read searches the whole buffer again from its first byte (`TcpListen.cs:267–297`), which takes time growing with the square of the message's size. *Fix:* remember how far it has searched.

**E8. A module's counters share cache lines (measure first).** Modules feeding a module count `_received` on their threads while it counts `_handled` and `_emitted` on its own, in neighbouring fields (`Node.cs:80–91`); `LinkCount` already pads for this. Worth a benchmark with several feeders before changing anything.

## 4. Performance

**P1. Waiting on asynchronous work by blocking (medium).** Includes from a URL are fetched with `.GetAwaiter().GetResult()`, up to 30 seconds each (`PipelineSources.cs:255–257`); committing an edit blocks on `ssh-keygen` for up to 15 seconds (`PendingBranch.cs:100`); and Listen.HTTP starts a new route while holding a lock (`HttpListen.cs:121–122`). Each ties up a thread-pool thread, the last inside a lock. *Fix:* read includes asynchronously (or fetch them before parsing), make `PendingBranch.Commit` asynchronous, and start the new route outside the lock.

**P2. Changes and flushes poll every millisecond while a module is busy (medium).** `RunBetweenMessagesAsync` waits for the loop with `Task.Delay(1)` in a loop (`InflectionNode.cs:189`), so a flush timer that fires while a module handles a slow message spins on timers until it's done. *Fix:* a completion the loop signals when it reaches the gap between messages.

**P3. Two file-system calls for every API request (low).** The daemon checks the tokens file's time on every request, and sets `_tokens` and `_tokensWritten` without a lock (`MeepDaemon.cs:688–697`). *Fix:* check at most once a second, under a lock, or watch the file.

**P4. Smaller ones:** the stats stream makes a timer for each tick where a `PeriodicTimer` would do (`MeepDaemon.cs:868`); changing a pipeline uses `Contains` on lists inside loops (`Pipeline.cs:438`, `:460`, `:488`).

Every change to the runtime gets the benchmarks run before and after (FlowCost, FlowMicro, ChainBenchmarks, ObservingBenchmarks, LeaseBenchmarks), against `main` in a scratch worktree on the same day, as before.

## 5. Duplicate code

| ID | What repeats | Where | Fold into |
|---|---|---|---|
| D1 | stamp, count, trace, tap, deliver | `Pipeline.Emit`, `EmitLeased`, `EmitTracked` (`Pipeline.cs:681–756`) | one private method that takes the extra tracking scope |
| D2 | nine members forwarding to the node | `InflectionContext`, `SourceContext` (`Contexts.cs:29–49`, `:229–253`) | a shared base class (H1) |
| D3 | the start of `HandleAsync`: leave errors alone, work out the address and text, check the address | `SendWebSocket`, `SendTcp` (`Send.cs:302–321`, `:436–455`) | a shared base class (H2) |
| D4 | validated `Timeout`, `Tries`, `RetryDelay`, `MaxSize`, `Ping`, `MaxQueued`, `End`, `Length`, `Connections` | seven parameter records (`Send.cs:349–380` and `:493–525`; `Send.cs:373–390` and `WebSocketListen.cs:318–335`; `Send.cs:477–497` and `TcpListen.cs:152–172`) | base records (H3) |
| D5 | turning an HTTP response into `HttpResponse` or `HttpErrorMessage` | `HttpFetch`, `SendHttp` (`Fetch.cs:80–100`, `Send.cs:604–623`) | one helper |
| D6 | certificate checks in `WhyNot` | `HttpListen`, `WebSocketListen` (`HttpListen.cs:46–57`, `WebSocketListen.cs:59–70`) | `Certificates.WhyNot` |
| D7 | the loop for each kind of count, and writing them | `Subscription.Modules`, `Links`, `Bytes`, `Timing` (`Subscription.cs:400–463`); `:962–976` and `:1105–1119` | one generic loop, one writer |
| D8 | the two passes that pair unchanged modules | `ModuleMatcher.cs:73–81`, `:116–124` | one pass that takes the rule for parents |
| D9 | finding a generic interface on a module's class | `FindWhyNot`, `FindMostAtOnce`, `FindReconfigure` (`ModuleRegistry.cs:526–537`, `:611–624`) | one `FindContract(type, parameters, typeof(IX<>))` |
| D10 | running a program and reading what it says | `CommitSigning.SignAsync`, `KeysCommands.SshKeygenAsync`, `DaemonService.RunAsync` | one runner that kills on timeout and cancellation (fixes M6) |
| D11 | expanding `~`, and finding your SSH key | `KeysCommands.cs:238–247`, `CommitSigning.cs:36–37` and `:88–91`, `Sources.cs:111–120` | one of each |
| D12 | an `HttpClient` with Meep's User-Agent | `PluginInstaller.cs:70–75`, `KeysCommands.cs:13–18`, `PipelineSources.cs:108` | one shared client |
| D13 | writing durations as `250ms`, `5s`, `2m` | `Node.cs:356–360`, `Combining.cs:299–304` | beside `Durations`'s parser |
| D14 | reading a query string | `SocketSession.cs:320–327` by hand, the HTTP API by ASP.NET | ASP.NET's for both, so they agree |
| D15 | the body members of HTTP messages | `HttpResponse`, `HttpRequest` (`Messages.cs:40–70`, `:100–125`) | left as they are (Q229) |

## 6. Functions over 20 lines

227 functions have more than 20 lines of code in their bodies. Splitting every one would make some harder to follow, parsers especially, so I've sorted the 83 over 40 lines by the kind of function they are, which decides how each should be broken up. The 144 of 21–40 lines I'd split when they're next changed, or when part of one is wanted elsewhere. All 227 are listed in the appendix.

**Dispatchers: a table of handlers instead of a long switch.** `LanguageServer.RunAsync` (111 lines), `MeepDaemon.RouteAsync` (82), `MeepCommand.RunAsync` (81), `DaemonCommands.ClientAsync` (58). Each case becomes a method, and the table gives one place to catch errors (which also fixes S10) and to see what's supported.

**Steps in sequence: a method for each step.** `MeepDaemon.StartPipelineAsync` (107: reserve the name, the bootstrap's options, build it, run it, wait until it's ready), `PluginInstaller.InstallNuGetAsync` (95: resolve, download, unpack, check, publish), `DraftEditor.ApplyAsync` (92), `SourceEditor.ApplyToFile` (90), `MeepCommand.RunFileAsync` (85), `Pipeline.ApplyCoreAsync` (83), `HttpFetch.HandleAsync` (81) and `SendHttp.HandleAsync` (79), which share D5, `LanguageServer.ApplyEditsAsync` (77), `Subscription.RunAsync` (76), `DaemonConfig.Load` (70), `KeysCommands.SignAsync` (69), `ModuleRegistry.AddPlugin` (65), `Load.BuildOptions` (63), `WebSocketListen.ServeAsync` (63), `ParameterBinder.Bind` (63), `Detaching.StartAsync` (63), `MeepDaemon.EditAsync` (62: edit, apply, then save to the file, a branch or nowhere), `Load.CheckTypes` (60), `SendReply.HandleAsync` (60), `HttpListen.AcceptAsync` (60), and the others from 41 to 58 lines that read as steps.

**Writers: a method for each section.** `Subscription.Update.Write` (213: the list, the host, the runtime, and for each pipeline its graph, totals, counts, places, folded, clients, peers and timing), `MessageJson.WriteValue` (112, one for each kind of value), `ExpressionText.Write` (85), `Subscription.Build` (65).

**Parsers and number code: only along the grammar.** `LineParser.ParseModuleLine` (126), `ParseDirective` (120, which should become `ParsePlugin` beside the existing `ParseInclude`), `ParseTemplateHeader` (73), `TemplateParser.Parse` (78), `ExpressionParser.Parse` (70) and `ParsePrefix` (59), `ErrorPolicyParser.ParsePart` (74), `ModuleMatcher.Match` (107, a method for each matching pass, which also removes D8), `BigDecimal.Read` (66), `ExpressionBinder.Comparison` (51) and `Arithmetic` (49), `Numbers.TryConvert` (57), `Lexer.ReadDurationUnits` (43). A parser reads best when each method is one rule of the grammar, even a long one, so I'd split these where a rule has taken on several jobs, not to reach 20 lines.

**Hot loops kept whole on purpose.** `InflectionNode.RunLoopAsync` (96) is written inline so that awaiting doesn't allocate for each message, as its comment says, and the same goes for `SourceNode.RunAsync` (49) and `Pipeline.DeliverTo` (64). I'd only move out what's synchronous (the checks before a message is handled, the bookkeeping after), keep every `await` in the loop, and check that the benchmarks don't move.

## 7. Functions that belong together: classes to extract

These are groups of members that share fields and calls among themselves, and not with the rest of their class. The measurements found `Node`'s, `Telemetry`'s, `BayesTree`'s and `HostIdentity`'s on their own. The others show as one tangle, joined through a few fields and helpers that every part uses, so their groups come from reading them.

| Class | Groups found | Becomes |
|---|---|---|
| `Node` (284 lines) | six with no field in common: ten counters and `GetStats`; taps; arrivals, destinations and origin; bytes; links; listening | `NodeCounters`, `TapList`, `PeerCounts`, leaving `Node` its spec, links and instances |
| `Subscription` (1,154) | the timing (slots, rates, budget, heartbeat); what was last sent, and finding what changed; the update and how it's written; hello | `SubscriptionClock`, `SentState`, `Update` (in its own file, with a writer for each section), `Hello` |
| `MeepDaemon` (952) | HTTP plumbing (reading requests and JSON, events, problems); who may do what (tokens, capabilities); the API's handlers (stats, tap, inject, edit); the daemon's life and its pipelines | `ControlSocket`, `DaemonAccess`, `DaemonApi`, with `MeepDaemon` keeping the last |
| `Load` (909) | resolving verbs; constants, defaults and options; wiring; type flow; the order they run in | `VerbResolver`, `OptionsBuilder`, `Wiring`, `TypeFlow`, with `Load` running them |
| `LanguageServer` (897) | JSON-RPC (reading, writing, answering); completion; hover; graphs; edits; semantic tokens | `JsonRpcChannel`, and a class for each feature |
| `Telemetry` (in Runtime) | tracing; metrics | `Tracing` and `Metrics` |
| `BayesTree` | the model; where trees are kept and which are open | `BayesTree` and `TreeStore`, which is where M2 is fixed |
| `PublicKey` | reading allowed-signers lines (`Parse`, `WithOptions`, `Words`, `Split`); the key itself | `AllowedSignersLine` and `PublicKey` |
| `HostIdentity` | the ID file; the machine's names and addresses; the clock | `HostIdentity`, `MachineAddresses`, and `Microseconds` beside the other clock code |
| `DaemonCommands` | the edit commands (`EditAsync`, `EditOf`, `Setting`, `ModuleEditOf`, `Modifier`, `IsName`) | `EditCommand` |

`ModuleContext`, the SDK's view of the runtime for modules, also measures as many separate groups (emitting, keeping, counting, tracing, listening), but it's what plugins are written against, so splitting it would break them. I'd leave it, and look again when the SDK is packaged (goal 9).

### Functions only one caller can use (Q222)

Your answer adds three more signs of a function that's no use to any other caller without work done inline. I measured each:

- **Many parameters (6 or more, not counting a cancellation token): 25.** The ones worth a record:
  - `Subscription.Modules`, `Links`, `Bytes`, `Timing`, `Places` (7–8 each), plus `Counts` and `Gone`, pass the same update, pipeline name, plan, rate, pipeline, keys and what was sent to each other. One `PipelineRead` record carries them, and goes with D7.
  - `Connections.SendAsync` (8: client, message, text, end, tries, retry delay, timeout, context) takes four values straight from a Send's parameters. It should take the base record from H3 instead.
  - `DaemonCommands.StartAsync`, `EditAsync`, `TapAsync`, `InjectAsync` (6 each) all pass the client, output and error along. A `CliContext` record carries them.
  - `PendingBranch.CommitTo` (7) shrinks when it becomes asynchronous (P1).
  - The rest I'd leave: `Stamper.Stamp` is on the hot path and passes a `ref`; `Tally.Count` takes six optional counts by name, which reads well where it's called; the two constructors are internal and called once.
- **Tuples returned: 42.** Most are private helpers whose one caller takes them apart in the same line, which is fine. The pattern worth fixing is a value paired with why it couldn't be made, written out afresh each time: `LivePipeline.Load` and `PipelineLoader.Define` (a result and diagnostics), `PipelineDraft.SetBase`, `CommitSigning.KeyFor` and `SignAsync`, `LanguageServer.RegistryForAsync`, `CompiledComputed.ProblemWith` and `KeysCommands.SshKeygenAsync` (a result or a reason). One reusable record for "a value, or why not", with helpers to get the value or report the reason, lets callers nest the call instead of unpacking it. `Pipeline.ReadPeers` is public and returns a list of tuples, so it should return a named record.
- **Arguments prepared only for one call:** only two calls take three or more locals made just for them (`Subscription.cs:192`, `Signatures.cs:347`), and both read fields out in order, which is fine. I'll keep looking for this as the big classes are taken apart (phase 4).

These go into phase 3 (the records and the "value or why not" type) and phase 4 (applying them as classes are extracted).

## 8. Class hierarchies

**H1. Module contexts.** `InflectionContext` and `SourceContext` both derive from `ModuleContext` and repeat what they forward to their node (D2). A `NodeContext` between them holds it once.

**H2. Send's connection inflections.** `SendWebSocket` and `SendTcp` have the same fields, the same lifecycle and the same first half of `HandleAsync` (D3). An abstract `ConnectionSend<TParameters>` holds them, with each inflection supplying its client and how it checks its address.

**H3. Parameter records.** The same validated properties are written out in seven records (D4). Base records (for retries, for framing, for connections) would hold them once; where defaults differ, as `max-size` does (1 MiB for WebSocket and TCP messages, 16 MiB for HTTP bodies), the property stays where it is. The binder already reads inherited properties, but the verb catalogue looks up their documentation under the record that inherits them (`VerbCatalogue.cs:167`), so it needs to use the declaring type instead. Q225: base records.

**H4. Tracking scopes held as `object`.** `Message.Tracking` is an `object?` holding nothing, one scope or an array, and `ScopeSet` casts it in three switch statements (`Tracking.cs:68–114`). A readonly struct like `Parents` keeps the design that doesn't allocate, makes it typed, and replaces the three switches with one enumerator.

**H5. Words where types would do.** Subscriptions name what's asked for with strings (`"modules"`, `"links"`, `"totals"`…) and reach totals by position (`totals[12]` to `totals[14]` in `Subscription.cs:585–587`). An enum for the kinds and named positions would let the compiler catch a typo.

**H6. Two types called `AInflection` in one file**, with `IRouteMessages` between them (`AInflection.cs`). Each gets a file of its own (section 9).

The SDK's module classes (`AModule`, `ASource`, `AInflection<T>`, `APassthrough<T>`) are a sound, shallow hierarchy whose constructors plugins can't misuse; I wouldn't change them.

## 9. Files with more than two classes

62 of the 135 files hold more than two classes, records or structs. They're of three kinds:

- **Several classes with behaviour of their own**, which I'd split one to a file, each named after its class: `Send.cs` (five inflections, their parameter records and `Connections`, into a `Send/` folder), `Signatures.cs` (eight), `PipelineLoader.cs`, `Subscription.cs`, `HttpListen.cs`, `TcpListen.cs`, `WebSockets.cs`, `Clients.cs`, `Fetch.cs`, `Combining.cs`, `Classifying.cs`, `Bayes.cs`, `PluginInstaller.cs`, `PluginCatalog.cs`, `Contexts.cs`, `Node.cs`, `Tracking.cs`, `MeeplangParser.cs`, `SourceEditor.cs`, `DraftEditor.cs`, `ModuleRegistry.cs`, `VerbCatalogue.cs`, `HostingModules.cs`, `PipelineSources.cs`, `SocketSession.cs`, `UpdateWriter.cs`, `LanguageServer.cs`, `Capabilities.cs`, `DaemonService.cs`, `Lexer.cs`, `Highlighter.cs` and the rest of this kind.
- **A class with a small companion**: a verb's class and its parameters record, or a class and the exception it throws. With one verb to a file, these come to two types, which meets the rule.
- **Families of one-line records**: `Contracts.cs` (26 records the daemon's API sends), `Messages.cs` (16 message types), `ExpressionSyntax.cs` (13 syntax nodes), `HandlingTimes.cs` (6), `Attributes.cs` (9 attributes), `ErrorPolicy.cs` (4). Splitting 26 one-line records into 26 files makes them harder to read as a set.

The rule (Q226): one class to a file, plus its parameters record or exception if it has one, and families of one-line records kept together, a family to a file, in a folder of their own when they're big (`Contracts/Graph.cs`, `Contracts/Pipelines.cs` and so on). The splits are pure moves, so I'd make them in one commit with nothing else in it, which `git diff -M --color-moved` shows as moves.

## 10. Tests and tooling

**T1. A flaky daemon test.** `Meep_daemon_socket_shows_the_socket_in_full_and_the_daemons_id` waits for the socket file to appear (`DaemonTests.cs:302`), but Kestrel makes the file when it binds, before the daemon answers, so under the full suite's load the CLI sometimes connects in between and is told no daemon is running. It passed six times out of six on its own. *Fix:* wait until the daemon answers.

**T2. A test for each fix**, as listed with it, and the plugin unloading test through the daemon (M1).

**T3. Keeping the numbers.** The measuring script could live in `tools/` and run with the tests, failing when a new function passes, say, 60 lines, or a file passes its limit of types, so they don't creep back. Q227: left out.

## The plan

Each phase is a branch of its own, made from `main` and merged when you say, as features are. Every commit builds with warnings as errors and passes the whole suite, and changes to the runtime get the benchmarks before and after. The phases go in order, since later ones move and reshape the files earlier ones fix.

1. **Security and leaks** (branch `refactor-safety`): T1 first, so the suite can be trusted; then S1, S2, S3, S4, S11, M1 and M2; then S5–S10 and M3–M7; then the fam chain benchmark (M8), whose numbers go to you for Q7. A small commit for each finding, each with its test.
2. **File splits** (`refactor-files`): pure moves, by section 9's rule, in one commit with nothing else changed, so the diffs after it are smaller and easier to read.
3. **Duplicates and hierarchies** (`refactor-shared`): D1–D14 with H1–H6, starting with the runtime (D1, D2 with H1, H4, E5) under the benchmarks, then the modules (D3–D6, H2, H3, with `Connections.SendAsync` taking H3's base record), then the rest, including the "value or why not" record and the other records from "Functions only one caller can use".
4. **Big classes and long functions** (`refactor-classes`): section 7's extractions, breaking up the long functions in each class as they move (section 6), largest first: `Subscription`, `MeepDaemon`, `Load`, `LanguageServer`, `Node`, then the smaller ones; then the dispatchers and remaining functions over 40 lines.
5. **Performance** (`refactor-performance`): P1–P4 and E1–E4 and E6–E8, each measured, keeping only what the numbers support.

## Questions

**Q222.** Did I read "functions with high cohesion" as you meant it: groups of functions that share data and calls inside a class that does other things too, to become classes of their own (section 7)? If you meant something else, such as functions too tightly tied to others, I'll measure that instead.

**Answer:** Your interpretation is nearly exactly what I had in mind. The way I interpret it came from my CS teacher who described it as something almost identical, but this was in a time before OOP was common, so it also meant: functions with an excessive number of parameters that can be represented by a reusable record (or class) instead (although that's more of a readability issue, to be honest); functions that require several lines of code to prepare its arguments where that preparation isn't useful to the caller again later (fluent interfaces AKA method chaining are an acceptable alternative); and functions that return tuples or one-off types that only have value to one caller, or that require many lines of code to unpack before it's useful (this does not apply when there are reusable functions available that unpack or convert those return types to something its caller can immediately use, so the caller only needs to nest the function calls. Nesting or fluent-style dot-chaining a function call in the parameters to one or more conversion functions is fine). 
The sense or gestalt of "high cohesion" being anything that makes a function useless to any other caller without a lot of work done inline by the that caller.
Can you add my definition of "high cohesion" to your global memory as something that agrees with and expands on yours? I'd like it to be followed for all projects.

**Q223.** For computed paths in Save and Fetch.File (S2): (a) a `root=` parameter that keeps paths inside a folder; (b) refusing `..`, absolute paths and `~` in what a `{…}` hole produces, unless the module sets something like `any-path=true`; (c) both; or (d) leaving it to pipeline authors, and documenting it. I'd recommend (c): (b) protects pipelines written without thinking of it, and (a) suits one that saves under a folder by design.

**Answer:** I choose C as well.

**Q224.** Public Listens and the daemon's ID (S11): keep answering anyone, as now; answer `Meep-Host` only to loopback and private addresses unless a setting allows more; or add a setting that turns it off? And should Fetch and Send give origins only to hosts known to be Meep through `meep-hosts` or this machine, rather than to any host that says it is? I lean towards private addresses by default, with a setting, but it's your design (Q213–Q216).

**Answer** (agreed in chat, 21 November): The ID itself is a random UUID that reveals nothing about the machine and isn't a credential; what it gives away is that the server runs Meep, and a way to recognise the same daemon across addresses, ports and restarts. So:

- Answer `Meep-Host` only to clients on loopback and private addresses by default, with a setting to answer anyone.
- Give origins only to hosts named in `meep-hosts`, this machine, and private addresses, rather than to any host that answers `Meep-Host`.
- Say in the docs that flow evidence is what the other end claims, not proof, since anyone can send an origin or answer with another daemon's ID.

**Q225.** Shared parameter records (H3): base records, with the verb catalogue fix described, or only a small helper for the checks in each `init`? I'd recommend base records.

**Answer:** Base record.

**Q226.** Files with more than two classes (section 9): at most two everywhere, strictly, or my suggestion: one class to a file, plus its parameters record or exception, and families of one-line records kept together, a family to a file?

**Answer:** That's fine.

**Q227.** Keep the measuring script in the repo as a check (T3), or leave it out?

**Answer:** Leave it out.

**Q228.** Add the fam chain measurements now (M8), so Q7 can be decided with numbers?

**Answer:** Yes.

**Q229.** Should HTTP requests and responses share a base record for their body members (D15)? It changes routing by type, since a module taking the base would take both, so I'd leave them as they are unless you want that.

**Answer:** Leave them as they are.

## After your answers

What each answer changed:

- **Q222 (cohesion):** section 7 keeps the classes to extract, and gains "Functions only one caller can use", which measures your three further signs: too many parameters (25 found, records proposed for four groups), tuples that callers have to take apart (a reusable "value or why not" record for the eight that pair a result with a reason, and a named record for `Pipeline.ReadPeers`), and arguments prepared only for one call (none worth changing so far). Phases 3 and 4 now include them. Your definition is in your global instructions (`~/.claude/CLAUDE.md`, "High cohesion"), so it applies to every project.
- **Q223 (computed paths):** both. S2's fix is a `root=` parameter, and refusing `..`, absolute paths and `~` from `{…}` holes unless the module sets `any-path=true`.
- **Q224 (the daemon's ID):** S11 becomes a fix in phase 1: `Meep-Host` answered only to loopback and private addresses unless a setting allows anyone, origins given only to hosts in `meep-hosts`, this machine and private addresses, and a note in the docs that flow evidence is a claim.
- **Q225 (parameter records):** base records (H3), with the verb catalogue reading documentation from the declaring type.
- **Q226 (files):** section 9's rule as proposed: one class to a file, plus its parameters record or exception, and families of one-line records kept together.
- **Q227 (measuring script):** left out of the repo; T3 is dropped.
- **Q228 (fam chain):** the M8 measurements are added to the end of phase 1, and their numbers come back to you for Q7.
- **Q229 (HTTP messages):** left as they are; D15 is dropped.

## Appendix: functions over 20 lines

Lines of code in each function's body, by file under `src`, longest first.

- `Meep2.Daemon/Subscriptions/Subscription.cs`: Update.Write 213, RunAsync 76, Build 65, Replan 57, Places 50, Host 47, WriteNode 41, Hello 29, Update.WriteSummary 22
- `Meep2.Meeplang/Loading/PipelineLoader.cs`: BuildOptions 63, CheckTypes 60, Define 48, Wire 43, Resolve 42, BindCandidates 37, Prepare 35, FlowTypes 35, ApplyDefaults 34, CopyDistributedBranches 31, ChooseByParameters 31, ResolveInPlugin 30, CheckBuffers 29, EvaluateConstants 24
- `Meep2.Meeplang/Parsing/MeeplangParser.cs`: ParseModuleLine 126, ParseDirective 120, ParseTemplateHeader 73, ParseLine 51, Include 33, ParseInclude 31, DirectiveWords 30, ParseParameterLine 28, ParseConstant 22
- `Meep2.Daemon/MeepDaemon.cs`: StartPipelineAsync 107, RouteAsync 82, EditAsync 62, Tap 50, HandleAsync 40, StartAsync 37, Tap/Stream 28, PrepareSocket 26, EditByKeyAsync 26, Stats 26, Sending 22
- `Meep2.Cli/LanguageServer.cs`: RunAsync 111, ApplyEditsAsync 77, Hover 42, SemanticTokens 39, Answer 38, SetBaseAsync 36
- `Meep2.Meeplang/Parsing/ExpressionParser.cs`: Parse 78, Parse 70, ParsePrefix 59, ParseHole 44, FindHoleEnd 26, ParseCall 21
- `Meep2.Runtime/Pipeline.cs`: ApplyCoreAsync 83, DeliverTo 64, RunCoreAsync 35, Wire 33, EmitLeased 25
- `Meep2.Modules/Send.cs`: HandleAsync 79, HandleAsync 60, HandleAsync 34, HandleAsync 28, HandleAsync 24
- `Meep2.SDK/Numbers/BigDecimal.cs`: Read 66, Divide 37, Write 32, Scientific 32, ToString 27, TryGetDecimal 24
- `Meep2.Meeplang/Editing/SourceEditor.cs`: ApplyToFile 90, Apply 41, Target 35, Set 28, Text.CommentAt 21
- `Meep2.Cli/DaemonCommands.cs`: ClientAsync 58, EditAsync 43, RunAsync 31, InstallAsync 29, TapAsync 28, SocketAsync 23
- `Meep2.Modules/HttpListen.cs`: AcceptAsync 60, AddAsync 39, ProcessRequestAsync 37, RunAsync 29, ParametersChanged 25, WriteAsync 22
- `Meep2.Meeplang/Editing/Highlighter.cs`: Walker.Expression 50, Lexical 41, Tokens 35, Directive 25, Painter.Tokens 22, Walker.Verb 21
- `Meep2.Plugins/PluginInstaller.cs`: InstallNuGetAsync 95, InstallGitHubAsync 37, FindLocal 36, Unzip 23
- `Meep2.Modules/Fetch.cs`: HandleAsync 81, HandleAsync 31, EmitStreamedAsync 28, SendAsync 22, ReadBodyAsync 21
- `Meep2.Meeplang/Compilation/Binder.cs`: Comparison 51, Arithmetic 49, TypedCall 32, TryConvertNumber 23, Unary 22
- `Meep2.Runtime/Internal/InflectionNode.cs`: RunLoopAsync 96, ApplyAsync 48, Choose 26
- `Meep2.Cli/MeepCommand.cs`: RunFileAsync 85, RunAsync 81
- `Meep2.Plugins/Signatures.cs`: Verify 47, Verify 46, PublicKey 44, VerifyCommit 25
- `Meep2.Meeplang/Parsing/Lexer.cs`: ReadDurationUnits 43, Run 38, ReadNumberOrDuration 33, ReadRawString 23, ReadString 21
- `Meep2.Daemon/MessageJson.cs`: WriteValue 112, Write 44
- `Meep2.Meeplang/Compilation/Numbers.cs`: TryConvert 57, Arithmetic 38, TryGetWhole 30, Whole 21
- `Meep2.Cli/KeysCommands.cs`: SignAsync 69, TrustAsync 43, SignPipelinesAsync 26
- `Meep2.Daemon/SocketSession.cs`: Handle 53, AnswerAsync 43, SubscribeAsync 36
- `Meep2.Modules/WebSocketListen.cs`: ServeAsync 63, AcceptAsync 42, RunAsync 27
- `Meep2.Meeplang/Editing/ModuleMatcher.cs`: Match 107, Match/MatchByStructure 21
- `Meep2.Meeplang/Loading/ParameterBinder.cs`: Bind 63, Convert 35, ConvertConstant 25
- `Meep2.Meeplang/Editing/TextChanges.cs`: Myers 47, Between 43, Hunks 24
- `Meep2.Meeplang/Loading/ModuleRegistry.cs`: AddPlugin 65, FindBooks 23, FindWhyNot 23
- `Meep2.Modules/Clients.cs`: EnsureUpAsync 39, OpenAsync 38, ReadAllAsync 34
- `Meep2.Meeplang/Compilation/Dynamic.cs`: Index 38, Compare 28, TryConvert 22, GetMember 21
- `Meep2.Daemon/PipelineSources.cs`: ReadAsync 55, Parse 30, GitIncludes.Read 22
- `Meep2.Modules/TcpListen.cs`: ServeAsync 38, RunAsync 33, ReadAsync 28
- `Meep2.Meeplang/Loading/TemplateExpander.cs`: Expansion.Use 53, Expansion.Bind 45
- `Meep2.Modules/Bayes.cs`: Scores 40, SaveAsync 31, Forget 22
- `Meep2.Meeplang/Editing/DraftEditor.cs`: ApplyAsync 92
- `Meep2.Modules/Classifying.cs`: HandleAsync 37, HandleAsync 30, HandleAsync 25
- `Meep2.Runtime/Internal/SourceNode.cs`: RunAsync 49, SwapAsync 21, RestartAsync 21
- `Meep2.Meeplang/Syntax/ExpressionText.cs`: Write 85
- `Meep2.Modules/JsonPath.cs`: Reader.ReadPath 29, Reader.ReadSelector 26, Reader.ReadQuoted 21
- `Meep2.Meeplang/Loading/ErrorPolicyParser.cs`: ParsePart 74
- `Meep2.Meeplang/Hosting/HostingModules.cs`: HandleAsync 43, HandleAsync 28
- `Meep2.Modules/WebSockets.cs`: CloseAsync 27, ReceiveAsync 23, WriteAllAsync 21
- `Meep2.Daemon/DaemonConfig.cs`: Load 70
- `Meep2.Daemon/DaemonService.cs`: Launchd 43, Systemd 27
- `Meep2.Daemon/DaemonClient.cs`: DaemonClient 23, SendRawAsync 23, ReceiveAsync 22
- `Meep2.Meeplang/Editing/PipelineDraft.cs`: Key 43, SetBase 21
- `Meep2.Cli/Detaching.cs`: StartAsync 63
- `Meep2.Modules/Combining.cs`: HandleAsync 34, FlushAsync 27
- `Meep2.Daemon/Reloading.cs`: InstallAndChangeAsync 33, EditOf 23
- `Meep2.Daemon/HostIdentity.cs`: IdOf 32, MachineId 23
- `Meep2.Plugins/Install.cs`: InstallAllAsync 55
- `Meep2.Meeplang/Compilation/MessageCondition.cs`: Parse 32, Matches 22
- `Meep2.Meeplang/Settings.cs`: Read 52
- `Meep2.Cli/HostSettings.cs`: Parse 47
- `Meep2.Git/PendingBranch.cs`: CommitTo 24, Commit 23
- `Meep2.Plugins/PluginLock.cs`: Check 24, Load 22
- `Meep2.Cli/Pinning.cs`: PinAsync 23, Pin 22
- `Meep2.Meeplang/Editing/LivePipeline.cs`: ChangeAsync 45
- `Meep2.Meeplang/Loading/VerbCatalogue.cs`: Describe 23, Documentation.Plain 21
- `Meep2.Daemon/Graphs.cs`: Module 42
- `Meep2.Git/GitSnapshot.cs`: Fetch 41
- `Meep2.Daemon/Subscriptions/UpdateWriter.cs`: Copy 40
- `Meep2.Git/CommitSigning.cs`: SignAsync 40
- `Meep2.Daemon/Subscriptions/SubscriptionPlan.cs`: Of 32
- `Meep2.Meeplang/Editing/PipelineShape.cs`: Of 32
- `Meep2.Runtime/Internal/Telemetry.cs`: Emitted 29
- `Meep2.Runtime/Internal/TimingRecorder.cs`: Snapshot 28
- `Meep2.Modules/Bodies.cs`: ReadAsync 27
- `Meep2.Meeplang/Loading/SourceLocator.cs`: Symbols.Find 26
- `Meep2.Modules/Enumerate.cs`: HandleAsync 25
- `Meep2.Runtime/Internal/Stamper.cs`: Stamp 25
- `Meep2.Modules/Output.cs`: HandleAsync 24
- `Meep2.Daemon/HostedPipeline.cs`: DetailNow 22
- `Meep2.Daemon/PipelineWatcher.cs`: ReloadAsync 21
