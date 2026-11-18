# One subscription per connection, digests, and who a host is

Written 17 November 2026. The fifth of Synesthesia's asks (prompt 5 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`, for Synesthesia's step 7, host and mesh): one subscription on each `/ws` connection that asks the daemon only for what's on screen, each thing at its own rate, sending only what changed, as JSON or CBOR; digests of each module's handling time, the bytes network inflections move and the host's runtime; and a lasting identity and clock for each daemon, in the API and in OpenTelemetry. This covers what was built, what a client has to do, what it cost, the decisions I made while building it, what SSH forwarding needs, and questions numbered from 202 with answer lines, now answered (18 November), with what each answer changed. Nothing is committed; the work is on the branch `subscriptions`, made from `main` at 001842b.

## What's built

### A subscription

A subscription is a stream on `/ws`, started like any other request, with a plan of what it's for:

```json
{"id": "s", "method": "POST", "path": "/subscribe", "json": {
  "encoding": "cbor",
  "list": "2s",
  "host": "1s",
  "runtime": "1s",
  "pipelines": {
    "*":    { "totals": "2s" },
    "site": { "totals": "1s",
              "modules": { "*": "100ms", "m7": "1s" },
              "links":   { "*": "100ms" },
              "timing":  { "*": "1s" },
              "bytes":   { "*": "1s" } }
  }
}}
```

- **`list`:** the pipelines and their states, as `GET /pipelines` has them, with `graphVersion`.
- **`host`:** totals for the whole host, how many pipelines are in each state, and where its Listens are bound.
- **`runtime`:** the daemon's CPU, memory, garbage collection and thread pool from .NET's runtime metrics, and the machine's load and memory.
- **`pipelines`:** for each pipeline by name, or `"*"` for every pipeline not named: its `totals`, and for each module or link by key (`"*"` for the rest), its counts (`modules`, `links`), its handling-time histogram (`timing`) and its bytes (`bytes`).
- **Rates** are durations as Meeplang writes them, from 50 ms, as the stats stream takes them. Nothing is sent for what isn't in the plan.
- **A named pipeline's entry is all that's asked for it.** It doesn't add to `"*"`'s, so a pipeline on the canvas is named with what it needs, and the rest get `"*"`'s totals. Names not running yet are kept, and served once they are.
- **`heartbeat`** (5 s unless given): an update goes out at least this often, with the clock, even when nothing changed.
- **`budget`** (bytes a second, none unless given): updates wait so that bytes sent stay under it on average, with a second's worth allowed at once. Synesthesia's planner keeps under its budget by choosing rates; this is a backstop, which delays rather than drops, so nothing is lost. An update the budget held back says so, with `held` (Q210).
- **`encoding`:** `json` (the default) or `cbor`, chosen when the subscription starts.

Only `observe` is needed. Over plain HTTP, `POST /subscribe` is refused with 400, saying to use `/ws`, since a subscription is changed as it goes.

### Changing it in place

```json
{"id": "s", "change": {"pipelines": {"site": {"modules": {"m9": "100ms", "m7": null}}, "ticker": null}}}
{"id": "s", "resend": true}
```

- **`change`** is a JSON merge patch (RFC 7396) to the plan: objects merge, a value replaces, and null takes something out. The camera adds, drops and retunes pipelines and modules this way, without reconnecting. Things newly asked for go out whole in the next update, straight away; things dropped are forgotten, so asking for them again sends them whole.
- **The daemon answers a change** with a `plan` event, the whole plan as it now stands. A change that would leave something that isn't a plan (a rate under 50 ms, an unknown field, another encoding) is refused with a `problem` event, `{"error": "…"}`, and the plan stays as it was.
- **`resend`** sends everything whole in the next update, as after a gap in the sequence.
- **`cancel`** ends it, as for any stream.

### What it sends

The first event is `hello`:

```json
{"host": {"id": "01d21f38-8095-49ee-af20-9a71904eee47", "names": ["studio", "studio.local"],
          "addresses": ["192.168.1.20"], "version": "1.0.0", "pid": 37954},
 "clock": 1794955040956883,
 "counts": {"modules": ["received", "handled", "emitted", "passed", "busy", "queued", "dropped", "filtered", "failed", "restarts", "expired", "refused"],
            "links": ["carried", "errors"], "bytes": ["received", "sent"],
            "totals": ["received", "handled", "emitted", "passed", "busy", "queued", "dropped", "filtered", "failed", "restarts", "expired", "refused", "sourced", "bytesReceived", "bytesSent"]},
 "timing": {"scale": 3, "unit": "s"},
 "plan": { … }}
```

Then `update`s. These two are from the test pipeline `Listen` → `Where` → `Send text="hello"`, after three requests, then one more (runtime cut short):

