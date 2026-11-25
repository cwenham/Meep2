# Permissions, phase 4: `@permission` gates

Written 24 November 2026. Phase 4 of the plan in `permissions.md`: the `@permission` gates, verbs that require permissions, checks in code, a pipeline's own `#permission` directives, gates in `meep lsp` and the graph, and `Listen.HTTP proof=required`. The work is on the branch `permissions-phase-4`, made from `main` at f6fa47a, not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q289, for you to answer inline.

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 880 of 880 (856 before, on `main`, and 24 new tests, counting each case of a test with arguments). Two full runs of the finished work, both 880 of 880. Earlier, while building, one full run had nine tests time out and another had one timing-based test fail; none of them is one of the three known flaky tests, and all passed on their own and in the runs after (see "Found in passing").
- No test touches the real `~/.meep`: each uses a temporary home folder.
- A module without a gate costs what it did: no difference beyond noise on this Mac, and about 3% in a chain of five modules on strawberry.local, which matches the 8 bytes more on each message. A gate costs from about 20 ns, for an answer already kept on the message, to about 0.5 µs on this Mac and 0.65 µs on the Pi for a new message one generation below its verification, plus 10–25 ns (Mac) or about 50 ns (Pi) for each generation further up it has to walk. Details below.
- The Pi's installed daemon wasn't touched; the benchmark copy is deleted.

## What was built

### The modifier

`@permission=Name` on any module, read in `OptionsBuilder` into `NodeOptions.Permissions`. It can be written more than once, and all are needed. A name is written as it is (`ReadNotes`, `meep.files.read`), in quotes when it has a scope (`@permission="meep.files.write:~/notes/**"`, since `:~/` isn't Meeplang), or as a constant holding one. Anything else is a load error saying how to write it.

### The check

A gate checks a message before delivering it, in `Pipeline.DeliverTo`, on the thread that delivers it, after the checks for expiry and type and before the message goes into the module's buffer:

- **What counts.** A `VerifiedMessage` of a credential that is genuine (`IsGenuine`, so not a copy) and was emitted (`IsStamped`), which only the runtime's `Verify` does.
- **Every line (Q261).** Each line of the message's family, back to a message with no parents, must pass through such a verification that passed, hasn't expired, and holds the permission, conferred by an authority the permission allows, with everything it requires. This is phase 3's `PermissionLines`, now shared by `Verify.Permission` and the gates.
- **What's needed.** For each name, the trust store's definition, narrowed by the pipeline's own `#permission` directive if it has one, and by the gate's own scope if it names one. A name that isn't defined, or that the pipeline widens, is held by nothing, and every message is refused saying why. These requirements are worked out once for each trust store and each set of the pipeline's options (`Gatekeeper`, `Gate`).
- **Revocation (Q259).** A verification made before the store last took something away is looked at again: its credential and grants are checked for revocation, and its authorities looked up as the store is now.
- **Only gated modules pay.** A module without a gate costs one more `bool` test for each message delivered to it.

### Answers kept on messages

A gate keeps its answer on the message (`Message.GateAnswers`, an internal field), and on the messages where the walk joined and the verifications it reached. So a second gate needing the same permission, for the same message or one made from it, stops at the first kept answer rather than walking on, and the work of finding what a credential holds is done once for each verification. A kept yes lapses when the verification it relied on does. A reloaded store or a changed pipeline makes new requirements, so nothing kept for the old ones is used. At most eight answers are kept on a message, for eight permissions.

The walk follows single parents in a loop and only recurses at joins, so a long family costs no stack. It gives up, refusing, after ten million messages or a thousand nested joins.

### Refusals

- **A refused message** becomes a `PermissionDenied` error from the module (Q258): an `ErrorMessage` with `Gate`, `Needed` (the permissions it lacked) and `Reasons` (why, one for each), and the refused message as its parent. Its text is `Save #3 needs WriteNotes, which this message doesn't have.` followed by the reasons. It follows the error policy as any error does, so an `@errors` module can answer it with a 403. Only the runtime can make one.
- **It goes no further.** A gated passthrough module or `Distribute` doesn't pass a refused message on, since the check comes before delivery.
- **A refused error** passes the module by, as an error a module doesn't take does, still looking for a module that takes errors.
- **Counted.** `ModuleStats.Denied`, the `denied` count in the daemon's subscriptions (added at the end of each module's counts), a `DENIED` column in `meep stats` when any module has refused something, and `meep.module.messages` with `meep.event = denied` in OpenTelemetry. Each refusal is also an event, `meep.permission.denied`, on the span of the module that emitted the message, with the gate, the message's ID, the permissions and the reasons: the audit trail the plan's "verification as audit" idea asked for.
- **Logged, never stopping.** A refusal nothing handles is logged as an unhandled error, but doesn't count towards `stop-pipeline`, so a stream of unwelcome requests can't stop a pipeline.

