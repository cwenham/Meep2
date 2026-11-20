# Flow evidence and the boundary

Written 20 November 2026. The seventh of Synesthesia's asks (prompt 7 in `~/Projects/Synesthesia/docs/Design/meep2-prompts.md`, for Synesthesia's step 8, the boundary and flows between hosts): origins on what Meep sends to Meep, a Listen's arrivals counted by origin, a Fetch's or Send's destinations counted by endpoint, the addresses Fetches and Sends declare, and traces that join up across hosts. This covers what was built, what a client has to do, what it cost, what's possible for TCP and for sampled traces, the decisions I made, and questions numbered from 212, now answered (20 November), with what each answer changed. Nothing is committed; the work is on the branch `flows`, made from `main` at 0c83a49.

## What's built

### How one Meep host knows another

Fetch and Send don't know whether the other end runs Meep, and an origin names a daemon, a pipeline and a module, which shouldn't go to APIs outside Meep. So they ask, and are answered, with one header, `Meep-Host`:

```
GET /in/ HTTP/1.1                        HTTP/1.1 200 OK
Host: studio.local:8080                  Meep-Host: 01d21f38-8095-49ee-af20-9a71904eee47
Meep-Host: ?                             …
traceparent: 00-4bf9…-00f0…-01

GET /in/ HTTP/1.1                        HTTP/1.1 200 OK
Host: studio.local:8080                  Meep-Host: 01d21f38-8095-49ee-af20-9a71904eee47
baggage: meep.origin=7c3e…/feeder/m2     …
traceparent: 00-4bf9…-00f0…-01
```

- **A request to a host and port not known to be Meep asks**, with `Meep-Host: ?`, and carries no origin.
- **A Listen that's asked, or that receives an origin, answers** with `Meep-Host: <its daemon's ID>`. It says nothing to clients that don't ask, so browsers and other outside clients learn nothing they didn't before.
- **The asking end remembers the answer for that host and port**, for every Fetch and Send in the process, and from then on sends `baggage: meep.origin=<daemon ID>/<pipeline>/<module key>` instead of asking. The daemon ID is the lasting one from 0c83a49, and the pipeline and key are the graph's, each escaped as in a URL. A reply to a request that carried an origin and doesn't say `Meep-Host` makes it forget, so an address that stops being Meep stops getting origins.
- **This process's own Listens are known without asking:** a request to a loopback address, the machine's own name or one of its addresses, at a port one of its own Listens is bound to, carries the origin from the first request. So flows between pipelines on one host are whole from the start.
- **Hosts the daemon's settings name are known without asking** (Q213): `meep-hosts = "studio.local, 10.0.0.5:8080"` under `defaults` in `daemon.meep`, each a host, for any port on it, or a host and port. They get origins from the first request, and stay known whatever they answer, since the setting says so.
- **What's learned is remembered across restarts** (Q213), in `daemon/meep-hosts` in Meep's folder, a line for each host and port and its ID, written a second after any change and when the daemon stops. A host that answers a request carrying an origin without `Meep-Host` is forgotten there too.
- **The first request to a host neither named nor remembered only asks.** It, and any sent before its answer arrives, counts at the Listen as from outside, by the client's address. After that, every request carries its origin.
- **`meep run` is a Meep host too,** with an ID made for each run (it's also the run's OpenTelemetry `service.instance.id`), so what it sends to a daemon has an origin, under an ID no daemon has (Q214).

### Traces that join up across hosts

- **Fetch and Send add `traceparent`** while tracing (with `--otlp`, or anything else listening to Meep's spans), naming the span of the module making the request. A message whose trace isn't recorded gets one marked as not recorded, with new IDs, so the other host doesn't record its part either: the host where a message starts decides for its whole trace, as OpenTelemetry's parent-based sampler expects. Nothing is added when nothing is tracing. It goes to every host, Meep or not, as .NET's own `HttpClient` sends it today, so a service outside Meep that traces too can join in (Q212).
- **.NET's own `traceparent` is turned off** for Fetch's and Send's client. It named a span .NET made for the request, which nothing exports, so the other host's spans hung from a parent no collector had.
- **Listen continues the trace:** the span for a Listen's emitting a request (`Listen.HTTP`) has the request's `traceparent` as its parent, and is of kind Server when it does. The SDK has `ModuleContext.ContinueTrace(message, traceParent)` for any source, and `ModuleContext.TraceParent` for any module making requests.
- **WebSocket and TCP carry no traces.** A WebSocket's handshake is one request for a connection that carries many messages, and TCP has no headers; neither has anywhere to put a trace for each message.

### Redirects

Fetch and Send follow redirects themselves (Q217), rather than leaving it to `HttpClient`, which sent the same headers on to wherever a redirect led, origins included:

- **As `HttpClient` did:** up to 50 redirects; to http or https only, and never from https to http; after 300, 301 or 302 a POST becomes a GET, and after 303 anything but a HEAD does, without its body; after 307 or 308 the method and body are kept, and the body sent again, unless it was streamed, when the redirect is the answer.
- **A redirect to another host or port** is asked whether it's Meep, with `Meep-Host: ?`, and never given the origin, whether or not it's known to be Meep. A redirect within the same host and port is treated as the first request was. Each answer is taken for the host that gave it, so a redirect can teach this process about both.
- **Counted where it led,** as before: the destination is the host and port the last request reached. A body sent again for a 307 or 308 counts in the module's bytes each time, and in the destination's once.

### Arrivals by origin

Each Listen counts what arrives by where it comes from, in a subscription as a new kind, `arrivals`, at its own rate, as `bytes` has:

```json
{"id": "s", "method": "POST", "path": "/subscribe", "json": {
  "pipelines": {"site": {"arrivals": {"*": "1s"}, "destinations": {"*": "1s"}}}}}