```json
{"seq": 1, "clock": 1794955041088849, "bytes": 819,
 "list": {"site": {"path": "/…/site.meep", "state": "running", "started": 1794955040174881, "restarts": 0, "watching": false, "graphVersion": 1, "name": "site"}},
 "host": {"totals": {"received": 6, "handled": 6, "emitted": 3, "passed": 6, "busy": 12123, "queued": 0, …, "sourced": 3, "bytesReceived": 9, "bytesSent": 15},
          "states": {"running": 1},
          "listens": [{"pipeline": "site", "module": "m1", "label": "Listen #1", "address": "http://127.0.0.1:59638/b/"}]},
 "runtime": {"dotnet.gc.collections": {"gen0": 0, "gen1": 0, "gen2": 0}, "dotnet.process.cpu.time": {"system": 0.28, "user": 2.27},
             "dotnet.process.memory.working_set": 74891264, "host.load": {"1m": 17.3, "5m": 13.1, "15m": 12.0}, "host.memory.available": 34359738368, …},
 "pipelines": {"site": {
   "graph": {"version": 1},
   "totals": {…},
   "modules": {"m1": [0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0], "m2": [3, 3, 0, 3, 1275, 0, 0, 0, 0, 0, 0, 0], "m3": [3, 3, 0, 3, 10848, 0, 0, 0, 0, 0, 0, 0]},
   "links": {"m1>m2": [3, 0], "m2>m3": [3, 0]},
   "bytes": {"m1": [9, 15]},
   "timing": {"m2": {"count": 3, "sum": 0.001275, "min": 0.0000164, "max": 0.00123, "zero": 0, "offset": -128, "buckets": [1, 0, 0, 0, 0, 0, 1, 0, …, 1]}, "m3": {…}}}}}

{"seq": 2, "clock": 1794955041146269, "bytes": 3268,
 "host": {"totals": {"received": 8, "handled": 8, "emitted": 4, "passed": 8, "busy": 12241, "sourced": 4, "bytesReceived": 13, "bytesSent": 20}},
 "pipelines": {"site": {
   "totals": {…},
   "modules": {"m1": [null, null, 4], "m2": [4, 4, null, 4, 1294], "m3": [4, 4, null, 4, 10947]},
   "links": {"m1>m2": [4], "m2>m3": [4]},
   "bytes": {"m1": [13, 20]},
   "timing": {"m2": {…}, "m3": {…}}}}}
```

- **`seq`** counts updates from 1, so a client can tell it missed one. Updates go out in order on one connection, so a gap means a bug or a lost connection; `resend` recovers.
- **`clock`** is the daemon's clock as the update was made, in microseconds since 1970 (UTC), on every update.
- **`bytes`** is what the subscription has sent before this update, its own frames included, so a client knows its bandwidth exactly.
- **`held`**, only on an update the `budget` held back: how long it waited past when it was due, in microseconds. The daemon doesn't lower rates itself; this tells the client's planner to (Q210).
- **A module's counts** are an array in the order `hello` gives. After the first time, a count that hasn't changed is null, and nothing after the last one that changed is sent: `[null, null, 4]` is "emitted is now 4". Counts are cumulative, as everywhere else, so a client takes differences.
- **`busy`** is new: how long the module has spent handling messages, in microseconds, the sum of its histogram. Its rate of change is how busy the module is, 0 to 1 per message it handles at once, so load can be drawn at any zoom without histograms (Q206).
- **Links** are `[carried, errors]` by the graph's link key; **bytes** `[received, sent]`, only for modules that count any.
- **Totals and host totals** are named, only what changed: each count added up over the pipeline's modules, with `sourced`, what its sources emitted (messages coming in), and its bytes. A pipeline drawn as a dot needs nothing else.
- **`timing`** is each module's histogram, whole, whenever it has changed (Q205). Sources handle nothing, so have none.
- **Something gone** is null: a module or link an edit removed (`"m4": null`), a pipeline that's gone from the daemon (`"site": null` in `pipelines` and in `list`).
- **`graph`** comes with a pipeline's first update, `{"version": 3}`, and whenever its graph changes, `{"version": 4, "reason": "edit"}`, straight away, whatever the rates, as the stats stream's `graph` event does. Ask for the graph again when it says so. A restart that leaves the graph as it was shows as `restarts` in the list, with counts starting again.
- **The runtime** is each metric by its OpenTelemetry name: a number, or for one with attributes, numbers by the attributes' values. Only metrics that changed are sent.

**CBOR** carries the same events, with the same keys and the same shapes, each as a binary frame holding a CBOR map `{"id", "event", "data"}`. Whole numbers go as integers, others as the smallest float that holds them exactly. Requests and changes stay JSON text. `DaemonSocket.ReceiveAsync` turns a CBOR frame back into the same JSON, which the tests use to check that both carry the same values.

### When updates go out

