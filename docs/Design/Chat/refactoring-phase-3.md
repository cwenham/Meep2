# Refactoring, phase 3: duplicates and hierarchies

Written 22 November 2026. Phase 3 of the plan in `refactoring.md`: section 5's duplicates (D1–D14), section 8's hierarchies (H1–H6), E5, and the records from "Functions only one caller can use" (Q222). The work is on the branch `refactor-shared`, made from `main` at 26b8d3e, with a commit for each finding, not merged or pushed. This covers what was done for each finding, what the benchmarks say, what I skipped and why, what I found in passing, the decisions I made while building, and questions numbered from Q240.

## Results

- 22 commits: 21 for findings, and this document.
- The build has no warnings, with warnings as errors.
- The whole suite passes: 694 of 694 (679 before, and 15 new tests). `FlowEvidenceTests.A_subscription_says_which_places_were_folded`, one of the three flaky tests phase 1 lists, failed once under the full suite; it passed six times out of six on its own.
- Every commit was built and tested on its own, in a scratch worktree, in order. All 21 build. Two of them had one test fail in that run: E5 had `HostingTests.A_trace_that_isnt_sampled_gets_no_spans_after_its_source`, the known flaky one, and D12 had `WriteBackTests.An_edit_changes_the_running_pipeline_and_commits_its_text_to_pending` (below).
- Every verb's inflections come in the same order as on `main`, checked against the built `Meep2.Modules.dll` after each commit that touched modules (Q238). No inflection type or file moved; the new types in Modules (`ConnectionParameters`, `SendConnectionParameters`, `ConnectionSend`) are abstract and carry no `[Verb]`, so the registry passes over them.

## What was done

The commits are in the order the plan gives, runtime first, except that H3 went before H2 (H2's base class takes H3's base record) and H2 after D10 (its first try was built while D10 was waiting, and I committed them in the order they were ready).

### The runtime

**D1. Emitting.** `Pipeline.Emit`, `EmitLeased` and `EmitTracked` now share one private method, `StampAndDeliver`, which stamps, adds the scope it's given (a lease's, or a tracked emit's outcome) to what the message inherits, counts, traces, taps, delivers, and releases scopes made for new errors. `Emit` passes no scope. A lease is now attached before the message is stamped rather than after, which nothing can see, since stamping doesn't throw for a new message.

**D2, H1. Module contexts.** A new `NodeContext<TNode>` holds the ten members `InflectionContext` and `SourceContext` both forwarded to their node (`Module`, `Stopping`, `PipelineName`, `Shared`, `CountBytes`, `Listening`, `HostId`, `Origin`, `Arrival`, `Destination`). It's generic in the node's type, so each context still reaches its own node's members without a cast. `InflectionContext` overrides `Module`, as it did.

**H4. Tracking scopes.** `Message.Tracking` is now a `TrackingScopes` readonly struct in the SDK rather than an `object?`. It still holds nothing, one scope or an array, so nothing new allocates, and its one enumerator replaces the three switch statements in `ScopeSet` and the one in `Lease.In`. `Union`, `Prepend` and `Append` moved onto the struct, `AddRef`, `Release` and `Flag` became its methods, and `ScopeSet` is gone (`ReleaseAll` moved to `TrackingScope`). Tests: union, prepend, append and release on the struct.

**E5. Routing.** `InflectionNode` keeps a `Route` for each message type in its cache, rather than a slot array, so the last route used is one already made and messages of alternating types no longer allocate one each time.

### The modules

**D5. Responses as messages.** `HttpFetch.MessageFor(url, response, body)` makes an `HttpResponse`, or an `HttpErrorMessage` for a status that isn't a success, for both Fetch.HTTP and Send.HTTP. Test: both kinds, with a body in Latin-1.

**D6. Certificates.** `Certificates.WhyNot` checks a secure Listen's certificate and key for Listen.HTTP and Listen.WebSocket, with the kind of address passed in ("an https://", "a wss://") so each says what it said before. Test: each reason, from both.

