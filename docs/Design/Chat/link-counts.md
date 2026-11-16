# Counts for each link

Written 16 November 2026. The second of Synesthesia's asks (prompt 2 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`): a count of the messages each link has carried, in the stats the daemon sends, keyed as the graph's links are, so the pipeline canvas can draw particles along each wire. This covers what was built, what it cost, the decisions I made while building it, and questions numbered from 179 with your answers, which are now applied (see "Applying the answers").

## What's built

### `links` in the stats

`GET /pipelines/{name}` (`PipelineDetail`) and every `stats` event on the stats stream (`GET /pipelines/{name}/stats`, and the same over `/ws`) now carry `links`, beside `modules` and `graphVersion`. For two Intervals in a `+` chain into one WriteLine:

```json
{
  "name": "merge", "state": "finished", "graphVersion": 1,
  "modules": [ … as before … ],
  "links": [
    { "key": "m1>m3", "carried": 4, "errors": 0 },
    { "key": "m2>m3", "carried": 2, "errors": 0 }
  ]
}
```

- **Keys** are the graph's link keys, `from>to` from the modules' lasting keys, so `links` and the graph's `links` match one for one. They're in the graph's order too: grouped by the module each goes to, in the order of its inputs.
- **Counts go up** from zero when the pipeline starts, like the module counts, so a client takes the difference between two updates, and a missed update loses nothing.
- **A reload or edit that keeps both ends of a link keeps its count.** A new link starts from zero, and a link to or from a removed module goes. A restart starts every link from zero, under the same keys, as it does the module counts.
- **What counts as carried:** each message that goes along the link to the module at its end, whether that module takes it in, stops it (drops, expires or refuses it), or lets it pass by to the modules under it. So an error that passes a module with `@no-errors` is counted on the link into it (Q179). Not the other messages that stop at a module with `@errors`, so a link on the `errors` port counts only errors.
- **`errors`** is how many of `carried` were errors (Q180), so a wire can show error particles in their own colour.
- `links` is null until the pipeline has loaded, and a finished pipeline keeps its final counts, as `modules` does.

### Why module counts weren't enough

You found a Where whose `emitted` said 424 though it received 2,123 and the module after it received 1,699. That's how `emitted` is meant to work: it's documented as "messages the module emitted, not counting ones it passed on unchanged". A filter passes on the messages it keeps (they go straight on to the modules under it, from the thread that delivered them), and only its errors are emitted, so 2,123 = 1,699 passed on + 424 errors. I haven't changed `emitted`, but each module now has a `passed` count beside it (Q181), so your Where would show received 2,123, passed 1,699, emitted 424.

With link counts, the wire after a filter is drawn from its own count, and a module with several modules under it has a count for each.

### In the runtime

`Pipeline.GetLinkStats()` gives a `LinkStats` (from, to, input, carried) for each link, in the graph's order. Each link has a counter of its own, made when the pipeline is wired; when a change rewires a running pipeline, a link between two modules that were linked before takes the same counter object, so nothing counted in between is lost. `DeliverTo`, which every delivery goes through (from an emit, a passthrough's pass-on, an error or bypass forwarded past a module, or Distribute's choice of copy), increments the link's counter once, and its error counter too if the message is an error, unless the message stops there because the receiving module has `@errors` and it isn't one.

### In the daemon and for C# clients

`Graphs.LinkCounts(live, pipeline)` puts the modules' keys on the runtime's counts, using a new `LivePipeline.ModuleKeys()`: a map from each running module to its key, much cheaper than building the whole graph each second. `PipelineDetail.Links` is a list of `LinkDetail(Key, Carried, Errors)`, and `PipelineDetail.Carried("m1>m2")` reads one. `DaemonClient.GetAsync` and `WatchAsync` return them with no change to how they're called; their comments say so.

The contract is documented where the rest of the API is: the comments on `PipelineDetail.Links` and `LinkDetail` in [Contracts.cs](../../../src/Meep2.Daemon/Contracts.cs), `GraphLink.Key` pointing to them, and the API list on [MeepDaemon](../../../src/Meep2.Daemon/MeepDaemon.cs).

## Tests

Nine in [LinkCountTests.cs](../../../tests/Meep2.Tests/LinkCountTests.cs), each checking exact counts after the pipeline has run to the end:

- **A `+` chain** of two Intervals (4 and 2 ticks) into one WriteLine: `m1>m3 4; m2>m3 2`, with the same keys as the graph.
- **Zip's numbered inputs** (3 and 2 messages, `within=50ms`): `m1>m3 3` on input 1, `m2>m3 2` on input 2, and `m3>m4 3 (1 error)` for the two pairs and the error for the message left without a partner.
- **Fan-out after a filter:** 10 ticks into `Where msg.Count <= 3` with two WriteLines under it: `m1>m2 10; m2>m3 3; m2>m4 3`, and Where's own counts say received 10, passed 3, emitted 0.
- **An `@errors` branch:** 6 ticks into a Where that divides by zero on even ticks, with three WriteLines under it, `@errors`, plain and `@no-errors`: `m2>m3 3` (all errors), `m2>m4 6` (3 of them errors), `m2>m5 6` (the 3 errors pass by). Where's counts add up: received 6, passed 3, emitted 3.
- **A `Tap … @errors`** on that Where: the tap's link counts only the errors, and the `@no-errors` WriteLine's link counts the errors going past it.
- **Distribute 2**: each message counted on the one copy it went down, 8 in all, and Distribute's `passed` is 8.
- **A passthrough** (a test module that records and passes on): received 4, passed 4, emitted 0, so a message isn't counted twice when a passthrough also passes it on itself.
- **A restart** (`LivePipeline.Rebuild`): the same keys, all at zero.
- **A reload through the daemon** that keeps one link and adds another: an Interval feeding one WriteLine runs until `m1>m2` has carried at least 3, then a second WriteLine is added beside it. `m1>m2` keeps its count (it's ahead of the new `m1>m3` by at least what it had before the reload), `m1>m3` starts from zero, the keys match the graph's, and the same counts come over `/ws` in a `stats` event.

## What it cost

The runtime's throughput benchmarks, `ChainBenchmarks` and `BranchBenchmarks` in `benchmarks/`, times per message end to end. Run-to-run noise on this machine is large: the two runs of the unchanged code closest together differed by up to 40% on the chain cases, and other work kept the load average between 8 and 15. So I ran them in pairs, before and after, from a clean checkout of `6d496dd` and from this change, four times, the last pair in the opposite order, and then FourBranches alone twice more:

| Benchmark | Pair 1 | Pair 2 | Pair 3 | Pair 4 (after first) | FourBranches alone |
|---|---|---|---|---|---|
| FourBranches | 5.09 → 5.60 | 5.27 → 5.34 | 4.32 → 5.07 | 4.66 → 5.09 | 5.08 → 5.56, 4.81 → 5.07 |
| Chain, 1 step, passed on | 1.63 → 1.60 | 1.26 → 1.27 | 1.11 → 1.16 | 1.29 → 1.17 | |
| Chain, 1 step, new message | 2.66 → 2.57 | 1.73 → 1.81 | 1.58 → 1.70 | 1.80 → 1.67 | |
| Chain, 4 steps, passed on | 3.12 → 2.38 | 1.71 → 1.79 | 1.65 → 1.75 | 1.83 → 1.76 | |
| Chain, 4 steps, new message | 4.45 → 3.83 | 2.80 → 2.55 | 2.44 → 2.62 | 3.01 → 2.46 | |

Microseconds per source message, before → after. Pairs 1 and 2 were run before I fixed the counters' padding (below); pairs 3 and 4 and the last column are the code as it is now. Allocation is the same before and after in every case (89 B for FourBranches; 109 to 504 B for the chains).

- **The chains show no cost I can measure.** The differences go both ways, and when the order was reversed in pair 4, the change came out faster in every chain case. A chain has one link per step, so 2 to 5 increments per message.
- **FourBranches is slower in all six pairs, by 1% to 17%, about 9% in the middle:** roughly 0.4 µs per source message, or 0.1 µs for each of its four links. That's more than an uncontended `Interlocked.Increment` costs by itself (a few nanoseconds). On x86 a locked instruction waits for the thread's earlier writes to finish, and here the source has just written into the sinks' buffers, which the sinks' threads are reading. It's the worst case: four deliveries per message from one thread to modules that do nothing. A module that does any real work per message would hide it.
- **A plain increment** (`_carried++`, not atomic) recovered about half of it, but noisily (4.85 and 5.31 µs against the baseline's 5.08 and 4.81). It would lose counts when two threads deliver along the same link at once, as they do after a passthrough that several modules feed, so I kept the atomic one. See Q182 for a way to get most of it back without losing counts.

In detail:

- **On the hot path:** one `Interlocked.Increment` on the link's counter per delivery, after a check of the receiving module's `@errors` option that `DeliverTo` already made (it's moved up, not added). Messages that come in along no link, as an injected one does, count on a shared counter nobody reads, so there's no extra branch.
- **No allocation per message.** Each counter takes 80 bytes, a 64-byte cache line and the object's header, so two links counted on different threads never share a line. A pipeline with 100 links has 8 KB of counters. (My first version asked for 64 bytes with `StructLayout(Size = 64)`, which the runtime ignores on a class: a throwaway allocation test showed 24 bytes, so the padding is now a field at the end of the line, and the same test shows 80.)
- **Per stats update:** a walk over the links and a map of the modules' keys, which grows with the size of the pipeline, not its traffic.
- **Code:** about 220 lines in twelve files in `src`, half of them comments, and a new test file of about 220 lines.

## Decisions I made while building

- **`links` is a list of `{key, carried}`** rather than an object keyed by link, to match `modules`, keep the graph's order, and leave room for more counts on a link later (Q180) without changing its shape.
- **Counted where the message is delivered, not where it's emitted,** because one emit goes down several links, and a message can reach a module by being passed on or forwarded past one without being emitted again.
- **What goes along the wire decides what a link counts.** First built so the port decided, which left errors passing an `@no-errors` module uncounted on the way in; Q179 changed that. Now anything that goes along the link counts, even if the module then stops it, except other messages stopping at an `@errors` module, which `DeliverTo` filters out. The module's own counts say what became of each.
- **Counters are handed on through changes by module, not by key.** The runtime knows nothing of keys, so a reload keeps a link's count when both its modules are kept (which is also when they keep their keys). The two always agree, because the daemon's keys come from the same matching.
- **The daemon puts keys on the counts each time** from `LivePipeline.ModuleKeys()`, by the module's identity object rather than its number, so a module renumbered by a reload can't be mistaken for another. In the moment between a change being applied to the running pipeline and its keys being worked out, a link whose module has no key yet is left out of that one update.

## Questions

179. An error that reaches a module with `@no-errors` isn't taken by it, but it does go on past it to the modules under it. So the link into the `@no-errors` module doesn't count the error (its port says it carries none), but the links out of that module do, and an error particle would appear from the module rather than arriving at it. Is that the drawing you want, or should the link in count errors that pass by, though its port is `no-errors`?

     **Answer:** Should count errors.

180. Errors go down every `main` link as well as `errors` links (as the graph's types say), so on a `main` wire the count mixes messages and errors: `m2>m4 6` in the test is 3 of each. Would you like a second count on each link, `errors`, so Synesthesia can draw error particles in their own colour? It would cost a second increment only for errors, which are rare, and nothing for other messages.

     **Answer:** Yes.

181. A filter's `emitted` leaves out what it passed on, which is why your Where showed 424 of 2,123. Now that the links after it are counted, is that enough, or would you also like a module count of what it passed on (`passed`), so a module's counts add up on their own?

     **Answer:** Yes.

182. If the cost on fan-out matters, the counting could move to the receiving end: each module's own task counts the messages it takes from its buffer by input number (one writer, so a plain increment), and only the rare paths, messages forwarded past a module or stopped before its buffer, count atomically on a second counter that's added in when read. It would make a link's count trail by what's waiting in the buffer, and it's more code in the part of the runtime that is hardest to get right. Is about 0.1 µs per link, measured fanning out to modules that do nothing, acceptable, or should I do that?

     **Answer:** Don't do that.

## Applying the answers

- **179:** `DeliverTo` now counts a message on the link when the module takes it, or when it goes past the module: an error always does, and with `@bypass` so does any message. Only a non-error stopping at an `@errors` module isn't counted. The graph's port for such a link is still `no-errors`, which says what the module takes, not what the wire carries.
- **180:** each link has `errors` beside `carried`, in `LinkStats`, `LinkDetail` and the JSON. It's a second field on the counter's cache line, incremented only for errors, behind a check of a value `DeliverTo` already has.
- **181:** `ModuleStats.Passed` (`passed` in the JSON) counts messages a module passed on unchanged: a filter's kept messages, every message a passthrough takes, and every message Distribute routes. It's also a new `passed` series in the OpenTelemetry counts and a PASSED column in `meep stats`. A passthrough that also passes a message on by emitting it counts it once.
- **182:** left as it is.

I haven't benchmarked again after these: the load average was over 40 when I'd have run them, which would swamp anything. The hot path gained one branch on a local value for the error count and two more booleans in the test of whether to count, which I wouldn't expect to measure; `passed` is an increment on paths that already increment `received` or deliver onwards.

## The test run

With the answers applied, the full suite of 521 tests passed in one run (load average about 40, no warnings in the build): the 512 there before, and the 9 new ones. Before the answers, 520 passed the same way. The link counts' tests also passed on their own each time I changed them. The six tests that failed under heavy load in the graph session's runs, and the Zip timing test that failed in two of them, all passed here.