- **Each thing asked for has a timer at its rate, on one grid**, so things at 100 ms and 200 ms go out in the same update, and when several fall due, one update carries them all. Something newly asked for is due at once; after that it's due at the next multiple of its rate, so the second update for a 1 s item can come sooner than a second after the first.
- **An update with nothing in it isn't sent**, apart from the heartbeat. A pipeline that has finished sends nothing more after its first update.
- **The next update isn't made until the last is on the socket.** On a slow link (SSH, say) updates come less often and carry more, rather than queueing in the daemon; with nothing lost, since each has everything that changed since the one before. With a `budget`, they also wait for it.
- **A graph change wakes the subscription** at once, as does a change of plan.

### Digests

**Handling time.** Each module now times every message it handles (`Pipeline.GetHandlingTimes`), into a histogram in OpenTelemetry's exponential form at a fixed scale of 3: eight buckets to each doubling, each about 9% wide, from 1 ns to 18 hours. Bucket `i` holds times above `2^(i/8)` seconds and up to `2^((i+1)/8)`, as OpenTelemetry maps them, exact powers of two included. Every module's histogram has the same scale, so a pipeline's or a host's is the sum of its modules', bucket by bucket at the same index, at any zoom, without asking the host again (Q204). The subscription sends `count`, `sum`, `min`, `max`, `zero`, `offset` and the buckets from `offset`, trimmed of empty ones at either end, all cumulative. Recording costs two clock reads, up to seven comparisons and a few plain increments a message: only the module's own loop records, one message at a time, so no locked instructions are needed, and the bucket is found from the time's bits, as OpenTelemetry suggests, rather than by a logarithm (see the costs below, Q203). A test checks the lookup against the logarithm for 10,000 random times. The OpenTelemetry histogram `meep.module.duration` is still recorded only while something listens.

**Bytes.** The SDK has `ModuleContext.CountBytes(received, sent)` and `Outlet.CountBytes`, safe from any thread at any time, and `Pipeline.GetTraffic` reads them. The network inflections count:

| Inflection | Received | Sent |
|---|---|---|
| Listen.HTTP | request bodies, as they're read (streamed or not) | the replies written to its requests, whichever module writes them, and the default reply |
| Listen.WebSocket | each message's payload | everything written to its connections, Send.Broadcast's and Send.Reply's included |
| Listen.TCP | every byte read from its connections | every byte written to them |
| Fetch.HTTP | response bodies, streamed ones as they're read | — (it sends no body) |
| Send.HTTP | response bodies | request bodies, streamed ones as they're read |
| Send.WebSocket, Send.TCP | what the server sends, counted for each Send using the shared connection, since each emits it | what this Send writes; a reply through its connection counts for it |

So bytes are counted for the module whose connection they cross: a Listen counts its clients both ways, and Send.Reply and Send.Broadcast count nothing of their own (Q207). It's the payload, without HTTP headers, WebSocket framing or TLS; for a TCP Listen it's the stream itself, delimiters included. Fetch.File reads no network, so it counts nothing.

**The host.** `RuntimeMetrics` listens to .NET's `System.Runtime` meter, the source of the runtime's own OpenTelemetry metrics (`dotnet.process.cpu.time` by user and system, `dotnet.process.memory.working_set`, `dotnet.gc.collections` by generation, `dotnet.gc.heap.total_allocated`, `dotnet.gc.pause.time`, `dotnet.gc.last_collection.heap.size`, `dotnet.thread_pool.queue.length`, `dotnet.process.cpu.count`, and the rest .NET has), and adds `host.load` (1, 5 and 15 minute load averages, from `getloadavg`) and `host.memory.available` (the machine's memory, or a container's limit), which .NET's metrics don't give. They're read when a subscription asks, at most every 100 ms however many ask. CPU headroom is `dotnet.process.cpu.time`'s rate against `dotnet.process.cpu.count`, and the machine's load against the same.

### Who a host is, and its clock

