# Refactoring, phase 5: performance

Written 23 November 2026. Phase 5 of the plan in `refactoring.md`, the last: P1–P4 from section 4, and E1–E4 and E6–E8 from section 3 (E5 was done in phase 3). The work is on the branch `refactor-performance`, made from `main` at 8ab6a53, with a commit for each item kept, not merged or pushed. This covers what was done for each item with its numbers before and after, what I dropped because the numbers didn't support it, what I found in passing, the decisions I made while building, and questions numbered from Q248.

## Results

- 9 commits: 8 for items kept (P1, P2, P3, E1, E3, E4, E6, E7), and this document, which also carries the benchmarks written for the items I dropped.
- Dropped, with their numbers below: P4 (both parts), E2 and E8.
- The build has no warnings, with warnings as errors.
- The whole suite passes: 701 of 701 (694 before, and 7 new tests).
- Every commit was built and tested on its own, in a scratch worktree, in order: all 9 build with no warnings, and all pass, from 696 tests at P1 to 701 at the last. None of the known flaky tests failed in those runs, nor in the dozen full runs I made while working.
- Every verb's inflections come in the same order as on `main`, checked against the built `Meep2.Modules.dll` after each commit that touched Modules (P1 and E7). No inflection type or file moved or was added.

## How I measured

Every number here was taken on this Mac (16 processors, .NET 11), `main` in a scratch worktree beside the branch, one after the other, with nothing else building. Where a benchmark was new, I copied it into `main`'s worktree so both ran the same code. BenchmarkDotNet's medium job throughout; the load runs (`requests`, `flush-wait`, `subscriptions`) report their own way, as below. Where a first comparison moved for both versions alike, I ran `main` and the branch again back to back, and quote both runs.

New benchmarks, all in `benchmarks/Meep2.Benchmarks`:

| Benchmark | For | What it does |
|---|---|---|
| `requests` (`RequestRate`) | P3 | API requests a second with a token, from 8 clients at once, and the daemon's CPU for each |
| `AccessBenchmarks` | P3 | checking a token |
| `flush-wait` (`FlushWait`) | P2 | 50 modules busy 20 ms a message, each asking for a flush 1 ms in |
| `LiveBenchmarks`, `Branches` | P4 | the existing change benchmark, now also for a pipeline of 503 modules |
| `subscriptions --ticks-only`, and a "list and host only" row | E2 | a subscription's tick timed alone, without the 2½-minute live phases |
| `NuGetBenchmarks` | E4 | resolving and downloading the test plugin and the package it needs from a folder feed |
| `LspBenchmarks` | E6 | reading `meep lsp`'s messages from a pipe (`JsonRpcChannel.cs` is compiled into the benchmarks, since the CLI is a program of its own) |
| `FramingBenchmarks` | E7 | one message of 64 KB or 1 MB arriving in 4 KB pieces |
| `FeedersBenchmarks` | E8 | 1, 4 or 8 sources feeding one module |

## What was done

### P1. Waiting on asynchronous work by blocking (077be75)

A correctness fix as well as a performance one, so it stays without needing numbers; nothing measured it before, and the blocking it removes was of a thread-pool thread for up to 30 seconds, which only shows under load.

