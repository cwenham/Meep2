# Step 7: hosting

Written 9 November 2026. This covers what step 7 built, the decisions I made while building it, benchmark numbers, and questions numbered from 108 with answer lines.

## What's built

- **The daemon** (goal 8): `meep daemon` runs pipelines in the background until Ctrl+C or `meep daemon stop`.
  - Each pipeline is loaded by its own bootstrap pipeline, as `meep run` loads one: Read → Parse → Install → Build → Host.
  - A pipeline is named after its file, so `ticker.meep` is `ticker`.
  - Its WriteLine output goes to the daemon's standard output. What happens to it goes to the daemon's log, on standard error: starting and finishing, warnings, and errors it leaves unhandled.
  - Stopping the daemon stops its pipelines first, letting their messages finish.
- **A control API** (Q63): HTTP with JSON, on a Unix socket in `~/.meep/daemon`.
  - The socket's folder is one only you can open, and the daemon asks the system which user connected. Only the user running the daemon gets in, unless the client shows a token.
  - Remote access is through an SSH tunnel to the socket, which arrives as your own user.
  - Anything that can speak HTTP over a Unix socket can use it, such as `curl --unix-socket ~/.meep/daemon/meep.sock http://meep/pipelines`.
- **Capabilities** (Q40, Q63): each client may observe, inject or control.
  - **observe:** list pipelines, see each module's counts, and tap the messages passing a module.
  - **inject:** send test messages into a running pipeline.
  - **control:** start and stop pipelines, and stop the daemon.
  - The daemon's own user has all three. `meep daemon token <name> observe` makes a token for a client that should only observe, such as a dashboard. Tokens are kept as their SHA-256s in `~/.meep/daemon/tokens`, so the file doesn't hold the tokens themselves.
- **A live stream** (Q64): each module's counts every second, or as often as asked, as server-sent events. The counts now include how many messages wait in each module's buffer.
- **Debug taps** (Q64, design section 4): the messages passing any module, as JSON.
  - A tap can have a condition, written in Meeplang, and a sample rate.
  - It has its own small buffer that drops its oldest message when the reader falls behind. It holds no leases, and doesn't count towards halting, acknowledgements or error handling.
  - With no tap open, the cost is one check of a field per message.
  - Messages are written as JSON for display: type, id, emitter, expiry, leases (`lease 12 (open)`), content, and the nearest ten ancestors in fam. Long text is cut short, bytes are shown as a count, and streamed bodies are never read.
- **Injecting** (Q72): a test message goes into any module as if it came from upstream, or on from a source as if the source had emitted it. Text arrives as a `TextMessage`, a new core message type, and JSON as a `JsonMessage`.
- **OpenTelemetry** (Q43):
  - **Spans:** one each time a module handles a message, named by verb and inflection, such as `Fetch.HTTP`. A span's parent is the span its message came from, so a trace follows fam: it starts with a short span for the source emitting the message. A message with several parents links to the others' spans. A module that throws marks its span as failed.
  - **Metrics:** messages through each module by what happened to them, failures and restarts, queue depth, and how long each module takes to handle a message.
  - **Exporting:** `meep run --otlp http://localhost:4318` or `meep daemon --otlp …` sends them over OTLP/HTTP, to a collector or the Aspire dashboard, say. The standard `OTEL_EXPORTER_OTLP_ENDPOINT` variables work too. Without either, nothing listens, and it costs nothing.
- **New commands:** `meep daemon`, `meep daemon stop`, `meep daemon token`, `meep start`, `meep list`, `meep stop`, `meep stats [--watch]`, `meep tap`, `meep inject`. A client shows a token by setting `MEEP_TOKEN`.
- **Tests:** 337 TUnit tests, 23 of them new. The daemon tests run real daemons on real sockets, and one sends spans and metrics from `meep run` to a test collector. The suite passed 8 runs in a row. Breaking the tap at emitting modules, the capability check and the spans' parents on purpose made their tests fail, and the code is restored.

## Using it

