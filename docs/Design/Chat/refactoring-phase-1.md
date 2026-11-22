# Refactoring, phase 1: security and leaks

Written 21 November 2026. Phase 1 of the plan in `refactoring.md`: T1, then every security finding (S1–S11) and every memory leak (M1–M8), a small commit for each, with its tests. Nothing else is refactored here; files move in phase 2 and classes are reshaped in phases 3 and 4. The work is on the branch `refactor-safety`, made from `main` at 356574a, not merged or pushed. This covers what was built for each finding, the fam chain's numbers for Q7, what the benchmarks say, what I found in passing, the decisions I made while building, and questions numbered from 230.

## What's built

| Finding | Commit | In short |
|---|---|---|
| T1 | d4a47d4 | The daemon tests wait until the daemon answers |
| S1 | 839832d | `#plugin` names and versions checked; the installer refuses folders outside the cache |
| S2 | 6824174 | Paths a message steers stay where the pipeline put them; `root=` and `any-path=true` |
| S3 | 516f23a | Fetch.File reads within its limit, and only regular files |
| S4 | cce00f4 | WebSocket streams wait for the socket, so a slow client's tap drops |
| S11 | 58dc918 | The daemon's ID and origins go only to private hosts by default |
| M1 | f3fd53b | An ended pipeline keeps only what's shown, so its plugins unload |
| M2 | ac0c7d2 | Learned trees leave memory when the last module lets go |
| S5 | 3366539 | Downloads have size limits |
| S6 | 947f94c | Git sources are https://, file:// or a path on this machine |
| S7 | c4cebc1 | Meep's folder, logs, trees and tokens are kept to you |
| S8 | 3557403 | A folder you haven't trusted can't choose the meep command |
| S9 | 17bd738 | Request bodies on the control socket are limited to 1 MiB |
| S10 | 2e74ff4 | `meep lsp` answers malformed messages instead of stopping |
| M3 | 87ae506 | Old client names are dropped when the table is full |
| M4 | c802f76 | Watchers for folders no longer included are let go |
| M5 | a76f0c9 | Temporary signing key files are deleted |
| M6 | ebd3474 | Programs are killed when their wait is cancelled |
| M7 | 1a5d1e3 | `Pipeline` disposes `_stopSources` |
| (found) | 502be5a | A race in `MeepHosts.Save` |
| M8 | acb8b89 | `dotnet run -c Release -- fam` measures what the fam chain keeps |

### T1. The flaky daemon test

`DaemonLab.UntilAnswering` waits until the daemon answers `GET /` on its socket, or has ended, rather than for the socket's file, which Kestrel makes a moment before the daemon answers. Both tests that start a daemon through the CLI use it; `The_meep_command_drives_the_daemon` had the same race as the one the audit found.

### S1. `#plugin` lines and the plugin cache

- **The parser checks each word** (`PluginNames` in `Meeplang/Parsing`): a GitHub owner is letters, digits and `-`, up to 39 characters; a repository is letters, digits, `-`, `_` and `.`, up to 100, and not `.` or `..`; a NuGet ID follows NuGet's own rule (`^\w+([.-]\w+)*$`, up to 100, ASCII only); a version is letters, digits, `.`, `+` and `-`, up to 64 characters, starting with a letter or digit. Each mistake is a diagnostic on the word, saying what's allowed.
- **The installer refuses a folder outside the cache** (`PluginInstaller.InCache`) before it reads a manifest there, deletes it or moves a new copy in, whatever made the directive, and checks NuGet IDs with NuGet's own `PackageIdValidator` too.

### S2. Computed paths in Save and Fetch.File