**D4, H3. Parameter records (Q225).** Two base records hold what was written out in each:

- `ConnectionParameters`: `connections` and `max-size`, for Listen.TCP, Listen.WebSocket, Send.TCP and Send.WebSocket.
- `SendConnectionParameters`, derived from it: `url`, `text`, `timeout`, `tries` and `retry-delay`, for Send.TCP and Send.WebSocket.

`Connections.SendAsync` takes `SendConnectionParameters` in place of the three values it took from it, so it has seven parameters rather than nine. The verb catalogue now reads an inherited parameter's description from the record that declares it. The descriptions of `connections`, `max-size`, `url` and `text` were written for one record each, so they're now worded for all that share them, naming both message types for `connections` and both kinds of address for `url`. Test: the catalogue documents Send.TCP's `tries` and Listen.TCP's `max-size`, and still lists `url` first.

The framing properties stay in each record: `ping` and `max-queued` (Send.WebSocket and Listen.WebSocket), and `end` and `length` (Send.TCP and Listen.TCP). A record takes one base, and `max-queued` means different things on each side (a Send waits, a Listen drops). Q241 asks whether to go further.

**D3, H2. Send's connection inflections.** A new abstract `ConnectionSend<TParameters>` holds what Send.WebSocket and Send.TCP shared: errors left alone, the address and text worked out for each message, the connections joined, sending with retries, `Address` for `ISendsTo`, and disposing. Each inflection supplies `ClientFor`, which checks its address and joins its connection, or says why not; Send.TCP also supplies `End`. Its members for inflections are `private protected`, so plugins can see the class but not derive from it.

### The rest

**D7. Subscriptions' counts (with `PipelineRead`, Q222).** `Modules`, `Links` and `Bytes` became one generic loop, `CountEach`, given the items and how to read each one's counts; `Timing` and `Places` use the same module enumeration. A `PipelineRead` record carries what they passed each other (the update, the pipeline's name, what's wanted and the rate, its counts, keys, what was sent and the counts read), so `Counts`, `Gone`, `Places`, `Client` and `Peer` take one argument for it rather than five to eight. In `Update.Write`, one `WriteChanges` writes counts by key, for modules and links as for places.

**D8. Pairing unchanged modules.** `ModuleMatcher`'s two passes for unchanged modules are one local function, `PairUnchanged(underSameModule)`, called twice in the same order, with both comments kept.

**D9. Finding a module's contracts.** `ModuleRegistration.FindContract(moduleType, parametersType, typeof(IX<>))` finds the generic interface a module implements for its parameters, for `WhyNot`, `MostAtOnce` and `ParametersChanged`.

**D10. Running programs (with M6).** `ProgramRunner.RunAsync` in the SDK runs a program with its arguments as a list, optionally writing to its input and setting variables, reads what it says into a `ProgramResult`, and kills it, with what it started, when cancelled. `DaemonService`, `meep sign` and commit signing use it; the daemon's `Programs` class is gone. `Meep2.Git` now references the SDK, as every other project does (Q240). `KeysCommands.SshKeygenAsync`, one of Q222's tuples, returns `ProgramResult`.

**D11. Home and the SSH key.** `HomeFolder.Expand` reads `~`, `~/` and `~\` as the home folder, and `HomeFolder.SshKey` finds the default key, for `meep sign`, commit signing, Save and Fetch's paths, `daemon.meep`'s files and `#include`. Before, `meep sign` didn't take `~` alone, and only paths took `~\`. Test: what it expands, and what it leaves.

**D12. One HTTP client.** `MeepHttp.Client` in Meep2.Plugins is shared by plugin installs, `meep keys trust github:` and pipelines from URLs, and says `Meep2/1.0` in its User-Agent. Pipelines from URLs used a client of their own with a 30-second limit and no User-Agent; the limit is now set for each request, and covers reading the body as well as the headers. Fetch and Send keep their own client, since they follow redirects themselves. Test: a pipeline from a URL is asked for as Meep.