```
meep daemon &                                  # or in a terminal of its own
meep start ticker.meep
meep list
NAME    STATE     STARTED   FILE
ticker  running   15:13:21  /Users/you/ticker.meep

meep stats ticker
MODULE         RECEIVED    HANDLED    EMITTED   QUEUED   DROPPED   EXPIRED   FAILED
Interval #1           0          0          3        0         0         0        0
WriteLine #2          3          3          0        0         0         0        0

meep tap ticker 1 --where 'msg is Tick and msg.Count % 2 == 0'
{"type":"Tick","id":"01a1c0df-…","created":"2026-11-09T15:13:23.16Z","emittedBy":"Interval #1","content":{"Count":6,"Payload":null}}
{"type":"Tick","id":"01a1c0df-…","created":"2026-11-09T15:13:23.76Z","emittedBy":"Interval #1","content":{"Count":8,"Payload":null}}

meep inject ticker WriteLine "hello from inject"
Sent TextMessage 01a1c0df-… into WriteLine #2.

meep stop ticker
meep daemon stop
```

A module is named by its `as` name, its number, or a verb or inflection only one module has. I tried all of this by hand as well as in the tests.

## Decisions I made while building

1. **The API is HTTP with JSON on a Unix socket** (Q108). Streams are server-sent events: a line of JSON each, which any language can read, and `curl` can show. It runs on Kestrel, as Listen does, with no ASP.NET features beyond the server.
2. **A new project, `Meep2.Daemon`,** holds the daemon, its client and the JSON writer for messages. The meep command uses the client.
3. **Checking who connected:** macOS and the BSDs report the user at the other end of a local socket through `LOCAL_PEERCRED`, and Linux through `SO_PEERCRED`. Windows has no equivalent for these sockets, so there it relies on the socket's folder being in your profile (Q117).
4. **Users other than the daemon's own** are refused, unless they show a token, and the socket's folder keeps them from connecting anyway (Q110).
5. **Pipelines that finish leave the list,** with a line in the log (Q111). There's no restarting yet (Q112).
6. **Tap conditions are Meeplang conditions** on messages of any type. Each message type gets the condition compiled for it the first time one passes. A type the condition doesn't fit doesn't match, as Where skips the types its expression doesn't fit (Q69, Q113).
7. **Messages are written as JSON by reflection,** not by a source generator as the design has it, so it only works on the JIT, as the daemon is (Q115). It lives in the daemon, which keeps the runtime AOT-compatible.
8. **Telemetry lives in the runtime** and uses only .NET's own `ActivitySource` and `Meter`, both called `Meep2`. The OpenTelemetry SDK, which does the exporting, is only in the meep command.
   - Most metrics are read from the counts each module keeps anyway, when a collector asks, so they cost nothing per message.
   - A pipeline's traces start with its own sources. They used to start inside the span of whatever started the pipeline. Under `meep run` that's the Host module, so every message would have joined one trace lasting as long as the pipeline. A test now checks it.
9. **Sampling is left to OpenTelemetry's own settings,** such as `OTEL_TRACES_SAMPLER`. By default it records everything (Q114).
10. **The runtime's `Tap` became `DebugTap`,** since the core modules have a Tap verb.
11. **Hand-tested** with a ticker pipeline: starting, listing, counts, injecting into a module and into a source, a tap with a condition, stopping a pipeline and stopping the daemon.
12. **New in the runtime:** `Pipeline.Modules`, `IsRunning`, `Completion`, `Tap`, `Inject` and `Injector`; `DebugTap` and `DebugTapOptions`; `MeepTelemetry`; `ModuleStats.Queued`.
13. **New in Meeplang:** `MessageCondition`, `Durations`, `BuiltPipeline.Registry`, and Host's `running` callback.
14. **New in the core modules:** `TextMessage`.

## Benchmark numbers

Measured with BenchmarkDotNet's short run. The chain benchmarks ran on step 6's commit and on step 7 back to back, since this Mac's speed changes from hour to hour.