- **What a `{msg…}` hole gives can't steer a path** unless the module sets `any-path=true`: it can't make the path absolute, start it with `~`, put `..` in it, or be part of a `file://` address. So `Save "~/inbox/{msg.Query['name']}.txt"` writes only in `~/inbox`, whatever the request says. A refusal is an error naming the path and why, ending "Set any-path=true if the message should choose any path."
- **Holes that don't read the message aren't limited**, such as `Save "{Folder}/all.txt"` with a constant, and neither is text written in the pipeline, so `Save "/var/log/{msg.Name}.txt"` is as it was.
- **`root=` keeps every path inside a folder**: relative paths start from it, and a path that leads anywhere else, by `..`, an absolute path or `~`, is an error. With `root=`, Fetch only reads files, since a web address can't be inside a folder.
- **How modules see the holes:** a compiled template gives its value in pieces, marking the pieces that came from the message, through a new SDK interface, `IComputedPieces`, and `Computed<string>.Pieces(message)` for any computed text. A value made in C# with `Computed<T>.From` counts as one piece from the message; one made with `Constant`, as one that isn't. Plugins that take paths can use the same check. `docs/writing-verbs.md` says so.
- `docs/verbs.md` describes both parameters for Save and Fetch, and I corrected Save's `mode` there, which said `Replace` where the value is `Overwrite`.

### S3. Fetch.File's size limit

Fetch.File reads through the same bounded reader the HTTP modules use (`Bodies.ReadAsync`), to the end of the file rather than the length it had when checked, so a file that grows while it's read stops at `max-size` with an error. It refuses anything that isn't a regular file, such as `/dev/zero`, a named pipe or a socket. .NET's `FileInfo` reports those as ordinary files of length 0 on Unix, so `FileKinds` asks the system, through .NET's own `stat` wrapper: before opening, since opening a named pipe waits for a writer, and again on the open handle, in case the path changed in between. Text is decoded as `File.ReadAllText` would, UTF-8 unless a byte order mark says otherwise.

### S4. Slow WebSocket clients

Over `/ws`, a tap's or a stats stream's events now wait until each is on the socket (`QueueAsync`), as a subscription's updates already did. A client that reads slowly holds its stream back, and the tap's buffer drops its oldest messages and counts them, as designed, instead of frames piling up in the session's queue. Answers and `end` events are still queued without waiting, one for each request. A socket that fails while a stream waits now ends the stream quietly; before, its error escaped the request's task.

### S11. Who hears the daemon's ID, and who gets origins

- **Listens answer `Meep-Host` only to clients on this machine and private addresses**, unless the daemon's settings say `meep-host-public = true`, under `defaults` in `daemon.meep` beside `meep-hosts` (Q213). A client on another address that asks is told nothing, as one that doesn't ask always was.
- **Fetch and Send ask, or give an origin, only to hosts `meep-hosts` names, this machine, and hosts on private addresses.** A host named by address is private by its address; a host named by name is looked up, and is private only if every address it has is (looked up again at most every 30 seconds). Private means loopback, 10/8, 172.16/12, 192.168/16, link-local (169.254/16, fe80::/10), the shared range carriers and overlay networks such as Tailscale use (100.64/10), and IPv6's unique-local range (fc00::/7).
- **Public hosts aren't even asked now** (Q232), so an API on the internet hears nothing of Meep from a pipeline calling it.
- **The docs say flow evidence is a claim, not proof:** `flows.md` gains a paragraph after "What a client has to do", and `verbs.md` says so under Listen and Fetch.

### M1. Ended pipelines in the daemon

When a pipeline ends, `HostedPipeline.End` keeps its final detail, its counts as they were, its text, its graph, its warnings and how long it stays listed, and lets go of the built pipeline, the verbs and plugins it was built with, and the bootstrap pipeline. So its plugins unload while it's still listed. Subscriptions read counts through `PipelineCounts`, which reads a running pipeline as before, or gives back the counts kept when it ended; so a subscription that starts after a pipeline ended still gets its last counts, and the host's totals don't drop when a pipeline ends. The daemon also prunes ended pipelines on a timer, every 30 seconds, telling subscriptions when it takes one off, as well as whenever the list is read.

Before the fix, the new test (a plugin, through the daemon, with `keep-finished = 1h`) showed the plugin still loaded after the pipeline had finished; now it unloads, and the pipeline's detail, text and graph are still there.

### M2. Learned trees

Learn, Classify and Forget each hold their tree (`BayesTree.Hold`) until they're disposed, as when their pipeline ends. When the last lets go, the tree is saved, if a module that saves it held it (a Learn with `save-every` above 0, or a Forget), and leaves memory, to be loaded from its file when it's next named. `BayesTree.Open` still keeps a tree for the life of the process, for tests and tools. A tree kept in memory only, where every Learn had `save-every=0`, is now gone once no pipeline uses it (Q233); two tests that relied on such a tree outliving its pipeline were changed, and `docs/verbs.md` says so under Learn.