- **A lasting ID:** a UUID made the first time the daemon runs and kept in `daemon/id` in Meep's folder (`~/.meep/daemon/id`), beside the socket and tokens. Two daemons starting at once agree on one. `MeepDaemon.Id` has it.
- **The machine it was made on** is kept beside it, on a second line, `machine <hash>` (Q208). The hash is an HMAC of the machine's own ID (the hardware UUID on macOS, from `gethostuuid`; `/etc/machine-id` on Linux) with a key of Meep's, so the file doesn't hold that ID itself, as machine-id(5) asks. A daemon that finds itself on another machine takes a new ID, made from the old one and the new machine, so two daemons starting at once there from one copy agree on it, and writes it with the new machine. Where there's no machine ID to compare (Windows, or none readable), the ID is used as it is. A file from before, with no machine line, is taken as made here, and the machine is added.
- **`meep daemon socket`** prints the socket's full path and the daemon's ID, a line each: the running daemon's, or the one it will take when it starts, with a note on standard error that none is running (Q209). So `ssh host meep daemon socket` can come before `ssh -L`.
- **`GET /`** now also has `id`, `names` (the machine's name, its host name, and that name in full as DNS gives it within a second), `addresses` (its addresses on the networks it's on, IPv4 first, without loopback and link-local ones), `listening` (each Listen's pipeline, key, label and address), and `clock`. A client can estimate the offset between its clock and the daemon's from any request: half way between sending and receiving, the daemon's clock read `clock`.
- **Where Listens are bound:** the SDK has `ModuleContext.Listening(address)`, which Listen.HTTP, Listen.WebSocket and Listen.TCP call once bound, and again when Listen.HTTP moves to a new path; the runtime forgets it when the source stops (`Pipeline.GetListening`). The address is as bound: `http://0.0.0.0:8080/hooks/` for one on every network, with the host's own addresses beside it, and the real port for a TCP Listen given port 0.
- **The clock** is on every update, as above, and in `hello`.

### The same identity in OpenTelemetry

- **`service.instance.id`** is the daemon's ID on the resource of everything `meep daemon` exports, with `host.name`. `meep run` keeps a new one each run, as OpenTelemetry's default does.
- **`meep.module.key`** is now on every span and metric, beside `meep.pipeline` and `meep.module` that were there: the module's key in the graph (`m3`), which lasts while its number changes, so a collector, or Synesthesia taking OTLP pushes (its Q5), can place a module on its canvas. `LivePipeline` gives the running pipeline its keys (`Pipeline.NameModules`) whenever they change; a pipeline built without Meeplang has none, and its telemetry is as before.
- **`meep.module.bytes`** is a new counter, by `network.io.direction` (`receive`, `transmit`), for modules that count bytes.
- **.NET's runtime metrics** (`System.Runtime`) are now exported with Meep's, so a collector has the host's CPU, memory and garbage collection too.

### Smaller changes

- `PipelineSummary` has `graphVersion`, so `GET /pipelines` says when to fetch a graph again.
- `LivePipeline.ModuleKeys()` is worked out once for each change, not on each call: the daemon asks for it with every update.
- `Pipeline.ReadModules()` reads every module's counts, busy time and bytes in one pass.
- Over `/ws`, `change` or `resend` with an id that's neither a subscription's nor a request still being answered is answered 404. Each request's id is now taken as its frame is read rather than when it starts to be answered, so the 409 for an id in use comes in order too.
- `DaemonSocket` (the .NET client's `/ws`) has `ChangeAsync`, `ResendAsync`, and `Last`, the size of the last frame and whether it was binary; `ReceiveAsync` reads CBOR frames as JSON.
- `benchmarks/Meep2.Benchmarks` takes `subscriptions` as its first argument to measure what's below, outside BenchmarkDotNet.

## What a client has to do

1. Open `/ws` and send `POST /subscribe` with a plan: say, `list` and `host` at 1–2 s, `runtime` at 1 s, `"*": {"totals": "2s"}`, and for each pipeline on the canvas its modules and links at a rate for its size on screen. Choose `cbor` to halve the bytes.
2. Read `hello`: the host's ID (to tell daemons apart, however they're reached), names and addresses, the order of counts, the histogram scale.
3. Keep a model per host, and apply each `update` to it. If updates carry `held`, ask for less, or slower: the budget is holding them back.
4. Apply each update: a value replaces, null in a map removes, null in a counts array leaves that count as it was. Check `seq` goes up by one; if not, send `resend`.
5. Take differences of cumulative values over the `clock`s of two updates for rates and load (`busy`).
6. When the camera settles, send one `change` with what's now on screen and what isn't. Things added come whole in the next update.
7. When `graph` comes for a pipeline, fetch `GET /pipelines/{name}/graph` again.
8. Merge histograms by adding buckets at the same index (all at scale 3): a pipeline's is its modules', a host's its pipelines'.
9. For the clock offset, use `GET /`'s `clock` against the request's round trip, or each update's against when it arrived, taking the smallest delay seen.

The stats stream, taps, inject and every other request work as before, on the same connection.

## What it cost

All on this machine (an 8-core, 16-thread Xeon W-2140B), Release, with other work keeping the load average between 5 and 17, so single numbers move by tens of percent from run to run. `dotnet run -c Release -- subscriptions` in `benchmarks/Meep2.Benchmarks` measures it again.

**The setup:** 50 pipelines of 20 modules each (an Interval every 10 ms and 19 modules in a chain under it), so all 1,000 modules handle 100 messages a second and every count changes between updates: the worst case for sending only what changed. Synesthesia's case of 10 updates a second for all of it is the stats streams at `every=100ms`, one per pipeline, against one subscription with every module and link at 100 ms.

**One tick, timed alone** (the median of 30, 100 ms apart, in the daemon's process while it runs):

| What | Time to make | Allocated | Bytes |
|---|---|---|---|
| Stats streams: each of the 50 pipelines' detail, as JSON | 1.51 ms | 1,393 KB | 256,945 |
| Subscription, JSON: modules and links, what changed in 100 ms | 2.02 ms | 1,170 KB | 48,948 |
| Subscription, JSON: the same, whole | 2.17 ms | 1,407 KB | 62,998 |
| Subscription, CBOR: modules and links, what changed in 100 ms | 2.15 ms | 1,082 KB | 28,815 |
| Subscription, CBOR: the same, whole | 2.15 ms | 1,378 KB | 37,665 |
| Subscription, CBOR: and totals, and every histogram, what changed | 5.31 ms | 2,411 KB | 188,990 |
| Subscription, CBOR: modules alone, what changed | 1.15 ms | 604 KB | 17,914 |
| Subscription, CBOR: links alone, what changed | 1.01 ms | 571 KB | 11,214 |
| Subscription, CBOR: totals alone, what changed | 0.56 ms | 395 KB | 4,264 |
| Subscription, CBOR: histograms alone, what changed | 3.34 ms | 1,751 KB | 167,705 |
| Subscription, CBOR: nothing asked for | 0.13 ms | 27 KB | 52 |

An earlier run, with the load average between 11 and 17, had the stats streams' tick at 2.0 to 5.1 ms and the subscription's at 2.8 to 4.6 ms; the bytes were the same, apart from the JSON before link keys stopped being escaped (53,702 for what changed).

**Live, for ten seconds each**, a client on the socket counting what arrives, set against the phases with no client before and after each:

| Phase | CPU ms/s | With no client | Over that | Allocated MB/s over that | On the wire | Frames/s |
|---|---|---|---|---|---|---|
| Stats streams, `every=100ms`, one per pipeline | 537 | 414 | 123 | 14.6 | 2,509 KB/s | 500 |
| Subscription, JSON: modules and links at 100 ms | 473 | 439 | 34 | 11.7 | 525 KB/s | 10 |
| Subscription, CBOR: modules and links at 100 ms | 513 | 466 | 47 | 10.7 | 281 KB/s | 10 |
| Subscription, CBOR: and totals at 100 ms, histograms at 1 s, list, host and runtime at 1 s | 544 | 484 | 59 | 13.2 | 506 KB/s | 10 |
| Subscription, CBOR: each pipeline's totals at 1 s, as at host zoom | 473 | 483 | (none) | 0.4 | 4.3 KB/s | 1 |

The CPU column is the whole process: the 50 pipelines themselves take about 450 ms a second, and that moves by 50 ms or so between phases with no client at all, so the "over that" column is good to the nearest 50 ms or so. An earlier run under heavier load gave 181 ms for the stats streams and 38 and 109 ms for the JSON and CBOR subscriptions; the order was the same.

What this says:

- **On the wire, the subscription is a fifth of the stats streams in JSON, and a ninth in CBOR**, with 10 frames a second instead of 500. Most of the saving is not sending what each `PipelineDetail` repeats every tick (names, paths, verbs, every count), and the rest is CBOR's numbers. Sending only what changed saves another quarter even here, where everything changes; in a real host, where most modules are idle most of the time, far more.
- **At host zoom, each pipeline's totals once a second cost 4 KB/s and almost nothing on the daemon** for all 50 pipelines and 1,000 modules: what a dot on the mesh costs.
- **Making an update costs about what the stats streams' tick does** (2 to 4 ms for 1,000 modules and 950 links). Both read every module's counters, and that's most of it: those counters are in the caches of the cores running the modules, so reading 1,000 modules' costs about 0.7 µs each, whoever reads them. The subscription's saving in CPU is in not encoding and sending five times the bytes, 50 frames at a time, and the client not decoding them.
- **Histograms are the expensive part**, about 170 bytes and 5 µs each, so 1,000 modules' every 100 ms is 1.7 MB/s. At the 1 s they're meant for, it's 170 KB/s and under 1% of a core. Sending only the buckets that changed would cut the bytes (Q205).
- **Allocation** is about 1 MB an update for 1,000 modules, all short-lived, against 1.4 MB for the stats streams' tick.

**Timing every message**, on the runtime's chain benchmark (a source as fast as it can into 1 or 4 steps; microseconds per source message, `main` → this branch, two rounds each, run alternately):

| Case | `main` | This branch, timing switched off | This branch |
|---|---|---|---|
| 1 step, passed on | 1.14, 1.19 | 1.14 | 1.24, 1.27 |
| 1 step, a new message | 1.50, 1.63 | 1.77 | 1.92, 1.82 |
| 4 steps, passed on | 1.74, 1.84 | 1.69 | 1.81, 1.86 |
| 4 steps, a new message | 2.38, 2.63 | 2.44 | 2.56, 2.70 |

With timing switched off, the branch runs as `main` does (the second case aside, which is slower without timing too, so it's layout or noise rather than timing). With it, each message handled costs about 30 to 55 ns more, mostly the two reads of the clock: 5 to 10% of these benchmarks, whose modules do nothing, and under 1% for a module that takes 5 µs. Allocation is the same, within a few bytes either way, as between runs of `main`. (My first recorder used locked increments and a logarithm; runs of it fell within the same noise, so I can't say it cost more, only that it can't have cost less.)

## Reaching a daemon over SSH

`ssh -L /local/path.sock:/remote/home/.meep/daemon/meep.sock host` works with the daemon as it is:

- **The peer-credential check passes.** sshd connects to the remote socket from the session's own process, which runs as the user who logged in, so `LOCAL_PEERCRED` (macOS) or `SO_PEERCRED` (Linux) reports that user. If it's the user running the daemon, the client gets every capability, as a local one does.
- **Another user can't reach it at all,** token or not: the socket is in a folder only its owner can open (0700), so sshd, as that user, can't connect. Tokens matter only for clients that can reach the socket: the same user wanting less, or root (whom the folder doesn't stop, and the credential check refuses without a token). So nothing in the tokens gets in the way, and they don't open it to anyone else either.
- **The remote path has to be absolute.** ssh doesn't expand `~` in a forwarded socket's path, and `MEEP_HOME` may move it, so the client needs the remote daemon's socket path before it can forward. `ssh host meep daemon socket` prints it, with the ID to check against `hello` once connected (Q209). It needs `meep` on the remote's `PATH` for a non-interactive shell.
- **Forward to a local Unix socket, not a TCP port.** A forwarded TCP port on the Mac lets any local user connect, and the daemon would see them as its own user, with every capability. ssh makes a local socket readable only by you (`StreamLocalBindMask`, 0177 by default); add `-o StreamLocalBindUnlink=yes` so a stale one from a dropped tunnel is replaced.
- **sshd must allow it.** `AllowStreamLocalForwarding` is yes by default; `DisableForwarding yes` or `AllowStreamLocalForwarding no` on the server would stop it, and the client should say so when the forward fails.
- **The WebSocket's keep-alive** (30 s) keeps the tunnel open through idle NATs, and the subscription's heartbeat lets the client notice a dead one within its `heartbeat`.

## Tests

Fifteen in [SubscriptionTests.cs](../../../tests/Meep2.Tests/SubscriptionTests.cs), against a daemon of the test's own, over `/ws`, and one in [DaemonTests.cs](../../../tests/Meep2.Tests/DaemonTests.cs):

- **The first update has everything asked for, whole,** keyed as the graph is: `hello`'s host ID is the daemon's, the count order and scale; the list, the host's states and totals, the runtime's metrics (`host.memory.available` and `dotnet.gc.*`), the pipeline's graph version, every module and link by the graph's keys with every count, totals whose `sourced` is the Interval's `emitted`, the WriteLine's histogram with as many times as messages handled and buckets that add up to its count, and none for the Interval.
- **Later updates carry only what changed:** `seq` goes up by one, a finished pipeline isn't in them at all, and the Interval's array has nulls for received and handled, its new `emitted`, and nothing after.
- **Changed in place:** one module of one pipeline added comes whole, alone, with a `plan` event; a rate of 5 ms is refused with a `problem` and the plan carries on; dropping the pipelines stops anything more being sent for them.
- **A change sent straight after subscribing,** before `hello`, is taken: the plan event, then the module asked for. (Without care it could have come before the subscription was running, and been refused; now each request's id is taken as its frame is read, and changes that arrive early are held for it.)
- **A graph change is sent straight away,** with rates of an hour: a reload gives `{"version": 2, "reason": "reload"}`, the graph's version.
- **Resending** sends a finished pipeline's modules and links again, byte for byte as the first time.
- **CBOR** comes as binary frames, carries the same pipelines and list as JSON, value for value, is smaller, and a finished pipeline's histogram has exactly the messages handled.
- **Over HTTP**, `POST /subscribe` is refused with 400, pointing to `/ws`.
- **A budget** of 1,000 bytes a second holds over three seconds, with a second's worth at once, and the updates it held say so with `held`.
- **Bytes through a Listen:** two requests of 3 bytes, each answered `hello` by Send: the Listen has `[6, 10]` and Send nothing; the pipeline's `bytesSent` is 10; `GET /`'s `listening` and the host's `listens` have the Listen's key and address. (Waiting for `listening` is also how the test knows the port is bound.)
- **A daemon keeps its ID:** made once, the same when asked again, in the file with this machine's (on macOS); `GET /` has it, the machine's name and a clock within a minute of now.
- **A daemon copied to another machine takes an ID of its own** there, keeps it on later starts, and a second copy started there agrees on it.
- **Without a machine ID to compare, a daemon keeps its ID**, and its file as it was; one from before machines were kept is adopted, with the machine added.
- **`meep daemon socket`** prints the socket in full and the ID before a daemon runs (with a note that none is) and while one does, and is in `meep --help`.
- **Handling times go in OpenTelemetry's buckets:** 1 s is bucket -1, 2 s is 7, 0.5 s is -9, 1.05 s is 0, a hair over 2^(1/8) is 1; the lookup agrees with OpenTelemetry's logarithm for 10,000 random times from 30 ps to 9 hours, away from the edges of buckets; and a recorder's count, zero bucket, sum, min, max, offset and buckets for five times.
- **Metrics carry each module's key:** a Meeplang pipeline's counters have `meep.module.key` m1, m2 and m3.

## Decisions I made while building

- **One subscription per connection is what a client needs, but any number may run,** each under its own id. Nothing is shared between them, so two cost twice one.
- **The plan is JSON, changed by merge patch,** so a change says only what moved, in the same shape as the plan, and Swift can build both from one `Codable` type.
- **Module counts are positional arrays, with null for unchanged** (Q202). Names in every entry would double JSON's bytes for 1,000 modules; the order is in `hello`. Totals, few and read by people too, are named.
- **Counts are sent as they are, not as differences**, so a missed update, or one a client drops, loses nothing, as the stats stream's counts do.
- **Updates wait for the last to be sent**, rather than queueing, so a slow connection gets bigger, rarer updates, and the daemon's memory doesn't grow with a client that can't keep up.
- **`busy` joined the counts**, as the sum of each module's handling times, read without making the histogram. It's what the load colouring needs at every zoom.
- **Times in the subscription are microseconds since 1970**, as integers, `started` and `ended` included, so clocks compare with one subtraction. The rest of the API keeps ISO 8601.
- **The runtime's metrics come from .NET's own meter**, so they're the names and meanings a collector shows, and new ones in later .NET versions arrive by themselves.
- **Link keys aren't escaped in JSON.** `>` was written `>`, six bytes for one, in every link of every update; nothing reads these frames as HTML.
- **The ID is a UUID in a file of its own**, not derived from the machine, so two daemons with different `MEEP_HOME`s on one machine are two hosts. Copying a home to another machine now makes a new one there (Q208).
- **Listen addresses come from the Listen**, once bound, not from its parameters, so they're right for port 0 and for a Listen that moved.

Noticed in passing, not changed:

- `examples/github.meep` showed as changed when I started, and was to be left as it was. It now matches the commit, and its file was last written at 20:24, before this session's first command. Nothing I ran touches it, and nothing of mine is in it; I can't tell what changed it back.
- `Interlocked.Read` is a locked compare-exchange on x64, used for every count the API reads. On 64-bit, `Volatile.Read` gives the same guarantee without the lock; it would make reading 1,000 modules cheaper for the stats stream and the subscription alike.

## Questions

202. ★ Module and link counts go as positional arrays, `[received, handled, emitted, …]` in the order `hello` gives, with null for a count that hasn't changed and nothing after the last that did. Named fields (`{"received": 4, "busy": 1294}`) would be easier to read and to extend, at about twice the bytes in JSON and half again in CBOR. Keep the arrays?

     **Answer:** Keep the positional arrays, with null for unchanged. Bytes matter at this scale, and Synesthesia reads the order from `hello`.

     *Kept as built.*

203. ★ Every module now times every message it handles, always, so histograms and `busy` are cumulative from the start, like the counts. It costs about 30 to 55 ns a message handled (5 to 10% of the chain benchmark, where modules do nothing). The alternative is to time a pipeline only once something asks for its timing or `busy`, with the histograms then cumulative from when it was first asked. Keep timing always?

     **Answer:** Keep timing every message, always. Synesthesia needs `busy` at every zoom from the start, and the cost is under 1% for a module that does real work.

     *Kept as built.*

204. Histograms are at a fixed scale of 3 (eight buckets a doubling, about 9% wide) for every module, so they merge by adding. Scale 2 (four a doubling, 19% wide) would halve their size; OpenTelemetry's own default starts finer and scales down to fit 160 buckets, which then has to be matched before merging. Is 3 right?

     **Answer:** Keep the fixed scale of 3: fine enough to show, and it lets histograms merge by adding.

     *Kept as built.*

205. A histogram goes whole whenever it changed, about 170 bytes in CBOR for a busy module. Sending only the buckets that changed since the last update (as `[index, count]` pairs, still cumulative) would make it a few dozen bytes, at the cost of a second form for clients to apply. Worth it, now or later?

     **Answer:** Later. Synesthesia will ask for timing at about 1 s, where whole histograms cost little. Note it in the report as a possible addition.

     *Not built; noted under "Possible additions" below.*

206. `busy` is the 5th count of every module, in microseconds. Fine there, or would you rather it came only with `timing`?

     **Answer:** Keep `busy` in the counts: load can then be drawn without histograms.

     *Kept as built.*

207. Bytes are counted for the module whose connection they cross: a Listen counts the replies written to its clients, though Send writes them, and Send.Reply counts nothing. Counting them for the module that writes would put the replies on Send instead, and a Listen would show only what came in. Which suits drawing flows and the boundary (prompt 7)?

     **Answer:** Keep counting bytes for the module whose connection they cross. That gives each outside endpoint its own bandwidth, which is what the boundary needs (prompt 7).

     *Kept as built.*

208. The daemon's ID is a UUID kept in `daemon/id` under Meep's folder. Copying the folder to another machine copies the ID, so two daemons would claim to be one. Should the daemon also keep the machine it was made on, and make a new ID if it finds itself on another? (macOS and Linux both have a machine ID it could compare.)

     **Answer:** Yes: keep the machine's ID beside the daemon's ID, and make a new daemon ID when the daemon finds itself on another machine. Where the machine has no ID to compare, keep it as it is now. Test both.

     *Done: see "Who a host is" above. `HostIdentity.MachineId()` reads the machine's ID and hashes it; `IdOf` keeps it on a second line of `daemon/id` and makes a new ID on another machine. Two new tests: one for a folder copied to another machine (a new ID, the same on later starts, and the same for a second copy started there), one for no machine ID to compare (the ID and file as they were; an older file adopted with the machine added; a new one written without a machine line). The ID test also checks this Mac has a machine ID, the same each time, and that it's kept.*

209. To forward a remote daemon's socket, Synesthesia needs its full path first. Add `meep daemon socket`, which prints the socket's path (and the ID), so `ssh host meep daemon socket` can come before `ssh -L`? It would also suit scripts.

     **Answer:** Yes: add `meep daemon socket`, printing the socket's path and the daemon's ID, so `ssh host meep daemon socket` can come before `ssh -L`. Test it, and add it to the CLI's help.

     *Done: see "Who a host is" above. In `meep --help` after `meep daemon stop`. Tested in `DaemonTests`: the path in full and the ID before a daemon runs, with the note on standard error; the same two lines and no note while one runs; and the help line.*

210. The `budget` delays updates so bytes stay under it on average. Should the daemon instead lower rates itself when over budget (say, doubling every period), so the client learns of it in the `plan`? Synesthesia's planner is meant to do this, so I've kept the daemon's part simple.

     **Answer:** Keep the daemon's budget as a simple delay; don't have it lower rates itself. But have each update that the budget held back say so, with how long it waited, so the client's planner knows to lower its rates. Test it with the budget test.

     *Done: `held`, in microseconds, on an update the budget held past when it was due. The budget test now also checks that some update over its three seconds has `held`, under three seconds' worth. In the same change, a graph change or a change of plan that comes while the budget holds now goes as soon as the budget allows, rather than waiting for the next tick as well.*

211. A pipeline named in the plan replaces `"*"`'s entry for it rather than adding to it, so a pipeline on the canvas has to repeat `totals` if it wants them. The alternative is merging the two. Keep replacing?

     **Answer:** Keep replacing: a pipeline named in the plan replaces `"*"`'s entry for it.

     *Kept as built.*

## Possible additions

- **Histograms by the buckets that changed** (Q205). A histogram goes whole whenever it changed, about 170 bytes in CBOR for a busy module. Sending only the buckets that changed since the last update, as `[index, count]` pairs, still cumulative, would make it a few dozen bytes, at the cost of a second form for clients to apply. Not needed while Synesthesia asks for timing at about 1 s; worth it if timing at 100 ms over many modules becomes common.

## The test run

After the answers (18 November): the build has no warnings, and the full suite of 601 (the 598 below and the three new tests) passed three times running.

Before them: the build had no warnings. The full suite was 598: the 585 there before, and these 13. In the runs while I worked, two tests failed that I then fixed:

- **My own bytes test** sent its requests before the Listen had bound its port. It now waits for the daemon to say where the Listen is.
- **`The_meep_command_edits_reloads_and_shows_a_running_pipeline`** in `WriteBackTests.cs` failed in two of three full runs on this branch and in neither of two on `main`, and passed alone every time. It waited until `meep list` printed "running", which "Nothing is running." also matches, so under load it could edit before the daemon had started its pipeline. On this branch the pipeline starts a few milliseconds later after the daemon opens its socket (more to compile on the first load), which was enough to land in that gap. It now waits for the pipeline's own line.

Once one other, `Listens_on_one_port_take_their_own_paths`, failed in a full run under load; it passed alone five times out of five and in every full run since. After the fixes, the full suite passed four times running.