```

```json
"pipelines": {"site": {
  "arrivals": {"m1": {
    "meep://7c3e…/feeder/m2": [120, 120, 0, 240, 0, 0],
    "192.168.1.30":           [4, 4, 1024, 900, 1, 2],
    "192.168.1.31":           [2, 2, 0, 4, 0, 0]}},
  "clients": {"192.168.1.30": "laptop.example.net"}}}
```

- **The counts** are, in the order `hello` gives under `counts.arrivals`: `received` (requests, or WebSocket or TCP messages), `sent` (replies, or messages written back on the connection), `bytesReceived`, `bytesSent`, `failed` and `rejected`. Bytes are counted as the module's `bytes` are, and add up to them.
- **From another Meep host:** `meep://` and the origin, so the key is the address Synesthesia already uses, `meep://<host>/<pipeline>/<module>`, with the daemon's ID as the host.
- **From outside:** by the client's address, which never changes (Q220). The pipeline's `clients` gives a name for each address once reverse DNS has one, looked up by the daemon in the background, within two seconds, kept ten minutes, and sent when found or changed; loopback is `localhost`.
- **Failed** (Q221): replies with a status of 500 or more, the default reply when nothing answers included; for Listen.WebSocket, replies dropped for a client too slow to keep up.
- **Rejected** (Q221): replies with a status from 400 to 499; and moved here from `failed`, as the client's mistakes, bodies refused as too large (413), WebSocket connections refused for their page's origin (403), and WebSocket and TCP messages too long.
- **A limit:** a Listen keeps up to 256 origins in Meep and 64 other clients. When a new one needs the room, the one quiet for longest, if it has been quiet for ten minutes, is folded into `meep://*` or `*`: its counts are added there, and it goes (Q215). With none that quiet, the new one is counted there instead. Folding happens only when a new place needs the room. A tally kept for a folded place, such as a WebSocket connection's, counts there too, so the counts still add up to the whole.
- **Folded places are named** in the update that folds them, in the pipeline's `folded`, by module, beside the place's entry going null: `"folded": {"m1": ["192.168.1.31"]}`. A client moves the place's counts into `*`'s, so `*`'s rate doesn't jump. A place folded and back again before an update is sent whole, with `folded` naming it too.
- **WebSocket:** a connection's origin comes from its handshake (Q216). To a host known to be Meep, each Send module has a connection of its own, with its origin as `baggage` in the handshake, so every message on it counts for that Send. To anywhere else, one connection to the address is shared by every Send in the process (Q156), and its handshake asks. When the answer says Meep, each Send moves to a connection of its own with its next message, and the shared one closes once none uses it; what went on it counts by the client's address, as a first HTTP request does.
- **TCP:** counted by the client's address only (see "TCP" below).

### Destinations by endpoint

Each Fetch and Send counts what it sends by where it actually connected, as `destinations`, with the same six counts, in the same order (`received` is responses, or messages from the server):

```json
"pipelines": {"feeder": {
  "destinations": {"m2": {"studio.local:8080": [120, 120, 240, 0, 0, 0]},
                   "m3": {"api.example.com:443": [60, 60, 51200, 0, 1, 2]}},
  "peers": {"studio.local:8080": "01d21f38-8095-49ee-af20-9a71904eee47"}}}
```