### S5. Download limits

A pipeline, include or signature from a URL is read up to 4 MiB, and a plugin, a GitHub release asset or a NuGet package, up to 256 MiB (`PluginOptions.MaxDownload`), each refused past the limit with an error naming it, whether or not the server says how long it is.

### S6. Git addresses

Git sources allow only `https://`, `file://` and paths on this machine (`GitSnapshot.WhyNot`); anything else, `git://` and `ftp://` included, is refused with the same message as `ssh://` and scp-style addresses.

### S7. Meep's folder

- **A new SDK helper, `PrivateFolders`**, makes a folder of Meep's readable by you alone (700), and every missing folder above it, since .NET gives the mode only to the last; and narrows one that's there already if others can reach into it.
- **Used for** detached pipelines' `logs`, the default `trees` folder, the `daemon` folder, the folder the tokens file is in, the plugin cache's staging folder, and the folder `allowed_signers` is in. `~/.meep` itself is made private when Meep makes it, and left as it is if it's there already.
- **Files:** saved trees and the tokens file are made 600 from the start, rather than narrowed after.
- **As OpenSSH does, an `allowed_signers` file that others can change isn't trusted**: if the file or its folder can be written by the group or others, loading it fails, saying which and the `chmod` that fixes it.

### S8. The VS Code extension

`meeplang.meepPath` is scoped `machine-overridable`, and the extension declares `capabilities.untrustedWorkspaces` as `limited`, with `meeplang.meepPath` restricted, so in a folder you haven't trusted only your own setting is used. `npm test` checks the manifest says so. This also turns the extension on in Restricted Mode, which it wasn't before (Q234).

### S9. Request bodies

Kestrel's limit for the control socket is 1 MiB, as `/ws` frames have; a request whose body says it's larger is refused before any of it is read, with 413 and a problem saying the limit. `DaemonClient` sends `Expect: 100-continue` with bodies over 64 KiB, so a refusal arrives as an answer rather than as a connection closed while sending; the first version of the test failed that way under load.

### S10. `meep lsp`

Each message is handled inside a catch: one missing a field, or with the wrong kind of value, is answered with an invalid-params error naming the method, and the server carries on. A body that isn't JSON is answered with a parse error, and one that isn't an object with an invalid-request error, both with a null id, as JSON-RPC says. A message over 32 MiB is skipped without being read into memory. Only a `Content-Length` that can't be read stops the server, since where the next message starts is lost; it says so first. The dispatcher stays a switch until phase 4.

### M3–M7

- **M3:** when `ClientNames` is full, names kept longer than ten minutes are dropped to make room, at most every ten seconds.
- **M4:** `PipelineWatcher.Refresh` disposes the watchers of folders no watched file is in.
- **M5:** the file a `key::` signing key is written to is deleted once the commit is signed; `CommitSigning.LetGo` deletes only files it wrote itself.
- **M6:** `ssh-keygen` from `meep sign`, and `launchctl` and `systemctl` from `meep daemon install`, are killed, with anything they started, when the wait for them is cancelled (`Programs.WaitOrKillAsync`). The one runner for all three (D10) is phase 3's.
- **M7:** `Pipeline` disposes `_stopSources` beside `_abort`, once it has finished or if it never ran. Sources are started with the token kept as `StoppingToken`, which still reads as it was after the source is disposed.

## M8: what the fam chain keeps

`dotnet run -c Release -- fam` in `benchmarks/Meep2.Benchmarks` runs three cases and reports the memory still in use after a full, compacting collection, with what a module kept still held, less the same reading before. Nothing is trimmed (Q7). On this Mac (16 processors, .NET 11, workstation GC), 21 November:

**Tick → Fetch a 5 MB file → Split into 10,000 records of 524 bytes**, with a module after Split keeping them as a cache would:

| The module keeps | Memory kept |
|---|---|
| nothing | 0.15 MB |
| only each record's text | 10.47 MB |
| one record | 10.12 MB |
| all 10,000 records | 21.18 MB |

A file of 5 MB is 10 MB in memory as .NET's UTF-16 text. Keeping one record keeps the whole file, through its parent; keeping all the records costs the file plus their own text, about twice what keeping their text alone costs.