| Chain, per message | Step 6 | Step 7 |
|---|---:|---:|
| 1 step, passing messages on | 1.34 µs, 99 B | 1.22 µs, 99 B |
| 1 step, a new message each step | 1.72 µs, 194 B | 1.62 µs, 189 B |
| 4 steps, passing messages on | 1.83 µs, 99 B | 1.86 µs, 105 B |
| 4 steps, a new message each step | 2.82 µs, 458 B | 2.59 µs, 460 B |

What watching costs: a source, one step making a new message, and a sink.

| Watched by | Time per message | Allocated |
|---|---:|---:|
| Nothing | 1.65 µs | 187 B |
| The handling-time histogram | 1.81 µs | 197 B |
| A tap on the step, with a reader keeping up | 2.85 µs | 197 B |
| Tracing, recording every span | 7.28 µs | 2,458 B |

| Writing a message as JSON | Time | Allocated |
|---|---:|---:|
| A Tick | 0.74 µs | 1.2 KB |
| An HTTP request with headers and a body | 2.46 µs | 6.5 KB |

What they show:
- **Nobody watching costs nothing.** Step 7 is as fast as step 6, within the noise of a short run, with the same allocations.
- **A tap costs about 1.2 µs a message** on the module it watches, while it's open.
- **Tracing every message costs about 5.6 µs and 2.3 KB,** for a producer span and two hops. That's normal for OpenTelemetry, and why busy pipelines sample (Q114).

## Questions

108. The control API is HTTP with JSON on a Unix socket, and its live streams are server-sent events, which only go one way. The alternatives:
     - **WebSockets** go both ways, which a patch-bay UI that both watches and edits might want later.
     - **gRPC** is typed and streams both ways, but needs generated clients, and is harder to try by hand.

     Keep HTTP and server-sent events, adding WebSockets only if the UI needs them?

     **Answer:** Yes, keep HTTP and add WebSockets now.

109. A tap shows what's inside messages, which can be more sensitive than counts. The design puts both under "observe". Should taps need a capability of their own, so a dashboard can watch counts without seeing contents?

     **Answer:** No. Taps are meant for use within pipelines. DebugTap for observing message contents, and telemetry for counts. I may have misunderstood something, though, so please call me out on it if necessary.

110. Only the user running the daemon can connect, and others need a token and a way to reach the socket, which its folder prevents. Is that enough until mutual TLS? Or should a group you choose be able to connect, with the socket somewhere they can reach?

     **Answer:** It's enough until TLS.

111. A pipeline that finishes, or that its error policy stops, leaves the daemon's list, with a line in its log. Should finished pipelines stay listed for a while, with their final counts and why they stopped?

     **Answer:** Yes. How about a default of 24 hours, and a parameter at the top to override this in Meeplang?

112. Two related questions about pipelines that stop:
     - Should the daemon restart a pipeline that its error policy stopped, or that failed, after a delay, as sources are restarted?
     - Should the daemon remember which pipelines it was running, and start them again when it starts? Q41 settled that caches and messages in flight are lost; this is only about which pipelines run.

     **Answer:** The daemon should restart a failed pipeline according to a policy given in Meeplang. The daemon should not remember and restart pipelines when the daemon restarts, it should only automatically start pipelines given in the daemon's config or command line.

113. Conditions follow gradual typing (Q69): `msg is Tick and msg.Count > 2 or msg is TextMessage` never matches a TextMessage, because `msg.Count` doesn't fit that type, even though `or` would make it true. Should `is` narrow the type for the rest of an `and`, so that condition fits every type? That would change Where and every other expression too.

     **Answer:** Can the author use parenthesis to solve the conflict? E.G.: `(msg is Tick and msg.Count > 2) or (msg is TextMessage)`.

114. Tracing records every message unless OpenTelemetry's own settings sample, such as `OTEL_TRACES_SAMPLER=parentbased_traceidratio` with `OTEL_TRACES_SAMPLER_ARG=0.1`. Should `--otlp` sample by default, say one source message in ten, with an option to record all of them?

     **Answer:** Yes.