- **The key** is the host and port, with the host as the address names it, in lower case, and 80 or 443 for a web address that gives none. A name with several addresses behind it is one entry; a host reached by name and by address is two. After a redirect, the place it was redirected to.
- **`peers`** says which destinations are Meep hosts, by ID, so an observed flow names both ends by ID: the Listen's arrivals name the Fetch by daemon, pipeline and key; the Fetch's destination names the Listen's daemon. It's sent when it changes, null when a host stops being known as Meep, and only for pipelines asking for `destinations`.
- **Failed:** for Fetch.HTTP and Send.HTTP, a network failure, a timeout, a body over the module's `max-size`, or a status of 500 or more; a message Send.WebSocket or Send.TCP couldn't send in time.
- **Rejected:** a status from 400 to 499 (Q221).
- **A limit:** 64 endpoints for each module, folded into `*` when quiet, as for arrivals, and named in `folded` too.
- **Bytes stay counted for the module whose connection they cross (Q207).** Destinations and arrivals divide a module's bytes by place; they don't move them.

### Declared destinations

`GET /` has `sending`, beside `listening`, and the subscription's `host` part has `sends`, sent when it changes, as `listens` is:

```json
"sending": [
  {"pipeline": "feeder", "module": "m2", "label": "Fetch #2", "address": "http://studio.local:8080/in/", "endpoint": "studio.local:8080"},
  {"pipeline": "bus", "module": "m3", "label": "Fetch #3", "address": "\"https://api.example.com/stops/{msg.StopId}\"", "computed": true, "endpoint": "api.example.com:443"},
  {"pipeline": "lab", "module": "m4", "label": "Send #4", "address": "tcp://printer.local:9100", "endpoint": "printer.local:9100"}]
```

- **An address fixed when the pipeline starts** is given as it is, constants worked out, with its `endpoint` as destinations are keyed, so a declared flow can be matched with a Listen's `listening` address, and with what's observed later, without evaluating Meeplang.
- **An address worked out for each message** is given as its expression, as written, with `computed: true`. If the text before its first `{` holds the scheme, host and port, as in `"https://api.example.com/stops/{msg.StopId}"`, its `endpoint` is given too, so declared flows still match where only the path changes. Where the host or port is worked out (`"http://{msg.Host}/"`, `msg.Url`), there's no endpoint, and only observed flows can say where it goes.
- **Only network addresses:** a Fetch of a file, or a Send to a Listen by name, isn't listed.
- The SDK has `ISendsTo`, which Fetch.HTTP, Send.HTTP, Send.WebSocket and Send.TCP implement, so a plugin's own network verbs can declare theirs too.

### For plugins

The SDK has what the built-in verbs use, so a plugin's verbs can take part:

- `ModuleContext.Arrival(from)` and `ModuleContext.Destination(endpoint)` (and `Outlet.Destination`) give a `Tally`, with `Count(received, sent, bytesReceived, bytesSent, failed, rejected)`, `Status(code)` to count an HTTP status as failed or rejected, and, for destinations, `Meep(hostId)`.
- `ModuleContext.HostId`, `Origin` and `TraceParent`, and `ContinueTrace` for sources.
- `Peers`: the header and baggage names, `Origin`, `Baggage`, `OriginIn` (which checks an origin's form and length), `EndpointOf`, `ClientOf` and `EndpointOfTemplate`.
- `Pipeline.GetArrivals`, `GetDestinations`, `ReadPeers` and `GetSending` in the runtime, and `PipelineOptions.HostId`.
- `MeepHosts` in the modules: `Configure` for hosts known to be Meep, `Keep` for the file learned ones are kept in, and `Named`, how a setting's host is read. The daemon takes them from `DaemonOptions.MeepHosts` and `MeepHostsFile`.

## What a client has to do

1. Ask for `arrivals` and `destinations` for the pipelines whose flows and boundary are drawn, at a rate like `bytes`'s, and for `host` to get `sends`.
2. Read the order of the counts from `hello`'s `counts.arrivals` and `counts.destinations`. Apply them as module counts are applied: null for unchanged, a place gone is null, a module gone is null.
3. When `folded` names a module's places, add their last counts to that module's `*` (or `meep://*`, for a `meep://` place) before taking `*`'s difference, so its rate doesn't jump. Places go only by folding.
4. Show a client's address by its name from `clients`, when there is one, and group endpoints by domain yourself.
5. **Observed flows:** an arrival keyed `meep://<id>/<pipeline>/<key>` is a flow from that module on that daemon to this Listen. A destination whose endpoint is in `peers` is a flow from this module to that daemon. Draw solid.
6. **Declared flows:** match each `sends` entry's `endpoint` with the Listens on known hosts, by the host's names and addresses and the Listen's port, and its path, if the address is fixed. Draw dashed. Once both are drawn, an observed flow confirms a declared one.
7. **The boundary:** arrivals not keyed `meep://`, and destinations not in `peers`, are endpoints outside Meep, each with its rate, failures and rejections. `*` is everything past a module's limit, and what was folded there.
8. A `meep://` origin whose daemon isn't one you know is another Meep, perhaps a `meep run`.

## What it cost

All on this machine (an 8-core, 16-thread Xeon W-2140B), Release, with other work keeping the load average between 8 and 19, so single runs move by tens of percent.

**A busy Fetch to Listen pair** (`dotnet run -c Release -- flows` in `benchmarks/Meep2.Benchmarks`; the same file runs on `main`): one daemon, a Listen answering every request with `ok`, and four Fetches asking it over loopback, each sent a message every millisecond and asking as fast as it's answered. The CPU and allocation are the whole process's, for each request answered: both Fetch and Listen, HTTP both ways, and the daemon.

Two rounds each, run alternately; requests a second, then CPU and allocation for each request:

| Phase | `main` | This branch |
|---|---|---|
| No tracing | 3,501 / 671 µs / 89,354 B; 3,495 / 677 µs / 89,365 B | 3,494 / 745 µs / 90,820 B; 3,499 / 719 µs / 90,836 B |
| Tracing, one trace in ten recorded | 3,489 / 553 µs / 90,290 B; 3,440 / 661 µs / 90,288 B | 3,507 / 543 µs / 92,240 B; 3,492 / 567 µs / 92,244 B |
| No tracing, again | 3,502 / 529 µs / 89,366 B; 3,518 / 532 µs / 89,366 B | 3,528 / 490 µs / 90,833 B; 3,512 / 536 µs / 90,833 B |
| A subscription to bytes at 100 ms | 3,500 / 577 µs / 89,394 B; 3,519 / 518 µs / 89,404 B | 3,515 / 522 µs / 90,864 B; 3,504 / 563 µs / 90,863 B |
| … and arrivals and destinations too | — | 3,522 / 490 µs / 90,896 B; 3,477 / 551 µs / 90,898 B |

After that I trimmed the request path: an origin's baggage made once per module, the last origin a Listen saw kept with its counts, `Meep-Host` read without copying the header, and a request's endpoint worked out once unless it was redirected. Then, one run: 3,488 / 751 µs / 90,587 B without tracing; 3,507 / 546 µs / 92,014 B with; 3,525 / 505 µs / 90,593 B again.

What this says:

- **Throughput is the same.** The Interval feeding the Fetches (four a millisecond) is the limit, at about 3,500 requests a second, on both.
- **CPU is the same, within noise.** The same phase moves by up to 250 µs a request between runs, mostly the first phase as the machine's other work settles, so a difference of a microsecond or two can't show. The pieces timed alone, below, add up to about a microsecond a request between both ends.
- **Allocation is up 1.2 KB a request** (1.5 KB before the trims): the two headers both ways, Kestrel and `HttpClient` keeping them, the closures that count bytes by place, and endpoint strings. Most of the 89 KB on `main` is Fetch's buffer, which is now rented (below).
- **Tracing one trace in ten** costs 0.9 KB a request on `main` and 1.4 KB on the branch. The extra half is the nine requests in ten not recorded: `main` sent nothing with them, while the branch sends a `traceparent` saying "not recorded", with new IDs, and the Listen keeps it until its message goes. For the one in ten recorded, the branch's header replaces .NET's, which also made a span of its own.
- **Reading arrivals and destinations ten times a second** for this pair adds nothing measurable.
- **On the wire:** `traceparent` is 70 bytes a request while tracing; `baggage` 69 (with a 36-character daemon ID, a pipeline of six letters and key `m2`); the question, to hosts not known as Meep, 14; the answer 49 on each reply to Meep.

**After the answers** (20 November), with Fetch's buffer rented, the same benchmark, two rounds each, run alternately, with the load average between 4 and 7:

| Phase | `main` | This branch |
|---|---|---|
| No tracing | 3,530 / 409 µs / 89,350 B; 3,524 / 432 µs / 89,366 B | 3,513 / 452 µs / 8,536 B; 3,520 / 481 µs / 8,540 B |
| Tracing, one trace in ten recorded | 3,533 / 367 µs / 90,296 B; 3,525 / 381 µs / 90,331 B | 3,525 / 359 µs / 9,932 B; 3,517 / 361 µs / 9,965 B |
| No tracing, again | 3,533 / 341 µs / 89,365 B; 3,529 / 359 µs / 89,364 B | 3,526 / 337 µs / 8,538 B; 3,531 / 361 µs / 8,537 B |
| A subscription to bytes at 100 ms | 3,505 / 453 µs / 89,386 B; 3,522 / 384 µs / 89,393 B | 3,531 / 367 µs / 8,567 B; 3,521 / 386 µs / 8,569 B |
| … and arrivals and destinations too | — | 3,526 / 356 µs / 8,604 B; 3,515 / 364 µs / 8,604 B |

- **Allocation is down from 89.4 KB to 8.5 KB a request**, a tenth, with the buffer rented and the body read straight into an array of its length. The 1.2 KB the origins, counts and headers add is in that 8.5 KB.
- **Throughput and CPU are the same as `main`**, within the noise, as before. The first phase is the noisiest on both, as the machine's other work settles.
- **Tracing one trace in ten** adds 1.4 KB a request, as before the answers.

**The pieces alone** (`dotnet run -c Release -- flows-micro`, a million times each, the best of five):

| What | Time | Allocated |
|---|---|---|
| Reading an origin from a request's baggage (skipped when it's the same as the last request's) | 171–213 ns | 120 B |
| Finding a place's counts among 41, and counting a request | 41–44 ns | 0 |
| Working out a request's endpoint from its address | 122 ns | 88 B |
| A request message with no headers of ours | 163–176 ns | 184 B |
| The same, with `traceparent` and `baggage` added | 314–322 ns | 392 B |

So the counts by place cost about 40 ns a request at each end, and the headers about 150 ns and 200 bytes to add, before the network stack writes and parses them.

## TCP

TCP has no headers, so a Send can't ask anything without sending bytes that a server outside Meep would take as data: a printer on port 9100 would print the question. So:

- **What's built:** a Listen.TCP counts arrivals by the client's address; a Send.TCP counts destinations by host and port, and knows the far end is Meep only if it's this process's own Listen. Synesthesia matches TCP flows by address against known hosts' TCP Listens (Q218).
- **What's possible later:** a first line between Meep ends, sent only to hosts the `meep-hosts` setting names (Q213), since there's nothing to ask with. Listen.TCP would look for it at the start of each connection, before framing, by a prefix no protocol would start with, such as a NUL and `meep.origin=`, and pass everything else on as it does now. Not built (Q218).

## Sampled traces for tracers

Synesthesia's tracers (design section 4) want real messages drawn along their path. The spans Meep already makes are those hops: one for each module that handles a traced message, with `meep.pipeline`, `meep.module.key`, `meep.message.id` and its parent, and now joined across hosts by `traceparent`. So a daemon could give a client a sample of them:

- **How:** while a subscription asks for `traces` (say `{"traces": {"share": 0.01, "most": 100}}`), the daemon listens to Meep's spans itself, recording that share of the traces that start on it, and every trace a recorded `traceparent` brings in, and sends each finished span: trace and span IDs, parent, pipeline, key, message ID and type, start and duration, outcome. A client subscribed to several hosts joins them by trace ID, and draws each hop in order, across hosts.
- **Cost while nobody asks:** nothing more than now.
- **Cost while someone asks:** every message a source emits makes a span for its trace, recorded or not, so the other modules know whether to record. In the benchmark above, recording one trace in ten cost no CPU that showed through the noise, and 0.9 to 1.4 KB a request in allocation. My estimate, not measured, is about a microsecond and a few hundred bytes to record a hop, and about 120 bytes in CBOR to send one, so `most` keeps the wire bounded.
- **Side effect:** when the daemon also exports to a collector, the collector gets the traces the subscription recorded as well as its own share.
- **Size:** a listener shared by the daemon's subscriptions, a bounded ring of finished spans, a new kind with its own form, and tests across two daemons: a few hundred lines, so not small next to the rest of this. Not built: it will be a prompt of its own, after Synesthesia's step 8, starting from this design (Q219).

## Tests

Fourteen in [FlowEvidenceTests.cs](../../../tests/Meep2.Tests/FlowEvidenceTests.cs), with daemons of the tests' own:

- **Between two daemons**, three Fetches to a Listen: the Listen's arrivals have one origin, `meep://<feeder's ID>/flow-feeder/m2`, with 3 received, 3 replies and their 6 bytes; the Fetch's destination has `127.0.0.1:<port>` with 3 sent, 3 answered and 6 bytes, and `peers` names the Listen's daemon.
- **Origins go only to hosts that say they're Meep, and are remembered:** a server that never answers is asked twice and never told; one that answers `Meep-Host` is asked once, then told `meep.origin=<id>/origins/m3`; `peers` names only it, and the daemon's `meep-hosts` file has it.
- **Learned hosts are kept across restarts and forgotten when they stop saying so:** a file's host is known when it's read, a malformed line and an overlong ID are skipped, and an answer without `Meep-Host` to a request carrying an origin forgets it, in memory and in the file.
- **Hosts the settings name are told the origin from the first request**, and still told though they never answer `Meep-Host`; `meep-hosts` is read from `daemon.meep`, a bad one refused with why, and hosts and IPv6 addresses with and without ports are read as matched.
- **Redirects:** a Meep host that redirects to another is told the origin once known; where it leads is asked, never told, and is the destination counted. A 307 sends a POST's body again; a 303 becomes a GET without one.
- **A Listen on another daemon continues the trace:** each `Listen.HTTP` span's parent is the `Fetch.HTTP` span that made its request, on the other daemon. With the port as though on another machine, the first request counts by its address, `127.0.0.1`, named `localhost` in `clients`; the second by its origin.
- **Each Send has a WebSocket of its own to a Meep host:** two Sends to a Listen as though on another machine share one connection until it answers, counted by address, then each has its own, counted under its own origin; every message arrives once, and `peers` names the Listen's daemon.
- **Rejected and failed:** replies of 404 count as rejected and 503 as failed, at the Listen and at the Fetch, with the bytes of each.
- **Declared destinations:** a fixed web address and TCP address with their endpoints, a template with `computed` and its endpoint, a file left out; in `GET /` and in the host's `sends`.
- **A module's places are bounded:** past 64 clients and 256 origins, with none quiet, the rest under `*` and `meep://*`, the totals whole.
- **A quiet place is folded when a new one needs the room:** not before ten minutes; then the quietest, not one counted since; a tally kept for it counts into `*`, and the totals stay whole.
- **A subscription says which places were folded:** after 256 origins, an origin new after eleven minutes folds one, which goes null and is named in `folded`, with `meep://*` holding its count.
- **A response body is read whole**, with its length given or not, and one over the limit refused.
- **How origins, endpoints and clients are written,** and which baggage isn't taken: two parts, an empty part, a space, or longer than 256 characters.

## Decisions I made while building

- **One header, `Meep-Host`, both ways**, asking with `?` and answering with an ID, rather than a reply header to every client: Listens stay silent to clients that don't ask, as Kestrel's `Server` header is already off.
- **Origins go only to hosts that have answered**, or this process's own Listens, so nothing about a pipeline reaches an outside API. The question itself says only "Meep", which Fetch's `User-Agent: Meep2/1.0` already says.
- **Asking goes on for every request to a host not known as Meep.** Remembering "not Meep" for a while would save about 14 bytes a request, at the cost of missing a host that becomes Meep; not worth it yet.
- **An answer from a host a redirect led to** is taken for that host only.
- **Arrivals and destinations have the same six counts**, both ways, so the boundary has bandwidth and rates in and out for every endpoint, and `received` counts what a WebSocket server pushes to a Send.
- **Places with nothing counted yet aren't shown**, such as a WebSocket client refused before anything was counted.
- **`folded` is per pipeline, by module key**, beside `clients` and `peers`, rather than in a module's map of places, where it could be taken for a place. A module has only arrivals or only destinations, so its key says which.
- **A place that goes is always folded:** places leave a module's counts only that way, so the subscription names every place that goes in `folded`, without a log of folds to keep.
- **The redirect's new host is asked even if it's known to be Meep**, since the server, not the pipeline, chose to send it there.
- **A host the settings name stays known**, whatever it answers, and isn't written to the saved file, which holds only what was learned.
- **The saved file is shared by every daemon in one process**, as the knowledge is: tests run several daemons in one process, and each writes all that's known.
- **`peers` rides with `destinations`**, per pipeline, rather than in `host`, so a client asking for a pipeline's destinations doesn't have to ask for the host too.
- **Counting is per place, by a handle found once per request**, with plain atomic adds, and the table made the first time a module counts anything, so modules that don't talk to the network carry nothing.
- **Modules' internals are visible to the tests** now, for `MeepHosts.Unbound`, which lets a test treat a Listen in the same process as one on another machine. `SubscriptionTests` needed a `using` alias for `Subscription`, which now has a namesake there. `DaemonLab` takes a change to the daemon's options.
- **`PeerTable.Clock`** can be replaced by a test, to make places quiet without waiting ten minutes; the two tests that do are `[NotInParallel("peer-clock")]`.

Noticed in passing:

- **Fetch.HTTP allocated an 80 KB buffer for every response it read**, whatever its size, in `ReadBodyAsync`: most of the 90 KB allocated for each request in the benchmark. Now changed, as asked with the answers: the buffer is rented from `ArrayPool<byte>.Shared`, sized from `Content-Length` where that's smaller, and returned once the body is read; the body is a new array of its own, which no message shares with the pool, made without a copy when `Content-Length` was right.
- **Redirects carried our headers along**, so an origin meant for a Meep host went wherever it redirected. Now changed (Q217).
- Not changed: a Listen bound to port 0 over HTTP, and an HTTP and a WebSocket Listen sharing a port, are remembered by the port as given, and the first to stop forgets it for both; requests then ask, and learn again.

## Questions

Answered 20 November 2026, with one more change asked for at the same time: Fetch's buffer for reading a response is now rented (see "What it cost").

212. ★ `traceparent` goes to every host while tracing, as .NET sends it today, so a traced service outside Meep can join a trace. Only origins are kept to Meep hosts. Should `traceparent` also go only to Meep hosts?

     **Answer:** Keep `traceparent` going to every host while tracing, as built. It carries no pipeline details, and that's what .NET and OpenTelemetry already do. Only origins stay with Meep hosts.

     *Kept as built.*

213. ★ The first request to another Meep host only asks, so it counts there as from outside, once for each host and port after each start. Add a daemon setting listing hosts known to be Meep (`meep-hosts = "studio.local, 10.0.0.5:8080"`), and remember those learned in `~/.meep/daemon/` across restarts? The setting would also be what lets TCP say where it's from (Q218).

     **Answer:** Yes to both: a daemon setting listing hosts known to be Meep, and remembering learned Meep hosts in `~/.meep/daemon/` across restarts. Keep forgetting a host when a reply to a request that carried an origin doesn't say `Meep-Host`, and forget it in the saved list too.

     *Done: see "How one Meep host knows another". `meep-hosts` under `defaults` in `daemon.meep` (in `DaemonConfig`, passed as `DaemonOptions.MeepHosts` to `MeepHosts.Configure`), and `daemon/meep-hosts` in Meep's folder, read when the daemon starts and written a second after a change and when it stops (`MeepHosts.Keep`). A host learned and then answering a request that carried an origin without `Meep-Host` is forgotten in both. Three tests: the daemon writing what it learned, a file read and a host forgotten in it, and named hosts told from the first request, with the setting read and refused.*

214. `meep run` is a Meep host with an ID made for each run, so what it sends to a daemon has an origin under an ID no daemon has, and it answers `Meep-Host` with that ID. The alternative is that a run sends no origins and counts as from outside. Keep it as built?

     **Answer:** Keep `meep run` as a Meep host with an ID for each run, as built. Synesthesia will show an origin from an unknown daemon as "another Meep".

     *Kept as built.*

215. A Listen keeps 256 origins in Meep and 64 other clients, and a Fetch or Send 64 endpoints; past that, new ones count together under `meep://*` or `*`, and nothing is evicted, so every entry's counts stay cumulative. A public Listen will fill its 64 with the first clients it sees. Are the sizes right, and should a client quiet for a long time be folded into `*` to make room (its entry going, and `*` jumping by its counts)?

     **Answer:** Keep the sizes: 256 origins, 64 other clients and 64 endpoints. Fold a client or endpoint that has been quiet for about ten minutes into `*` (or `meep://*`) to make room for a new one. In the update where you fold one, say which entries were folded, for example `"folded": ["192.168.1.31"]` beside the counts, so a client can move their counts into `*` instead of drawing a jump in `*`'s rate. Fold only when a new place needs the room, not on a timer.

     *Done: see "Arrivals by origin". `PeerTable` folds the place quiet for longest, if it has been quiet for ten minutes, when a new one needs the room, and only then; a tally kept for a folded place counts where it was folded. The subscription names folded places in the pipeline's `folded`, by module, in the update where they go null. Two tests: folding in the table, and `folded` in a subscription.*

216. ★ A WebSocket's origin is per connection, and Send shares a connection to one address between every Send in the process (Q156), so a shared connection's messages count under the Send that opened it. The alternatives: a short message naming the origin whenever it changes, on connections both ends agreed are Meep's; or a connection of its own for each Send module when the far end is Meep. Keep it per connection?

     **Answer:** Don't count a shared connection under the Send that opened it. When the far end is known to be Meep, give each Send module its own WebSocket connection to that address, so each connection's origin is exact and needs no extra frames. Connections to hosts not known as Meep stay shared as Q156 has them.

     *Done: see "Arrivals by origin". To a host known to be Meep, each Send.WebSocket has a connection of its own, keyed by its origin, with `baggage` in the handshake. Elsewhere connections are shared, and ask; a Send moves to its own connection with its next message once the answer says Meep, and back if a host stops saying so. The first message frame naming the origin is gone, from Send and from Listen. The WebSocket test now has two Sends, each counted under its own origin once known.*

217. Fetch and Send follow redirects through `HttpClient`, which sends our headers on to wherever a redirect leads, origins included. Should they follow redirects themselves, dropping `baggage` when the host changes?

     **Answer:** Yes: Fetch and Send follow redirects themselves. When a redirect changes the host or port, drop `baggage` and ask the new host with `Meep-Host: ?`, as for any host not known as Meep. Keep the counting of the redirect's destination as built.

     *Done: see "Redirects". `HttpClient`'s own following is off, and `HttpFetch.SendAsync` follows them for Fetch.HTTP and Send.HTTP. A test covers a Meep host redirecting to another, a 307 and a 303.*

218. TCP says nothing of where it's from. Build a first line between Meep ends (a NUL, `meep.origin=…`, a line end), sent only to hosts configured as Meep (Q213), and looked for by Listen.TCP at the start of each connection? Or leave TCP counted by address?

     **Answer:** Not now. Leave TCP counted by address. Synesthesia will match TCP flows by address against known hosts' TCP Listens. Note in the report that the setting from 213 would make a first line possible later.

     *Not built. "TCP" says the setting from 213 would make a first line possible later.*

219. ★ Sampled traces for tracers could come through the daemon as a subscription kind, `traces`, from Meep's own spans, joined across hosts by trace ID (see "Sampled traces for tracers"). It's a few hundred lines, so I haven't built it. Build it next, as its own prompt, or wait until Synesthesia draws tracers?

     **Answer:** Don't build it here. Sampled traces will be a prompt of their own, after Synesthesia's step 8. Keep the design in the report as it is, so that prompt can start from it.

     *Not built. The design stays in "Sampled traces for tracers".*

220. Clients outside Meep are shown by their reverse-DNS name once the daemon finds one, their counts moving whole from the address's entry to the name's. Alternatively the key could stay the address, with names in a map of their own, as `peers` is. Which suits the boundary?

     **Answer:** Keep the client's address as the key. Give names in a map of their own beside it, as `peers` is (say `clients: {"192.168.1.31": "laptop.example.net"}`), sent when a name is found or changes. Keys never change that way, so rates don't jump, and Synesthesia groups endpoints by domain itself. Keep the lookup's two-second limit and ten-minute cache.

     *Done: see "Arrivals by origin". Arrivals from outside are keyed by address, and the pipeline's `clients` names them, sent when a name is found or changes (`ClientNames.NameOf`). The trace test checks `127.0.0.1` named `localhost`.*

221. A reply with a status of 400 or more counts as failed, at both ends: the Listen's arrival and the Fetch's destination. Should 4xx (the client's mistake) count as failed, or only 5xx and no answer?

     **Answer:** Count 4xx separately. Add a sixth count, `rejected`, for replies of 400–499, in `hello`'s order after `failed`, at both ends. Keep `failed` for 5xx, no answer, timeouts, bodies too large, and the other failures listed. Say in the report which of those moved into `rejected`, if any.

     *Done: `rejected` is the sixth count, in `hello`'s order after `failed`, at both ends. Moved from `failed` to `rejected`: bodies refused as too large (413), WebSocket connections refused for their page's origin (403), and WebSocket and TCP messages too long, as the client's mistakes. `Tally.Status` counts an HTTP status as one or the other. A test checks 404 and 503 at both ends.*

## The test run

After the answers (20 November): the build has no warnings, and the full suite of 615 (the 601 before this work and these 14) passed three times running.

Before them: the full suite was 608, the 601 before and 7 of these, and passed six times running at the end, three before and three after the last change below. In the runs while I worked, `A_trace_that_isnt_sampled_gets_no_spans_after_its_source` in `HostingTests.cs` failed three runs in a row, from two things my tests brought:

- **My trace test's listener records every trace**, and .NET takes the most any listener asks for, so while it ran beside that test, every one of that test's traces was recorded. It now has `[NotInParallel("tracing")]`, as the other tracing tests do.
- **That test counts every span that asks with an unrecorded parent**, in any pipeline in the process, to check nothing in its own does. A Listen in another test, continuing a `traceparent` that says "not recorded", now asks too, as it should, to pass the decision on. It now leaves out spans whose parent came from another host.