**A WebSocket to a Listen, held open while 100,000 messages of 100 bytes go through**, each written by WriteLine, measured after 25,000, 50,000 and 100,000: 0.00, 0.15 and 0.15 MB, about 1.6 bytes a message, which is within the noise of a measurement like this. A long-lived connection doesn't grow the daemon: each message's ancestry goes once it has left the pipeline, though the connection's lease lasts.

**A source making each message from the one before** (`last = last with { Value = i }`), as a running total would, with the last message kept:

| Chain | Memory kept | A message |
|---|---|---|
| 10,000 | 0.84 MB | 88 bytes |
| 100,000 | 8.39 MB | 88 bytes |
| 1,000,000 | 83.92 MB | 88 bytes |

This one grows without end: `with` makes the new message's parent the old one, so the last message keeps every one before it. At ten messages a second that's about 72 MB a day, for as long as the module runs. Q235 asks what to do.

How it's measured: each reading is taken on a fresh stack (`Task.Yield`), since a pipeline's continuations run inline, and the frames of a run that has just finished, with its locals, would otherwise still be there; my first readings were off by the whole file for that reason.

## Benchmarks

Run on 21 November, `main` (356574a, in a scratch worktree) first, then this branch, one after the other on this Mac. The runtime's hot path is unchanged by this phase; what could move is Fetch and Listen (S11 adds a check for each request), subscriptions (M1 reads counts through `PipelineCounts`) and the pipeline's ending (M7).

**ChainBenchmarks** (BenchmarkDotNet's short job, a message's time and allocation end to end):

| Steps, new message each step | `main` | This branch |
|---|---|---|
| 1, no | 1.74 µs, 120 B | 1.44 µs, 117 B |
| 1, yes | 3.13 µs, 219 B | 1.96 µs, 218 B |
| 4, no | 2.68 µs, 122 B | 1.79 µs, 128 B |
| 4, yes | 3.95 µs, 498 B | 2.72 µs, 492 B |

The branch is faster in every row, which nothing in it explains: the short job's error bars are wide (up to ±8 µs on `main`), and `main` ran first, while the machine was busier. Allocation is the same within a few bytes. I read this as no change.

**Flows** (`flows --seconds 10`, four Fetches to a Listen over loopback):

| Phase | `main` | This branch |
|---|---|---|
| No tracing | 3,443 requests/s, 8,549 B a request | 3,511 requests/s, 8,642 B a request |
| Tracing, one in ten recorded | 3,455 requests/s, 9,951 B | 3,522 requests/s, 10,064 B |
| No tracing, again | 3,426 requests/s, 8,534 B | 3,500 requests/s, 8,617 B |

Requests a second are the same or a little better. Each request allocates about 90–110 bytes more, about 1%, which I haven't traced to its line; S11's check for each request (`MayTell`, `MayAnswer`) is the likely place, and phase 5 can look if it matters. CPU a request varies more between phases than between branches, so I've left it out.

**Subscriptions** (`subscriptions --seconds 10`): what each kind of update allocates is the same to within 1% (the largest, timing only, 1,789 KB against 1,803 KB), and the time to make each is within the run's noise. The live phases' CPU readings move by more between their own "without a client" baselines (468 to 597 ms a second) than between branches.

## Found in passing

- **A race in `MeepHosts.Save`** (fixed, 502be5a): it sorted the `ConcurrentDictionary` of learned hosts directly, which can read past its end while requests change it, and failed a daemon's dispose in one run of the suite. It sorts a snapshot now.
- **Three tests that fail now and then under the full suite's load**, each passing on its own, and none from this work, so I've left them:
  - `HostingTests.A_trace_that_isnt_sampled_gets_no_spans_after_its_source` (twice in about 25 runs). A source's span starts without a parent of its own (`Telemetry.Emitted`, line 65), so it takes `Activity.Current`; on a Kestrel request thread, when another test has ASP.NET's activity listened to, that's an unrecorded activity that isn't remote, which the test's listener counts. The fix belongs in the runtime: start a source's span as a root, or from the request's `traceparent` alone.
  - `FlowEvidenceTests.A_subscription_says_which_places_were_folded` (once): it expects the fold and the new origin in one update, but under load they can come in two.
  - `PluginTests.The_cli_trusts_the_keys_on_a_github_account` (once): `TestServer` takes a free port and then binds it, and another test took it in between.