115. Messages are written as JSON by reflection, which only works on the JIT. The design had a source generator write a JSON writer for each message type instead, which an AOT build would need. Can the generator wait until a sealed AOT build is on the table?

     **Answer:** Yes.

116. `meep start` needs a daemon already running. Would you like `meep start` to start one if none is running, or a `meep daemon install` that sets it up as a service (launchd on macOS, systemd on Linux)?

     **Answer:** `meep start` should fail if a daemon is not already running, providing help for the command to install and start the daemon, or `meep run` to run it directly. Is there a switch for `meep run` to detach from the console and run in the background? If that can't be done, the user will need to use something like `screen` and manage it themselves.

117. Windows can't report who's at the other end of a local socket, so there the daemon relies on the socket's folder being in your profile. Is that enough for Windows for now?

     **Answer:** Yes.

Plugin reloading (Q107) needs the running pipeline's modules to be swapped, which step 8 builds for edits, so it comes with step 8.

## After your answers

Applied on 9 November 2026. 361 tests pass, 24 of them new, and the suite passed 8 runs in a row. Breaking the restart limit and the skipping of unsampled spans on purpose made their tests fail, and the code is restored.

- **Q108, WebSockets:** `GET /ws` is a WebSocket that carries the whole API, both ways, on one connection.
  - A request is a JSON frame naming its method and path as the HTTP API has them, with an id of the client's choosing: `{"id": 1, "method": "GET", "path": "/pipelines/ticker/stats?every=1s"}`. A body goes in `json` or `text`.
  - Answers come back with the same id and a status: `{"id": 1, "status": 200, "body": …}`. Streams send events instead, `{"id": 1, "event": "stats", "data": …}`, until one called `end`. `{"id": 1, "cancel": true}` stops a stream.
  - Any number of requests can be going at once, so a UI can watch counts, tap two modules and inject, all on one socket. It has the capabilities of whoever opened it, checked as for HTTP.
  - HTTP and server-sent events stay as they were. Both now go through the same handlers, so they can't drift apart. `DaemonClient.OpenSocketAsync` gives .NET clients the socket.
- **Q109, no capability of its own for taps:** kept under observe. I don't think you've misunderstood anything. The in-pipeline Tap verb, the debug taps behind `meep tap`, and telemetry are three separate things, and your split works: contents through debug taps, counts through telemetry. Two things worth knowing:
  - A daemon token with observe can also open debug taps. A dashboard that should only see counts can read the OpenTelemetry metrics instead of holding a token.
  - Spans carry an error's text, which can quote what was in a message.
- **Q110, Q115, Q117:** kept as built.
- **Q111, finished pipelines stay listed:**
  - A pipeline that ends on its own stays listed, with its final counts and why it stopped, for 24 hours by default.
  - `keep-finished` under `defaults` changes it: `keep-finished = 1h`, or `none` to leave the list at once.
  - States are now starting, running, restarting, stopping, finished, failed and stopped. `meep list` shows why each ended under its line, and `meep stats` gives final counts.
  - `meep stop` on one that has ended takes it off the list. Starting a file of the same name replaces it.
  - A pipeline someone stops leaves the list at once, since whoever stopped it knows why (Q118).
- **Q112, restarting:**
  - The policy is a new part of `on-error`, beside `stop-pipeline`: `restart-pipeline after 1m up to 5 times in 1h`. Without `in 1h` it counts every restart. It only goes in the pipeline's own policy, and the default is never.
  - It restarts a pipeline that its error policy stopped, or that failed, by building it again from the same file as loaded. Edits to the file aren't picked up; that's step 8's job.
  - The Host module does the restarting, so `meep run` honours it too, as well as the daemon. `meep run` says when it restarts: `Restarting it in 1m (restart 1 of at most 5 in 1h).`
  - The daemon starts only the pipelines named on its command line, `meep daemon ticker.meep feed.meep`, and in its settings file, `~/.meep/daemon/config.json`, or the one `--config` names. The file is JSON, which may have comments, with `start`, `otlp` and `sample`. Relative paths are from the file's folder (Q119). A file that doesn't load is logged, and the daemon carries on.