- **Includes from a URL** are fetched before the pipeline is parsed, not while it's parsed. `FetchedIncludes.FetchAsync` (in Meeplang) walks the pipeline's `#include` lines, and theirs, in the order the parser will, fetching each with an `IIncludeFetcher`; the parser is then handed what came back, or why it couldn't be used, through `IIncludeReader` as before. It doesn't look into a file that failed or doesn't match its pin, as the parser wouldn't, and it leaves loops for the parser to report. The parser's pin check and its UTF-8 reading are shared with it (`MeeplangParser.PinMismatch` and `Text`).
- **"… didn't arrive within 30s" (Q243).** A pipeline, include or signature from a URL that takes too long now fails with `https://…/shared.meep didn't arrive within 30s.`, and an include's says `Couldn't include shared.meep: …` before it, as other include errors do. A cancellation from the caller is still a cancellation.
- **`PendingBranch.CommitAsync`** awaits `ssh-keygen` rather than blocking on it, and passes a cancellation token through to it. `CommitTo`'s seven parameters are gone: `BranchTip` finds the branch and its parent (with the checks for a bad name, a checked-out branch and an empty repository) and moves it afterwards, and the commit is made by `TreeWith` (4 parameters) and `CreateCommitAsync` (4 and a token). The daemon commits without the request's cancellation (below).
- **Listen.HTTP** adds a new path outside its lock, then swaps it in under the lock only if it's still listening as it was; if it stopped or moved meanwhile, the new path is let go. It still waits for the path to be added, since `IReconfigurable.ParametersChanged` is synchronous (Q248).

Tests: a pipeline that never arrives, and an include that never arrives, each say so with a 300 ms limit (an internal overload of `PipelineSources.ReadAsync` takes the limit, so the test doesn't change it for others running at the same time); and a pipeline from a URL whose includes include others, and one that loops, is fetched whole before parsing, asks for each file once, and parsing asks for nothing more. `WriteBackTests` use `CommitAsync`. `LiveTests.Listen_takes_a_new_path_without_closing_its_port` covers Listen.HTTP's change.

### P2. Changes and flushes polled every millisecond (8bade54)

`RunBetweenMessagesAsync` queued its work, then looped on `Task.Delay(1)` until the loop ran it or went idle. Now whoever holds the module, the loop or a caller while the loop waits, runs what's queued before letting go and looks again after letting go. Taking and letting go of the module, and counting a job, are full fences (`Interlocked`), so whichever side acts last sees the other's: a job can't be left queued with nobody to run it. Nothing polls.

`flush-wait`, CPU a second across the process, two phases of each, 10 s each:

| | `main` | Branch |
|---|---|---|
| Not flushing | 65.1, 64.2 ms | 77.9, 77.7 ms |
| Flushing, ~2,400 flushes a second | 494.5, 476.8 ms | 136.4, 106.9 ms |
| What the flushes cost | ~420 ms | ~30–60 ms |

The message path itself, which now lets go with `Interlocked.Exchange` rather than `Volatile.Write`, doesn't move (ChainBenchmarks, µs a message):

| Chain | `main` | Branch |
|---|---|---|
| 1 step, same message | 1.316 ± 0.042, 109 B | 1.326 ± 0.039, 130 B |
| 1 step, new message | 1.844 ± 0.022, 218 B | 1.932 ± 0.045, 218 B |
| 4 steps, same message | 1.754 ± 0.040, 129 B | 1.791 ± 0.031, 125 B |
| 4 steps, new message | 2.786 ± 0.076, 501 B | 2.819 ± 0.082, 479 B |

Nor do the other runtime benchmarks, run on 23 November with the branch's last commit (µs a message):

| Benchmark | `main` | Branch |
|---|---|---|
| Leases, none | 1.654 ± 0.048, 128 B | 1.628 ± 0.056, 131 B |
| Leases, shared | 2.296 ± 0.032, 157 B | 2.106 ± 0.042, 154 B |
| Leases, each message | 2.685 ± 0.103, 578 B | 2.659 ± 0.103, 576 B |
| Observing nothing | 1.715 ± 0.107, 218 B | 1.543 ± 0.015, 218 B |
| Observing the histogram | 1.807 ± 0.031, 218 B | 1.642 ± 0.034, 218 B |
| Observing with a tap | 2.446 ± 0.036, 218 B | 2.602 ± 0.047, 218 B |
| Observing, sampled | 2.166 ± 0.071, 795 B | 2.353 ± 0.078, 798 B |
| Observing, tracing | 5.730 ± 0.188, 2,469 B | 5.430 ± 0.144, 2,443 B |

Times move both ways, as they did in phase 4, and allocation is the same. `flows-micro` (origins and places, which nothing here touched) is within 13 ns everywhere.

Test: a flush asked for 1 ms into a 150 ms message runs after that message and before the next.

### P3. Two file-system calls for every API request (0618029)

`DaemonAccess` set `_tokens` and `_tokensWritten` from many request threads without a lock, so one thread could pair new tokens with an old time, or the reverse. They're now read and set under one lock: the correctness fix. And the file is looked at no more than once a second, or at once when a token isn't found, so a token made a moment ago works straight away and one taken out stops working within a second (Q250).

| | `main` | Branch |
|---|---|---|
| Checking a token (`AccessBenchmarks`) | 13.32 ± 0.44 µs | 3.39 ± 0.08 µs |
| `requests`, four rounds of 5 s: requests a second | 20,550, 20,636, 22,622, 21,501 | 20,575, 22,895, 22,425, 22,813 |
| Daemon CPU for each request | 479, 452, 444, 447 µs | 458, 412, 403, 413 µs |

The requests a second are within the rounds' spread; the CPU for each is about 8% lower in every round after the first. The lock on its own, without the once-a-second check, measured 13.51 ± 0.25 µs, the same as `main`.

Test: a token made while the daemon runs works at once, and one taken out of the file is refused 1.2 s later.

### E1. Each link's key made afresh for every detail (6416600)

A pipeline's detail, which a stats stream asks for on every tick and a subscription on every update, formatted `"from>to"` for every link every time. The link keys are now kept beside the modules' keys they're made from, which `LivePipeline` already keeps for each version of the graph, so a new version gets new ones and the old are let go with it.

A tick timed alone, 50 pipelines of 20 modules (`subscriptions`, median of 30):

| | `main` | Branch |
|---|---|---|
| Stats streams: each pipeline's detail | 1,393 KB | 1,359 KB |
| Subscription, links only: what changed | 608 KB | 574 KB |
| Subscription, links only: whole | 755 KB | 721 KB |

Allocation is the same from run to run, so the 34 KB is real; the times moved both ways by more than this could, so I can't say it's faster. It's small, but it's what the audit asked for. The other half of E1, versioning the graph by serialising it, I left (below).

### E3. A new buffer for every frame and event (15fd774)

`SocketSession`'s frames, subscriptions' JSON frames, and the HTTP API's answers and events each made an `ArrayBufferWriter` that grew by doubling, then copied it out. `ScratchBuffer` keeps one for each thread and hands it out again empty. A write that rents one while its thread's is in use (a frame inside a frame) gets a new one; one over 1 MiB isn't kept. Frames are still copied out once, since they wait in the session's queue.

| | Before (with E1) | After |
|---|---|---|
| Stats streams for 50 pipelines at 100 ms, allocated over no client | 14.2 MB/s | 7.6 MB/s |
| Subscription, JSON, modules and links at 100 ms, allocated over no client | 11.6 MB/s | 10.3 MB/s |
| A JSON subscription update, timed alone: what changed | 1,182 KB, 2.88 ms | 1,050 KB, 2.26 ms |
| The same, whole | 1,438 KB, 2.64 ms | 1,306 KB, 2.07 ms |

Test: a buffer comes back empty, and one rented while another is in use is a different one.

### E4. NuGet packages copied in memory (825b76a)

A downloaded package was copied out with `MemoryStream.ToArray()` before it was read. `ResolvedPackage` now keeps the download's own buffer, and `Open()` gives a read-only stream over it for its dependencies, its files and its hash. (The audit's second copy wasn't one: `new MemoryStream(bytes)` doesn't copy.)

| `NuGetBenchmarks.Resolve` | `main` | Branch |
|---|---|---|
| Allocated | 167.65 KB | 156.23 KB |
| Time | 746 ± 44 µs | 635 ± 13 µs |

The 11.4 KB is the two test packages' size (7,037 and 3,476 bytes), as expected: an install saves a copy of every package it downloads, which for a plugin of a few megabytes is a few megabytes. The time is within what a folder feed's file reads vary by.

### E6. `meep lsp` read headers a byte at a time (686c998)

`JsonRpcChannel` read each header byte with its own read of the pipe. It now reads through a 16 KiB `BufferedStream`.

| `LspBenchmarks.Read`, a 2 KB message from a pipe | `main` | Branch |
|---|---|---|
| Time | 84.69 ± 0.95 µs | 4.13 ± 0.06 µs |
| Allocated | 5.26 KB | 5.27 KB |

### E7. TCP framing searched from the start again (ae727cd)

When a message arrived in pieces, each read searched everything waiting from its first byte. `Framing.ReadAsync` now keeps how far it has searched, and each framing starts from there: lines from the last byte searched, a marker from that less the marker's length, so one split across two pieces is still found. Taking a message sets it back to the start.

| `FramingBenchmarks`, 4 KB pieces | `main` | Branch |
|---|---|---|
| Lines, 64 KB | 36.27 ± 0.19 µs | 17.60 ± 0.14 µs |
| Lines, 1 MB | 5,733 ± 116 µs | 757 ± 58 µs |
| Marker, 64 KB | 37.31 ± 0.27 µs | 18.09 ± 0.18 µs |
| Marker, 1 MB | 6,031 ± 163 µs | 702 ± 43 µs |

Allocation is the same. On `main` 16 times the bytes took about 160 times as long; on the branch, 43 and 39 times, most of it copying the message out.

Test: lines and a `\r\n.\r\n` marker, sent two bytes at a time, come out as the three messages they are.

## Dropped, because the numbers didn't support them

### P4. Smaller ones

**`Contains` on lists inside loops** (`ChangePlan.Of`, `Pipeline.Remaining`). I changed them to sets and measured applying a one-parameter change, with `LiveBenchmarks` now also running a pipeline of 503 modules:

| `ApplyOneChange` | `main` | Sets |
|---|---|---|
| First run, 20 modules | 12.56 ± 0.61 ms | 10.76 ± 0.36 ms |
| First run, 503 modules | 245.97 ± 6.64 ms | 215.52 ± 6.36 ms |
| Back to back, 20 modules | 10.75 ± 0.25 ms | 10.93 ± 0.21 ms |
| Back to back, 503 modules | 211.23 ± 7.90 ms | 222.41 ± 3.61 ms |

The first run's gap was the machine (20 modules moved as much, where the change can't matter); run back to back there's none. The time grows in step with the modules, so the lists aren't what costs (see "Found in passing").

**A `PeriodicTimer` for the stats stream.** One timer for each stream rather than a `Task.Delay` each tick (keeping a tick that a graph notice interrupted, since a `PeriodicTimer` allows one wait at a time):

| Stats streams, 50 pipelines at 100 ms | `main` | Timer |
|---|---|---|
| CPU over no client | 172 ms/s (630 against 459) | 141 ms/s (686 against 545) |
| Allocated over no client | 14.5 MB/s | 14.7 MB/s |
| Frames a second | 492.4 | 500.0 |

Allocation didn't move, and the CPU difference is smaller than the baselines' own (459 against 545). The timer does keep a steadier rhythm, 500 frames a second rather than 492, since `Task.Delay` waits its whole time after each frame is made; that's not something anyone asked for, so I left it.

### E2. Subscriptions found changes by building strings

I changed `SentState` to keep the last pipeline summaries, Listens and Sends and compare them by value (a summary's warnings item by item, since a record compares lists by reference). A tick timed alone, with a new "list and host only" subscription:

| List and host at 100 ms | `main` | By value |
|---|---|---|
| What changed | 1.16 ms, 623 KB (again: 0.99 ms, 623 KB) | 1.10 ms, 623 KB |
| Whole | 1.09 ms, 654 KB (again: 1.08 ms, 654 KB) | 1.10 ms, 648 KB |

Nothing measurable: the strings are a few hundred bytes for 50 pipelines, and the 623 KB is reading the host's totals.

### E8. A module's counters sharing cache lines

`FeedersBenchmarks`, µs a message, with `_received` moved to a cache line of its own inside `NodeCounters` (an explicit layout, as `LinkCount` has; it stayed a class, Q247):

| Feeders | `main` | Padded | `main`, again | Padded, again |
|---|---|---|---|---|
| 1 | 1.108 ± 0.044 | 1.172 ± 0.048 | 1.061 ± 0.029 | 1.065 ± 0.019 |
| 4 | 1.235 ± 0.023 | 1.282 ± 0.022 | 1.177 ± 0.026 | 1.275 ± 0.016 |
| 8 | 1.378 ± 0.019 | 1.420 ± 0.061 | 1.371 ± 0.025 | 1.378 ± 0.021 |

Padding was never faster, and with 4 feeders slower by more than the error bars once. More feeders cost a little more per message on both, but sharing the counters' line isn't why; the buffer they all write into is the likelier place.

### The other half of E1

The graph's version is found by serialising the graph and comparing the text with the last. That happens when the graph is asked for (`GET /pipelines/{name}/graph`) and when something may have changed it (an edit, a restart, a reload), not on any tick, so there's nothing on a busy path to measure. Counting a version up at each change instead would also change what a version means: a restart that leaves the graph as it was doesn't count one up now (subscribers see it in the restarts), and would then. I left it; Q249 asks.

## Found in passing

- **Applying a change takes 11 ms for 20 modules, and 210 for 503**, in step with the number of modules, and it isn't the lists (P4). It's worth a profile some time: loading and compiling the text is a separate benchmark (`LoadOnly`), so this is the runtime's side, or matching.
- **Checking a token still takes 3.4 µs**, nearly all of it hashing the token with SHA-256 on each request. A small cache from token to capabilities, emptied when the file changes, would take it to nothing; at 400 µs of CPU a request it isn't worth it yet.
- **Reading the host's totals is most of a list-and-host update** (623 KB for 50 pipelines), which E2 couldn't touch.
- **`LspBenchmarks` compiles `src/Meep2.Cli/Lsp/JsonRpcChannel.cs` into the benchmarks**, so if that file starts using other CLI types the benchmarks won't build. Moving `JsonRpcChannel` to a library the benchmarks reference would fix it, if it ever comes to that.
- **`FetchedIncludes` fetches includes one after another.** Siblings could be fetched at once; with a handful of includes it isn't worth the extra code, but a pipeline with many would load faster.

## Decisions I made while building

- **Includes are fetched when the pipeline is read** (by `PipelineSources.ReadAsync`), rather than making the parser asynchronous. The parser stays synchronous for the LSP and files on disk, which don't wait on anything, and the network is touched in one place. `FetchedIncludes` and `IIncludeFetcher` are public in Meeplang, beside `IIncludeReader`, which they implement and mirror.
- **The daemon's commit isn't cancelled when its client goes.** By then the running pipeline has the edit, so the commit is made whatever happens to the request; `PendingBranch.CommitAsync` takes a token for other callers.
- **P3 looks at the file again at once for a token it doesn't know**, so `meep token` followed by a request works without waiting a second. Only a token taken out can keep working, for up to a second.
- **`ScratchBuffer` is in `Meep2.Daemon`**, not `Meep2.Shared`, since only the daemon uses it (Q240 is for helpers Meep's assemblies share). It keeps one buffer a thread rather than one a session, as the audit suggested, since a session's frames are written on whichever threads its streams run on.
- **Benchmarks for dropped items are kept** (`LiveBenchmarks`' 503 modules, `subscriptions --ticks-only` and its list-and-host row, `FeedersBenchmarks`), in the commit with this document, so the numbers above can be checked again.
- **No new type or file in Modules is public**: `Framing` was internal already.

## Questions

**Q248.** `IReconfigurable.ParametersChanged` is synchronous, so Listen.HTTP still waits, outside its lock now, for a new path to be added to its server, which can mean waiting while another Listen starts a server on another port. Making it `ValueTask<bool> ParametersChangedAsync(...)` would end that, but it's in the SDK that plugins are written against, so every plugin that takes new parameters would change. Change it before the SDK is packaged (goal 9), or leave it? I'd change it then, with goal 9, rather than now.

**Answer:** Okay, leave it for goal 9.

**Q249.** The graph's version is found by serialising the graph and comparing it with the last (E1's other half), only when the graph is asked for or may have changed. Leave it as it is, or count a version up at each change, which would also count one for a restart that leaves the graph as it was? I'd leave it.

**Answer:** Leave it.

**Q250.** With P3, a token taken out of the tokens file can still be used for up to a second. Is that all right, or should taking one out (there's no command for it yet) tell the daemon at once?

**Answer:** 