- **The baseline suite failed five tests in my first run**, because I was editing while it built; run again, all 615 passed, so the baseline was 615 of 615 here, T1 included, rather than 614.

## Decisions I made while building

- **A GitHub owner allows only letters, digits and `-`**, stricter than the audit's rule (which allowed `_` and `.` in both parts), since that's GitHub's rule for accounts.
- **The parser has its own copy of NuGet's ID rule**, since Meeplang doesn't reference NuGet; the installer checks with NuGet's validator as well.
- **S2 checks only holes that read the message.** A constant in a hole, such as a folder name, is the pipeline's own text. A `..` made by joining two holes, or a hole and the text beside it, is refused too, since what counts is whether the message gave any of the `..`.
- **A message can't take part in a `file://` address at all** without `any-path=true`, since such an address is decoded and normalised in ways the text doesn't show; a path without `file://` is checked as written.
- **`root=` goes by the path as written**: a link inside the folder that points out of it is followed (Q237). `docs/verbs.md` says so.
- **Values from `Computed<T>.From` count as from the message**, so C# code that builds a path from a message is checked too; it can set `AnyPath`.
- **Fetch with `root=` reads files only**, rather than ignoring `root=` for web addresses.
- **`FileKinds` reads the file's type through .NET's own `stat` wrapper (`SystemNative_Stat`)**, reading only the mode, which .NET lays out the same on every Unix, rather than libc's `stat`, whose layout differs between macOS and Linux.
- **S4 left answers and `end` events unwaited**, since there's one for each request.
- **S11's setting is a true-or-false, `meep-host-public`**, beside `meep-hosts` (Q230). It's for the whole process, as `meep-hosts` is; `meep run` has no settings file, so it takes `--meep-host-public` instead (Q230).
- **S11 doesn't ask public hosts at all** (Q232), and names are looked up to decide (Q231).
- **A Listen still counts the origin a public client gives**, as a claim, rather than counting it by its address; the docs say so.
- **M1 freezes the counts as they were**, so subscriptions keep working for ended pipelines, rather than dropping their counts when they end.
- **The daemon's catalog of plugins can be set in its options, for tests** (`DaemonOptions.Catalog`, internal); a reload still uses the process's own catalog.
- **M2 saves a tree when it's dropped only if a module that saves it held it**, so a Classify alone never writes a file.
- **`PrivateFolders` is public in the SDK.** I first made it internal, visible to Meep's own assemblies, but letting Modules see the SDK's internals changes which members it can override, and broke Batch and Zip; it's also useful to plugins that keep files in Meep's folder.
- **Saved trees are always 600**, even with `file=`, since they hold the words they learned; a folder named with `file=` is made as usual, and only the default `trees` folder is made private.
- **The tokens file is still narrowed after it's written**, for a file made before this change.
- **S9's limit is `SocketSession.MaxFrame`**, 1 MiB, for everything but starting a pipeline from its text, which can be 4 MiB, as one from a URL can (Q236).
- **`meep lsp` stops only when framing is lost**, saying why, rather than guessing where the next message starts.
- **M6 kills on cancellation only**, as the finding asks; `CommitSigning`, which has no cancellation, already kills on its timeout.
- **M8 runs as a measurement of its own**, like `flows` and `subscriptions`, rather than under BenchmarkDotNet, which measures time and allocation, not what's kept.

## Questions

230. S11's setting: is `meep-host-public = true`, under `defaults` in `daemon.meep`, the name and place you want? And should `meep run` be able to set it, with a command-line option, or is private-only right for it?

**Answer:** Yes to both.

231. What counts as private for S11: loopback, the three private IPv4 ranges, link-local, 100.64/10 and IPv6's unique-local range. 100.64/10 is the shared range carriers use too, so on a carrier's network others' machines may be in it; I included it for Tailscale, whose machines are given addresses there. Keep it, or leave it out and let Tailscale users name hosts in `meep-hosts`? And a name counts as private only if every address it resolves to is; is a lookup each 30 seconds acceptable, or would you rather names were never private unless `meep-hosts` names them?