**D13. Writing durations.** `DurationText.Write` in the SDK writes durations as Meeplang reads them, in whole units (`250ms`, `1m40s`, `1h30m`), or for one with part of a millisecond in the largest unit that fits (`1.5ms`). `Durations.Format`, the catalogue's defaults, a module's restart limit, Zip's "within" and Fetch's and Send's timeouts all use it. Some messages read differently (Q242): a Send.HTTP timeout says "1m40s" rather than "100s", and a restart limit of 90 seconds says "1m30s" rather than "1.5m". Tests: whole and part units, and that what's written reads back.

**D14. Query strings.** The WebSocket API reads a request's query with ASP.NET's `QueryHelpers`, as the HTTP API does, through one `ApiRequest.QueryOf`. They differed for a name given twice (the WebSocket kept the last value, HTTP joined them with a comma) and in matching names' case. Test: `+`, escapes, a repeated name and an empty value.

**H5. Subscriptions' words.** An `UpdateSection` enum names what a subscription asks for and an update carries (list, host, runtime, totals, modules, links, timing, bytes, arrivals and destinations), with `SubscriptionPlan.NameOf` giving each its name in plans and updates, so a misspelt section is a compile error. Totals are reached by named positions (`Sourced`, `BytesReceived`, `BytesSent`, and `Emitted` among a module's counts) rather than 12, 13 and 14. What's sent is the same. Test: every section's name, and every named position.

**H6.** Skipped; see below.

**Q222's records.**

- **"A value, or why not":** `ValueOrWhyNot<T>`, in the SDK, with `Of`, `Not`, `ValueOrThrow`, `ValueOr`, `TryGet`, `Then` and `ThenAsync`, so a caller can nest the call. `PipelineDraft.SetBase`, `CommitSigning.KeyFor` and `CommitSigning.SignAsync` return it. The daemon and `meep lsp` now write `draft.SetBase(…).ValueOrThrow(problem => new DaemonException(500, problem))`, and committing an edit signs with `KeyFor(repository).ThenAsync(key => SignAsync(content, key))`.
- **A value with its diagnostics:** three of the eight return diagnostics alongside a value, warnings included when there is one, so they aren't "or". `WithDiagnostics<T>` in Meeplang carries them, with `Errors` and `ValueOrThrow`, for `PipelineLoader.Define`, `LivePipeline.Load`, `LanguageServer.RegistryForAsync`, and `RegistryLookup`, the delegate `DraftEditor` takes registries through. It's a positional record, so callers that want both still write `var (live, diagnostics) = LivePipeline.Load(…)`.
- **`CompiledComputed.ProblemWith`** returns an `ExpressionProblem` record rather than a nullable tuple.
- **`KeysCommands.SshKeygenAsync`** returns `ProgramResult` (D10).
- **`Pipeline.ReadPeers`** returns `ModulePeers` records, beside `PeerCount` in `HandlingTimes.cs`.
- **`PipelineRead`** for the subscription's parts (D7), and `Connections.SendAsync` taking H3's base record.
- **`CliContext`** carries the daemon client, output, error and Ctrl+C for `meep start`, `edit`, `list`, `stop`, `stats`, `tap` and `inject`, which took three or four of them each.

## Benchmarks

Run on 22 November, `main` (26b8d3e, in a scratch worktree) first, then the branch at E5 (f113c15, the last runtime commit, in a second scratch worktree), one after the other on this Mac, with nothing else building. BenchmarkDotNet's short job, a message's time and allocation end to end:

| Benchmark | `main` | Branch |
|---|---|---|
| Chain, 1 step, same message | 1.41 µs, 109 B | 1.51 µs, 109 B |
| Chain, 1 step, new message | 1.87 µs, 218 B | 2.03 µs, 218 B |
| Chain, 4 steps, same message | 1.80 µs, 128 B | 1.97 µs, 122 B |
| Chain, 4 steps, new message | 3.17 µs, 481 B | 2.99 µs, 479 B |
| Leases, none | 1.87 µs, 122 B | 2.04 µs, 124 B |
| Leases, shared | 2.54 µs, 156 B | 2.65 µs, 156 B |
| Leases, each message | 3.23 µs, 573 B | 3.49 µs, 577 B |
| Observing nothing | 2.45 µs, 218 B | 1.86 µs, 218 B |
| Observing the histogram | 2.07 µs, 218 B | 2.25 µs, 220 B |
| Observing with a tap | 3.39 µs, 219 B | 3.27 µs, 218 B |
| Observing, sampled | 2.92 µs, 800 B | 2.80 µs, 795 B |
| Observing, tracing | 15.8 µs, 2,433 B | 8.05 µs, 2,482 B |

Allocation is the same within a few bytes everywhere. Times move both ways, by up to 10% in the rows that do the most work and far more in the tracing row, and the short job's error bars are wider than that (±0.3 to ±1.8 µs for the chains, ±47 µs for tracing on `main`).

So I ran the chains and leases again with BenchmarkDotNet's medium job (more iterations, error bars of ±15 to ±100 ns), `main` first, then the branch at its last commit, which has every runtime change:

| Benchmark | `main` | Branch |
|---|---|---|
| Chain, 1 step, same message | 896 ns, 123 B | 896 ns, 109 B |
| Chain, 1 step, new message | 1,224 ns, 218 B | 1,250 ns, 218 B |
| Chain, 4 steps, same message | 1,341 ns, 128 B | 1,314 ns, 126 B |
| Chain, 4 steps, new message | 1,955 ns, 478 B | 1,874 ns, 479 B |
| Leases, none | 1,330 ns, 126 B | 1,282 ns, 130 B |
| Leases, shared | 1,785 ns, 154 B | 1,693 ns, 156 B |
| Leases, each message | 2,197 ns, 583 B | 2,092 ns, 583 B |

Every row is within its error bars or a little faster on the branch, and allocation is the same. I read this as no change from D1, H1, H4 and E5. E5 shows only when message types alternate, which no benchmark does.

**Flows** (`flows --seconds 10`, four Fetches to a Listen over loopback):

| Phase | `main` | Branch |
|---|---|---|
| No tracing | 3,258 requests/s, 8,647 B a request | 3,177 requests/s, 8,648 B |
| Tracing, one in ten recorded | 3,265 requests/s, 10,047 B | 3,254 requests/s, 10,057 B |
| No tracing, again | 3,268 requests/s, 8,639 B | 3,277 requests/s, 8,647 B |

**FlowMicro**: the same allocation for each of its five measurements, and times within 10% each way (reading an origin from baggage 188 against 203 ns, a request message with trace headers 350 against 319 ns).