### Sources

`@permission` on a source, or a source verb that requires a permission, means the pipeline must hold it for the source to start (Q252). Pipelines don't carry credentials of their own until phase 6, so for now such a source can't start, and the pipeline fails to start saying so: `Numbers #1 can't start: it needs ReadNotes, which the pipeline must hold for its sources to start, and pipelines don't carry credentials of their own yet.`

### Permissions verbs require

- **`[RequiresPermission("Name")]`** (SDK) on an inflection's class: a permission it always needs, which no pipeline can remove (Q263). `@permission` adds to it, and both are needed. It's on the class rather than the verb, so `Authorise.Permission` and `Authorise.Certificate` can need different ones in phase 5. The registry reads it into `ModuleRegistration.RequiredPermissions`, and a name that isn't a permission's is an error when the verb is registered.
- **`context.Demand(permission, message, target)`** and **`context.Holds(…)`** on `ModuleContext`, for checks finer than a gate, such as `Fetch.File` only outside its `root`. `target` is what the module is about to touch, which a scoped permission must match (`PermissionName.Allows`). `Demand` throws `PermissionDeniedException`; the runtime catches it and emits a `PermissionDenied` in place of the message, counted as denied, without restarting the module or counting it towards `stop-branch`.

### A pipeline's own directives

`#permission` lines, read with phase 2's `Permission.ParseDirective`, into `PipelineSyntax.Permissions` and then `PipelineOptions.Permissions`. One for each name, at the left margin of the pipeline's own file, before its modules, as `#plugin` is. They narrow the definitions for the pipeline's gates, and now for its `Verify.Permission` too, so the two agree. Whether a directive is in fact no wider than the definition can only be known against the store, so it's checked when a gate first needs it, and a widening one makes the gate refuse everything, saying how it widens.

### Tools

- **`meep lsp`.** Hovering over `@permission` says what a gate does and lists what this line's module needs: its `@permission`s and what its verb requires. Hovering over a verb says what each inflection always needs. `@permission` and `#permission` are offered as completions.
- **The graph.** `GraphModule.Permissions` lists what a module needs, its `@permission`s and then its verb's, or is null if it isn't gated, so a drawing can put a lock on it. `ModuleShape` and `LoadedModule` carry it for the LSP's graph and the daemon's.
- **The verb catalogue.** `VerbEntry.Requires`.

### `Listen.HTTP proof=required`

A new parameter, `proof`, `optional` (the default) or `required` (Q280 A). With `required`, a request without `Signature-Input`, `Signature` and `Meep-Credential` is answered with 401 before it reaches the pipeline: `This server needs requests signed with a Meep credential, and this one has no Signature-Input, Signature or Meep-Credential.`, naming the ones missing. It's counted against the client as a rejected request, as other 4xx replies are. `Verify` still does the real check.

### Documentation

- `docs/verbs.md`: a "Gates: `@permission`" section after `Verify`, with `#permission` directives, and `proof` on Listen.
- `docs/writing-verbs.md`: a Permissions section on `[RequiresPermission]`, `Demand` and `PermissionDenied`, and `Demand` in the context's table.
- `docs/troubleshooting.md`: "Gates and refusals": how to read a refusal, each reason a gate gives and what to do about it, a gated source that won't start, and a 401 from `proof=required`.

### The tests