**Answer:** Leave it out. 30 second lookups are fine.

232. Q224 said origins go only to private and named hosts. I went a step further: Fetch and Send don't ask public hosts `Meep-Host: ?` either, since they'd never be told anything, and asking says the client runs Meep. So a public Meep host is never learned and must be named in `meep-hosts` to get origins. Agree?

**Answer:** Yes.

233. M2: a tree kept only in memory (`save-every=0`) now leaves memory when no pipeline uses it, so a pipeline started again later starts with an empty tree. Is that what `save-every=0` should mean, or should such trees stay until the daemon stops (which leaves M2's leak for them)?

**Answer:** Yes.

234. S8: declaring `limited` support for untrusted workspaces turns Meeplang's highlighting, checking and completion on in folders you haven't trusted, which were off before, running `meep lsp` from your own setting. Keep that, or declare `supported: false`, which keeps it off there as before, with the setting's scope still fixed?

**Answer:** Keep that.

235. Q7, with M8's numbers: holding one record of a split file holds the whole file, and a module that makes each message from the one before (`last with { … }`) grows a chain without end, 88 bytes a message, about 72 MB a day at ten a second. Options:

     - (a) Leave ancestry whole, and document that a module keeping state should keep values, not messages, and start each message afresh rather than with `with`.
     - (b) A way to cut ancestry where a pipeline asks: a parameter such as `@detach` on a module, so what it emits has no parents, or a verb that does only that.
     - (c) A depth limit: past N generations, a message's ancestors are let go, with `fam` finding only those within reach.
     - (d) For the endless chain alone: the runtime gives a message emitted by a source no parents unless the source names them, so `with` in a source doesn't chain. That changes what `fam` finds after a source that derives its messages.

     I'd suggest (a) now, and (b) when a pipeline needs it; (c) makes `fam` depend on how far back something is, and (d) only covers sources.

**Answer:** (a)

236. S9 limits a request's body to 1 MiB, so a pipeline sent to the daemon as text (`meep start -`, `StartTextAsync`) can be at most 1 MiB, though one from a URL can be 4 MiB (S5). Raise the control socket's limit to 4 MiB for starting pipelines, or keep 1 MiB for everything?

**Answer:** Raise to 4 MiB.

237. S2's `root=` goes by the path as written, so a link inside the folder that points out of it is followed. Should it resolve links, refusing a path whose real location is outside the folder? That costs a look at each folder on the path for each message.

**Answer:** No.

## Built from the answers

- **Q230** (0402069): `meep run` and `meep daemon` take `--meep-host-public`, and `--detach` and `meep daemon install` pass it on. A daemon now only ever sets the process-wide flag, never clears it, so a daemon without it doesn't undo it for another in the same process, as tests run them; the test that came with this found that race.
- **Q231** (d3e5694): 100.64/10 isn't private. `docs/verbs.md` says what is, and that Tailscale hosts go in `meep-hosts`. Since `meep run` has no settings file, it can't name them, so a run on a Tailscale network tells them nothing; `--meep-hosts <names>` would fix that if it's wanted.
- **Q236** (8834219): `POST /pipelines`, from a client that may start pipelines, may have a body of 24 MiB and 64 KiB, enough for 4 MiB of text however JSON escapes it (six bytes for a `<`); the text is then checked against 4 MiB, with its own message. Every other request, and every `/ws` frame, stays at 1 MiB, so a pipeline started by text over a WebSocket can still be at most 1 MiB; I left that, since the frame's size is checked before its path is known.
- **Q235** (f5f3db2): `docs/writing-verbs.md`, under "Parents and family", says why a module should keep values rather than messages and make each message afresh rather than `with` the last, with M8's numbers, and the checklist has a line for it.
- **Q232, Q233, Q234, Q237**: kept as built.

## The test run

The build has no warnings, and the full suite of 677 (the 615 before this work and 62 new) passed on the last run, and on most runs through the phase; the failures I saw are those in "Found in passing". `npm test` in `editors/vscode` passes, the grammar and the new manifest check.

After the answers, the suite is 678: two new tests, and one case fewer in the private-address test, for 100.64/10's upper edge, which no longer has a boundary to check. All 678 passed.