**Subscriptions** (`subscriptions --seconds 10`, `main` then the branch's last commit, for D7 and H5): every kind of update is the same size to within a few bytes, except those with timing, whose histograms differ from run to run by under 0.5%, and each allocates within 2% (1,106 KB against 1,124 KB for a CBOR update of what changed, the largest difference). The time to make each moves by up to 20% either way, within the run's noise (a CBOR update of what changed 1.67 against 2.05 ms; timing only 2.84 against 2.62 ms). The live phases' CPU over a run with no client moves between 3 and 45 ms a second on both, by about as much as their baselines move.

## Skipped, and why

- **H6, the two `AInflection` types in one file.** Phase 2 kept `AInflection` and `AInflection<TIn>` together, as a type and its generic form, and `IRouteMessages`, the third type, already has its own file. You accepted that under Q239, so there was nothing left to do.
- **`PendingBranch.CommitTo`'s seven parameters.** The audit expected it to shrink when it becomes asynchronous (P1), which is phase 5. It does read more simply now, through `ValueOrWhyNot`.
- **The framing properties** (`ping`, `max-queued`, `end`, `length`), as above, and Q241.
- **Fetch's client** stays its own, as D12 says.

## Found in passing

- **`HttpFetch.Describe` wrote timeouts in the current culture** (`{time.TotalSeconds:0.###}`), so on a machine set to, say, German it said "1,5s", which Meeplang can't read. D13 fixed it in passing.
- **`Durations.Format` dropped part of a millisecond**: 1.5 ms was written "1ms". D13 writes "1.5ms".
- **A pipeline from a URL that's too slow fails with a bare cancellation**, not a message saying it took longer than 30 seconds. That's as before D12; Q243 asks whether to say so.
- **The verb catalogue now lists inherited parameters after a record's own**, since that's the order reflection gives them, so Send.TCP shows `end` and `length` before `timeout`. `url` still comes first, as the positional one. It's only how the list is shown.
- **`WriteBackTests.An_edit_changes_the_running_pipeline_and_commits_its_text_to_pending` failed twice in about a dozen full runs at the D12 commit**, in under a second, and passed five times out of five on its own there, eight more full runs there, and ten full runs at the branch's last commit. Neither failure printed its message, since my script kept only the test's name, so I can't say which assertion it was. Nothing in D12 touches write-back or signing; D10, two commits earlier, changed how ssh-keygen is run for signing, and it passed every run from there to D11. I've added it here as a fourth test to watch, not a known cause.
- **`MeepDaemon.EditByKeyAsync`** had a local copy of the registry lookup that `meep lsp` has; both now return `WithDiagnostics`, but they're still two. Phase 4's `DaemonApi` is the place to share them.

## Decisions I made while building

- **Shared helpers live in the SDK** (`ProgramRunner`, `ProgramResult`, `HomeFolder`, `DurationText`, `ValueOrWhyNot`, `TrackingScopes`), since it's the one project every other references, as phase 1 did with `PrivateFolders`. `TrackingScopes` is internal; the rest are public, so plugins can use them, but that makes them part of the SDK's surface (Q240).
- **`WithDiagnostics<T>` is a second type**, in Meeplang beside `Diagnostic`, rather than `ValueOrWhyNot<T>` stretched to carry a list, since a value can come with warnings.
- **`MeepHttp` lives in Meep2.Plugins**, which the CLI and the daemon both reference and which installs plugins with it.
- **`ConnectionSend` is public and abstract**, as its derived classes are public; it can't be derived from outside Modules.
- **`Pipeline.ReadPeers` changed in the runtime** without its own benchmark run, since it's read by subscriptions, not on a message's path.
- **The subscription's section names stay in one array beside `NameOf`**, rather than coming from the enum's names lower-cased, so renaming a member can't change what's sent.

## Questions

**Q240.** The shared helpers are public in the SDK, which plugins are written against and which goal 9 will package: `ProgramRunner`, `HomeFolder`, `DurationText` and `ValueOrWhyNot` (and `PrivateFolders` from phase 1). Should they stay there, as things plugins may use too, or move to an internal assembly of Meep's own (say `Meep2.Shared`, referenced by every project, with nothing in it promised to plugins)? I lean towards `Meep2.Shared`, before the SDK is packaged, keeping in the SDK only what a module needs.

**Q241.** Should Send and Listen share framing too: a base record for WebSocket framing (`ping`, `max-queued`) and one for TCP framing (`end`, `length`)? A record takes one base, so it would mean framing records derived from `ConnectionParameters`, and Send's retries (`timeout`, `tries`, `retry-delay`) written out twice again, or an interface for those. `max-queued` also means "wait" on a Send and "drop" on a Listen. I'd leave them as they are.

**Q242.** Durations in messages are now written as Meeplang writes them: "didn't answer within 1m40s" rather than "100s", and "failed 3 times within 1m30s" rather than "1.5m". Is that wording what you want, or should messages keep a single unit?

**Q243.** A pipeline or include from a URL that takes longer than 30 seconds fails with a cancellation that doesn't say why. Should it say "… didn't arrive within 30s", as Fetch does? It's a small change, which I'd make in phase 5 with P1, since that changes how includes are read.