`GateTests` (22 with their cases), using a `GateLab` (a store with ReadNotes, WriteNotes and Other defined, and alice's bearer credential holding some of them in a file), and test verbs `NeedsNotes`, `NotesSource`, `WritesNote`, `Derive` and `Glance`:

- A verified message passes a gate and an anonymous one is refused, with its reasons, parent and text, an `@errors` branch taking it, and the `denied` count.
- With no store, 40 refusals are logged and don't stop the pipeline. An undefined permission is held by nothing.
- Several `@permission`s are all needed, with the reasons for each missing one; scopes must be covered.
- A `Zip` of a verified line and an anonymous one is refused; of two verified lines, passed.
- A credential revoked and the store reloaded between `Verify` and the gate is refused.
- Answers are kept on the message and on the verification; a grandchild's walk stops at a kept answer; a lapsed yes is worked out again; a reloaded store makes new requirements.
- A refused error passes a gate by; a gated passthrough passes nothing refused on.
- A refusal is an event on the emitting module's span.
- A pipeline's directive narrows its gates and its `Verify.Permission`, and a widening one is refused; directive mistakes (five cases); directives and modifiers read into the options, and bad modifiers reported.
- A gated source, by `@permission` or by its verb, stops its pipeline starting (two cases).
- A verb that requires a permission keeps it, `@permission` adds to it, the catalogue shows it, and a bad name can't be registered.
- `Demand` refuses a file outside the credential's scope without counting a failure or a restart.
- `Listen proof=required` answers 401 naming what's missing, and lets in a request with all three headers.
- The example in `docs/verbs.md`: a signed request with the permission is answered, and one signed by another key gets the 403 from the `@errors` branch.

Also: `GraphTests` has a gated module's `Permissions` in the graph, and `LanguageServerTests` the completion and the hover.

Two existing tests changed: `ParserTests` for the new list of directives in an error, and phase 3's `Every_line_of_a_family_must_carry_the_permission…`, which made a `VerifiedMessage` by hand that was never emitted. It now checks that such a one doesn't count, then stamps it.

## Performance

Measured on this Mac (Intel Xeon W-2140B, 16 threads, x64) and on strawberry.local (Raspberry Pi 4, 4 cores, arm64), each against `main` built in a scratch worktree, alternating, since the Mac's speed varies. `GateCost` is a new stopwatch benchmark, `dotnet run -c Release -- gates`, quick enough for the Pi; for `main`, a copy of its pipeline part ran in the scratch worktree only.

**Without gates.** BenchmarkDotNet's `ChainBenchmarks` on this Mac, `main` then the branch, two rounds each, per message:

| Steps, new message each step | `main` | branch |
|---|---|---|
| 1, no | 1.76, 1.70 µs, 130/110 B | 1.75, 1.55 µs, 124/133 B |
| 1, yes | 2.62, 2.41 µs, 218 B | 2.51, 2.22 µs, 234 B |
| 4, no | 2.38, 1.93 µs, 119/122 B | 2.08, 2.12 µs, 127/133 B |
| 4, yes | 3.52, 3.27 µs, 475/499 B | 3.51, 3.20 µs, 518/544 B |

On the Pi, `GateCost`'s chain of a source and four steps that each make a new message, 100,000 messages, four rounds each: `main` 4.88, 4.72, 5.07 and 4.62 µs (mean 4.82), the branch 4.94, 5.02, 5.17 and 4.77 µs (mean 4.98), so about 3% slower, with 496 B a message against 544 B. The extra bytes are the new field, 8 bytes on each of the five messages each one makes, and the time is likely the collector's for them (Q297).

**A gate's check**, of one message, on its own:

| | Mac | Pi |
|---|---|---|
| its answer already kept on it | 22–27 ns | 62 ns |
| a new message, 1 generation below the verification | 490–570 ns | 640–675 ns |
| 10 generations below | 310–490 ns | 1.1 µs |
| 100 generations below | 1.2–2.8 µs | 5.9–6.1 µs |
| 1,000 generations below | 10–24 µs | 52 µs |
| the first check against a new verification | 1.1–1.2 µs | 4.2–4.7 µs |

So walking fam costs about 10–25 ns a generation on this Mac and 50 ns on the Pi, and the first look at a verification, finding what its credential holds as the store is now, about 1 µs and 4.5 µs. A long family is walked once: each gate keeps its answer, and the next gate stops there.

**A gated pipeline**, the same chain with a gate on the last module or on all five, every message descending from one verification:

| | Mac | Pi |
|---|---|---|
| no gate | 3.1–3.3 µs, 514–520 B | 5.0–5.2 µs, 521–531 B |
| a gate on the last module | 3.9–4.4 µs, 647–649 B | 6.6–7.2 µs, 660–664 B |
| a gate on every module | 5.4–6.4 µs, 1,118 B | 8.7–8.9 µs, 1,137–1,139 B |

About 0.5–1 µs and 130 B for each gate a message passes on this Mac, and about 0.75 µs on the Pi: the kept answer (an object of 40 bytes) and the walk's own object, for each message checked.

## Decisions I made while building

- **Gates check before delivering, on the delivering thread**, not in the module's loop. That's the only place a passthrough module or `Distribute` can be stopped from passing a refused message on, and it keeps a refused message out of the module's buffer. The cost falls on the thread of the module upstream.
- **A message `@bypass` sends past a module isn't checked**, since it doesn't reach the module.
- **Only emitted verifications count**, not just genuine ones. The plan asked for "a `Verify` of the same pipeline". Nothing carries a message from one pipeline to another in this process (injection refuses verifications), so emitted is the same thing; checking that the emitter belongs to this pipeline would also need to remember modules a live change has removed.
- **A refused error passes the module by** rather than becoming a `PermissionDenied`. Otherwise each gate below would wrap the last one's refusal, and an error from an anonymous line could never reach an error handler that's gated.
- **Refusals don't count towards `stop-pipeline`** (Q291). They're a gate doing its job; counting them would let anyone who can send a request stop a pipeline that doesn't handle them.
- **A permission the store doesn't define is held by nothing** (Q289). Failing closed means a typo in `@permission` refuses everything, which is visible at once, rather than letting through whatever happens to carry the misspelt name.
- **A gated source stops its pipeline starting** until pipelines have credentials (Q290).
- **`[RequiresPermission]` is on the inflection's class.** A message that more than one inflection takes by its type needs what each requires, since a verb like `Verify` only chooses an inflection for each message in the module's own loop, after the gate (Q293).
- **Answers are kept in a field on every message**, 8 bytes each, rather than in a `ConditionalWeakTable`, which would cost only gated pipelines but costs more for each look-up and gives the collector a handle to follow for every message checked (Q297). Measured, ungated runs cost the same on this Mac and about 3% more on the Pi (above).
- **Where answers are kept:** on the message checked, at joins, and on verifications. Not on every message walked through, which would allocate for each, nor only on the message checked, which would leave a second gate walking back to the verification.
- **`Demand` takes a target** for scoped permissions, rather than the target being written into the name's scope, since a path can contain `*` or `?`, which a scope reads as wildcards.
- **`Demand` throws, and the runtime catches it**, so a module calls one line and needs no error handling of its own. The exception isn't caught as a failure: no restart, no `stop-branch`.
- **Directives only in the pipeline's own file** (Q295), once for each name, before the modules, as `#plugin`. They apply to `Verify.Permission` as well as gates, so asking ahead of time gives the gate's answer.
- **Whether a directive widens a definition is checked at the gate**, not at load, since the loader has no trust store and the store can change while a pipeline runs.
- **`proof=` is an enum, `optional` or `required`**, with room for a `connection` later (phase 7's challenge). A 401 from it isn't an error in the pipeline, since the request never reached it, and isn't a `denied` count, since `ModuleContext` has no way for a source to count one (Q296).
- **`ModuleStats.Denied` is the last field**, with a default, so code that makes one positionally doesn't change. In the daemon's subscriptions it's the last of each module's counts, which moves `sourced`, `bytesReceived` and `bytesSent` along by one among the totals (Q294).
- **`meep stats` shows `DENIED` only when something was refused**, so the table of a pipeline without gates reads as before.

## Found in passing

- **A refusal right after a source has no span to put its event on.** A source's span is made and ended as it emits, before the message is delivered, so `Activity.Current` is empty when the gate refuses. A gate further down, or after any module that handles messages, records it (Q298).
- **The walk was slow at first**, about 100 ns a generation on this Mac, from a stack of frames for every message; walking single parents in a loop brought it to about 10–25 ns. Reading the clock for every check cost too; it's now read only when a verification's grants are looked at.
- **`LinkCountTests.Zips_numbered_inputs_are_counted_apart`** failed once under the full suite and passed eight times out of eight on its own. It pairs two `Interval 1ms` sources with `within=50ms`, so it depends on timing; no module in it is gated.
- **One full run timed out in nine tests** that start processes, fetch from the network or start servers (`Detach…`, standard input, plugins from NuGet and caches, a slow URL, Listen over https), each at about 10 s. All nine passed on their own, and in the two full runs after. They aren't among the three known flaky tests, and none involves a gate.
- **Passthrough modules can't use `Demand` to stop a message**, as it has gone on before they handle it; the guide says to use `[RequiresPermission]` or `@permission` there.
- **`TrustStoreSource` can be subclassed** outside `Meep2.Security`, which the tests use to swap a store mid-run. That's fine for tests; a plugin could hand a pipeline a store of its own the same way, but plugins are trusted code already.
- **`Send` answers an error with `error-status` and `error-reply`**, and ignores `status` and `text` for one, so my first draft of the docs example, `Send status=403 text="{msg.Error}" @errors`, answered refusals with 500. The example in `verbs.md` now uses `error-status=403`, and a test runs it. Easy to get wrong when answering refusals; a load warning for `status=` on an `@errors` Send might help.
- **The `DENIED` column in `meep stats` has no test of its own**; the count behind it is tested through `ModuleStats`.
- **The Mac here is an Intel Xeon W-2140B (x64)**, not Apple silicon, which matters when comparing its numbers with the Pi's.

## Questions

**Q289.** A gate needing a permission that `permissions.meep` doesn't define refuses every message: it's held by nothing. The other way is to treat an undefined name as defined with no requirements, conferred by any trusted authority, so `@permission=ReadNotes` works with a credential carrying `ReadNotes` and no definitions at all. I chose refusing: it fails closed, and `meep lsp` could warn about an undefined name once it can read the store. Keep it?

**Answer:** No, the pipeline author should be able to name a new permission in the gate even if it isn't defined, but `meep lsp` should still warn. My apologies if this contradicted anything I said earlier.

**Q290.** A gated source stops its pipeline starting, since pipelines don't hold credentials until phase 6. Keep that, or let the pipeline start with a warning that the gate isn't enforced yet? I'd keep it: a gate that isn't enforced is worse than none.

**Answer:** Keep it.

**Q291.** Refusals nothing handles are logged but don't count towards `stop-pipeline`, so unwelcome requests can't stop a pipeline. Should they have a limit of their own, such as a notice when a gate refuses more than some share of what reaches it, as a sign of a misconfigured credential or an attack? I'd leave it to the `denied` count and telemetry for now.

**Answer:** Leave it to telemetry.

**Q292.** An error refused at a gate passes the module by. The alternative is to drop it, which would hide errors from anonymous lines from the gated module's `@errors` branch, or wrap it in a `PermissionDenied`, which piles refusals up at each gate below. Keep passing it by?

**Answer:** Yes, pass them on.

**Q293.** A message that more than one of a verb's inflections takes by its type must hold what each of them requires, since the inflection is chosen after the gate. For `Authorise` in phase 5, where `Authorise.Permission` and `Authorise.Certificate` will need `AuthorisePermission` and `AuthoriseCertificate`, that's only right if they take different message types. If they're chosen for each message, as `Verify`'s are, a message would need both. Should I move the check for what an inflection requires into the module's loop, after it chooses, for verbs chosen for each message? It costs a second check site, and a passthrough couldn't be stopped there.

*Agreed in chat, 25 November, for phase 5.* Not by moving the check. Instead, a new `Parse` verb turns text into a `Credential`, `Certificate` or `Permission` before it reaches the verbs that need permissions, so their inflections can be chosen by type and a message meets only one inflection's gate:

- **`Parse`** reads text, from `from=` or the message's own, and emits the typed message. Text that's none of the three becomes an error saying what it found.
- **`Authorise` and `Revoke` take typed messages only**, so `Authorise.Permission` and `Authorise.Certificate` take different types and never share a gate.
- **`Verify from=` keeps working on text**, as it does now. It needs no permission of its own, so its choosing for each message does no harm at a gate.
- **One reader, in the SDK.** What `VerifySubject` does now, telling the three apart by the text's form and reading them, moves into a public function in the SDK, which `Verify` and `Parse` both call, so the two can't disagree. It returns the typed message, or throws a `FormatException` saying what it found. It says "credential" only for text that really is an SSH certificate; `Verify` treats anything else as a malformed credential, as now, and `Parse` as an error.
- **`Read` stays as it is.** `Read` then `Verify bearer=true` already works on text, and `Read` then `Parse` covers a file going to `Authorise`. If that extra line becomes a nuisance, `Read` can call the same SDK function, rather than deciding by the file's extension, which would be a second rule.
- **Proposed, not yet agreed:** the registry refuses a verb whose inflections are chosen for each message (`[ChosenForEachMessage]`) if they require different permissions, so a plugin can't meet this problem either.

**Answer:** Go with the proposal above, including refusing verbs with inflections that require different permissions.

**Q294.** The daemon's subscriptions now send `denied` as the last of each module's counts, which moves `sourced`, `bytesReceived` and `bytesSent` one place along among the totals. A client that reads the names in `hello`, as the protocol intends, won't notice. Does Synesthesia read them by position anywhere? If so, it needs telling, or `denied` could go after `bytesSent` among the totals instead.

**Answer:** This will need to be put into handover notes for a session to update Synesthesia, which will need to be updated for all the other changes, too.

**Q295.** `#permission` directives may only be in the pipeline's own file. Allowing them in included files would let several pipelines share a policy, and narrowing is always safe, but a pipeline's restrictions would then be partly somewhere else. Allow them, or keep them in the pipeline's own file?

**Answer:** Keep in own file.

**Q296.** A 401 from `Listen proof=required` is counted against the client as rejected, but not in the Listen's own `denied` count, as sources have no way to count one. Add a `CountDenied()` to `ModuleContext` so the Listen's refusals show beside the gates', or leave them as rejected requests?

**Answer:** Add `CountDenied()`

**Q297.** Answers are kept in an 8-byte field on every message, gated or not. Keep the field, or move them to a weak table that costs only gated pipelines but is slower to look in? I'd keep the field: on this Mac ungated pipelines show no difference, and on the Pi about 3% in a chain where every module makes a new message, which is the worst case.

**Answer:** Keep it.

**Q298.** A refusal right after a source has no span, so its `meep.permission.denied` event is lost. Should the gate start a short span of its own then, as a source's emit does, so every refusal is in the trace?

*Agreed in chat, 25 November, for phase 5.* Every refusal gets a short span of its own, not only those after a source, so refusals have one shape and are easy to search for:

- It's named after the gated module, as in `Save.denied`, with the span the message came from as its parent, as a handling span has, and ends at once.
- It has the usual tags, plus the permissions and reasons, and `meep.outcome = denied`. Its status isn't `Error`, since a refusal isn't a fault and dashboards often alarm on errors.
- The `PermissionDenied` error's own trace hangs under it, so an `@errors` branch answering with a 403 is its child: request, refused at `Save`, 403 sent.
- It's skipped when the message's trace isn't recorded, as handling spans are, so it costs nothing without tracing.
- Not chosen: keeping a source's span open until its message is delivered, which would change what every source's span measures; or a span only where no other is open, which would give refusals two shapes.

**Answer:**

**Q299.** Q253's proposal mentioned gates that need a kind of proof, as in `@permission=meep.files.write proof=signed`, so a bearer credential can't reach them. Add it in phase 5, or wait until someone needs it?

**Answer:** Add in phase 5.

## After your answers

Added 25 November 2026. What each answer changed. Still on `permissions-phase-4`, not committed.

- The build has no warnings, and the whole suite passes: 881 of 881, in two full runs (880 before, and 1 new test; the test of an undefined permission was rewritten, and two others gained checks).
- **Q289 (undefined permissions may be needed).** A gate needing a name `permissions.meep` doesn't define now treats it as conferred by any trusted authority and requiring nothing, unless the pipeline's own `#permission` line narrows it, as it can a defined one. `meep lsp` warns about each such name, on its `@permission` or `#permission`, when this machine has a trust store: "Raednotes isn't defined in this machine's trust store (permissions.meep), so any trusted authority confers it and it requires nothing. Check the spelling, or define it." It reads the store without making one (`LspPermissions`). `Verify.Permission` still fails an undefined name as `UnknownPermission`, since Q257 has it check that a permission is well defined; say if it should follow the gates instead. `verbs.md` and `troubleshooting.md` say the new rule.
- **Q290, Q291, Q292, Q295, Q297.** Kept as they are.
- **Q293 (`Parse`, and refusing mismatched inflections).** `Parse`, typed-only `Authorise` and `Revoke`, and the shared SDK reader are added to phase 5 in `permissions.md`. The registry rule is built now, since it's about `[RequiresPermission]`: a verb whose inflections are chosen for each message, if any of them is, must have them all require the same permissions, or registering it fails saying why. `writing-verbs.md` says so. Tested with three inflections of a test verb.
- **Q294 (Synesthesia).** `docs/Design/synesthesia-handover.md` lists what changed in what the daemon sends across phases 1–4, the moved totals first, for a session that updates Synesthesia; `docs/Design/ToDo.md` points to it.
- **Q296 (`CountDenied()`).** `ModuleContext.CountDenied()` adds to a module's `denied` count without a message, and a Listen with `proof=required` calls it for each 401. The test checks the Listen's count. `writing-verbs.md` and `verbs.md` mention it.
- **Q298 (a span for each refusal).** For phase 5, as the proposal under the question says; added to phase 5 in `permissions.md`.
- **Q299 (gates that need a kind of proof).** For phase 5; added to phase 5 in `permissions.md`.