- **Q113, narrowing after `is`:** parentheses alone wouldn't have helped. `and` already binds tighter than `or`, so the original condition was grouped just as in your example. It failed because `msg.Count` didn't fit a TextMessage anywhere in it.
  - What makes your example work is narrowing, which round 3 already described ("After `msg is ErrorMessage`, the loader knows the type") and step 4 never built. Now, after `msg is Tick and`, msg is a Tick for the rest of the `and`, and after `msg is not Tick or`, for the rest of the `or`.
  - `msg is Tick and msg.Count > 2 or msg is TextMessage` and your version with parentheses both match TextMessages now.
  - This changes Where and every other expression too. `Where msg is ErrorMessage and msg.Error.Contains("timeout")` now keeps Ticks out, where before they skipped the Where and went on below it. That's what round 3 said it would do. A typo after the test, such as `msg.Eror`, is reported when the file loads.
- **Q114, sampling:**
  - `--otlp` records one trace in ten, decided where each message enters the pipeline. Everything after follows, so a trace is always whole.
  - `--sample 0.25` or `--sample 25%` sets the share, and `--sample 1` records everything. Without `--sample`, `OTEL_TRACES_SAMPLER` decides if it's set. The daemon's settings file can set `sample` too.
  - Messages in a trace that wasn't sampled now cost one lookup, not an unrecorded span each.
- **Q116, the daemon and the background:**
  - `meep start` with no daemon says how to get one, and how to run the file without one:

    ```
    No meep daemon is running on /Users/you/.meep/daemon/meep.sock.
    Start one in a terminal of its own with:  meep daemon
    Or set it up to start when you log in:    meep daemon install
    Or run it here, without a daemon:         meep run ticker.meep   (add --detach to run it in the background)
    ```
  - **`meep daemon install`** sets the daemon up as a launchd agent on macOS or a systemd user service on Linux. It starts at once and whenever you log in, and is restarted if it fails, but not after `meep daemon stop`. It takes the same options and files as `meep daemon`. `meep daemon uninstall` removes it.
  - **`meep run --detach`** runs a pipeline in the background, so `screen` isn't needed. It waits until the pipeline is running, or shows why it didn't load, then prints its process id. Its output goes to `~/.meep/logs/<name>.log`. Closing the terminal doesn't stop it, and `kill <pid>` stops it as Ctrl+C would, letting messages finish. Windows says to use the daemon instead.
  - Both `meep run` and the daemon now stop gracefully on SIGTERM, which is what `kill`, launchd and systemd send.

**Tried by hand:** a daemon started with a settings file and a file on its command line, with a pipeline that failed, restarted once and stayed listed as failed with its reason and final counts. Also `meep list`, `stats` and `stop` on ended pipelines, `meep start` with no daemon, and `meep run --detach` followed by `kill`.

**Not tried:** loading the launchd agent. The permission check stopped me loading one, even under a temporary home folder. The agent file passes `plutil -lint` in a test, and the commands are tested as text. `meep daemon install` then `meep daemon uninstall` would show whether it works. The systemd unit is untested on Linux.

**Found along the way:**
- The .NET runtime keeps copies of the standard handles a process starts with. A background process given the terminal's handles held them open after it moved its output to the log, so a script waiting on `meep run --detach` waited forever. `--detach` now gives it pipes of its own.
- One of the Listen tests, `Listens_on_one_port_take_their_own_paths`, sometimes failed. It asked for its paths before both Listens had registered them, and now waits for both.

| Watched by (per message, measured back to back today) | Time | Allocated |
|---|---:|---:|
| Nothing | 0.98 µs | 193 B |
| Tracing that records one trace in ten, the new default | 1.52 µs | 784 B |
| Tracing that records everything | 4.42 µs | 2,432 B |

Sampling costs about a sixth of recording everything. Most of what's left is the source's unrecorded span, which OpenTelemetry has to make so the sampler can decide. This Mac is faster today than when step 7's first numbers were taken, so compare within this table only.
