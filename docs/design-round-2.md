# Meep2 design, round 2

Written 6 November 2026 after reading your answers in [design-questions.md](design-questions.md).

- Section 1 records what's now decided.
- Section 2 answers the questions you asked back.
- Section 3 proposes Meeplang syntax.
- Section 4 points out where some answers affect each other.
- Section 5 has follow-up questions, numbered from 47 so they don't clash with the first file.

## 1. Decided so far

**Platform and dependencies**
- Rx.NET and AsyncRx.NET are out. Meep2 gets its own small push core.
- .NET 11 and C# 15 (preview). I'll install the SDK when coding starts; the final release is due next week.
- macOS, Linux and Windows, including the daemon.
- Git is built into the SDK. SQL and SSH become bundled plugins.

**Messages**
- Type routing respects interfaces. No unions: related message types share a base class.
- JSON arrives as a generic JSON message, unless a module that knows the format deserialises it into its own type.
- A derived message inherits its parent's expiry unless Meeplang explicitly resets it. Expiry cancels a token that modules may obey; the runtime never interrupts a module mid-message.
- Trimming fam is put off until benchmarks show a need. fam with several parents gets explored before coding.
- Live connections travel down the pipeline inside messages. Modules don't keep collections of resources. Reference counting is the leading approach.

**Flow**
- When a buffer is full, the oldest message is dropped. Nothing else. Buffer size is a system default, which a pipeline can override, which a module can override. Passthrough modules work the same way.
- A module handles one message at a time, in order, and emits zero or more messages per input.
- Meeplang gets syntax for letting unsupported types bypass a module.
- Errors are messages. An ErrorMessage short-circuits downstream to modules that handle it. The runtime restarts a module that threw. `Tap` can take only errors, which marks them as handled.
- Untapped errors follow a policy set at three levels (system, pipeline, branch): log them, stop a branch after X exceptions in Y ms, and stop the pipeline when Z% of messages end in untapped errors.
- A pipeline halts once every source (fountainhead) has finished and every message has left the pipeline. From the command line, Meep then exits.

**Verbs and inflections**
- A verb is a name plus the parameters its inflections share.
- A verb routes each message to an inflection, unless a name or configuration in Meeplang settles the inflection at startup.
- Ties go to the most specific input type, then to explicit syntax. Startup fails if a choice can't be made.
- Inflections declare the types they emit as well as the types they accept. Emitting just `Message` is allowed.

**Meeplang**
- Source first, top to bottom. Indentation shows parent-child relationships. No braces or pipe operators without good reason.
- One expression language, compiled where possible.
- Durations can use `TimeSpan.Parse` formats as well as `10s`, `250ms` and `5m`.
- File order: defaults and constants first, then `#include` and `#plugin`, then templates and pipelines. Templates are copied into the pipeline before it's built.
- Secrets come from the platform's secret store, then environment variables, never files. The CLI can add secrets to the store.

**Editing and hosting**
- A module's identity is its name, falling back to its position.
- When only a module's parameters change, it keeps its state and gets a hook to handle the change itself.
- Write-back commits to `pending` (unless the tool names another branch) without touching your checkout. Merging is up to you, and the UI shows the state.
- Changes come through a control API, used by a separate UI and by modules. The API needs securing.
- The bootstrap pipeline is fixed for the life of the process.
- Serialising messages to send them to another process is the pipeline author's job.
- Running across several machines is in scope.
- Pipelines are granted specific permissions.
- A restart loses caches and in-flight messages. Transactions are supported where a source offers them.
- No performance targets yet; we measure first. OpenTelemetry: yes. A VSCode extension, at least.
- Other .NET apps, including native GUIs, can embed the SDK. Apps in other languages run a Meep process or talk to the daemon.

**Plugins**
- The SHA covers the release asset. Plugins and pipelines can both be signed, and a developer mode allows unsigned local plugins.
- A plugin unloads once its last message is gone. `Message.Inflection` can go.

## 2. Answers to your questions

### NativeAOT and plugins (Q5)

**No, a NativeAOT host can't load other NativeAOT DLLs and use their types.** NativeAOT compiles the host and every assembly it references into one native program when you publish. Nothing managed can be added at runtime: `Assembly.Load` throws. NativeAOT can build native shared libraries, but those only export plain C functions. Each one carries its own copy of the runtime and garbage collector, so a Message from one can't be passed to another as a .NET object. You'd be copying data across a C boundary.

Reflection also works differently there. It only sees what the compiler kept, so "find every class that inherits AModule" must be worked out at build time, usually by a source generator.

The performance gain is also smaller than it looks for Meep. NativeAOT starts faster, uses less memory and ships as one file. But for a long-running process, the JIT usually matches or beats it on throughput. That's because the JIT recompiles hot code using what it learns while running (Dynamic PGO, on by default since .NET 8), and NativeAOT can't. Most Meep pipelines run for a long time.

**Recommendation:** a JIT host that loads plugins into unloadable contexts, as you planned, and ReadyToRun to speed up startup. The SDK and core modules stay AOT-compatible: a generated module registry and generated parameter binding, with no reflection on hot paths. That speeds up the JIT version too. It also leaves room for a later "sealed" AOT build, with chosen plugins compiled in, for small devices or fast-starting tools. (Q47)

### Live connections (Q9)

Reference counting fits, as long as we count **messages still in flight** rather than references to objects. .NET has no destructors that run at a known time, so the runtime has to do the counting itself. It has to track messages anyway, for buffers, expiry and halting.

How it would work:

- A live resource (a socket, an HTTP context) is wrapped in a **lease** owned by the runtime. A message refers to the lease; it doesn't own the socket.
- Messages derived from a leased message inherit the lease, so it travels down the fam chain with them.
- The runtime counts a lease's messages in flight. The count goes up when a message carrying the lease is queued for a branch. It goes down when that message leaves the pipeline: dropped by a full buffer, filtered out, expired, or past the end of its branch. At zero, the runtime closes the resource.
- A module can also close a lease on purpose (e.g. `Respond` with "final"). If the remote end closes first, modules that write to it get a clear "closed" error instead of a crash.
- Writes to one connection must not interleave. The lease keeps its own write queue, so two branches writing replies queue their writes without blocking each other. That keeps the "no thread waits on another" rule.
- Only messages that carry a lease pay for counting, so ordinary messages cost nothing extra.

Your three scenarios under this design:

- **TCP client with heartbeats:** the connection stays inside the Open/TCP module and messages carry no lease. Nothing to count.
- **HTTP long poll:** `HTTPGet` emits a message with a leased connection, and `Wait` holds it for 5 s. When `Wait` drops it or passes it on and it finishes, the count hits zero and the connection closes.
- **HTTP or TCP listening:** `Listen` emits a request message with a lease. Downstream modules reply through the lease, once or many times. It closes when the last related message is done.

There are three edge cases to decide: caches, requests that never get a response, and request bodies (Q49–51).

### Typed or untyped expressions (Q26)

**Untyped** means names are looked up when each message arrives. In `Where msg.minutesAway < 30`, "minutesAway" is found by name on whatever message turns up, and the value is converted on the fly. It's flexible, but:
- a typo (`msg.minutesAwya`) is only discovered when a message reaches that module, which could be hours later in production
- every evaluation does lookups and boxes values, which allocates and is slow (goal 6)

**Typed** means the loader knows which type reaches each module, because upstream modules declare what they emit (Q21). It checks each expression when the pipeline loads, then compiles it to the same code you'd write in C#. The VSCode extension can also autocomplete message members. The drawback is that it needs type information, and generic JSON doesn't have any.

**Gradual typing** (as in TypeScript) does both. It checks and compiles fully wherever the type is known, and falls back to lookups by name where it isn't (JSON, or an upstream that only declares `Message`). The editor shows which is which.

```
// Fetch.HTTPGet declares it emits HttpResponse, so these are checked at load time:
Where msg.StatusCode == 200        // fine: StatusCode is an int
Where msg.Statuscode == 200        // load error: HttpResponse has no 'Statuscode'. Did you mean 'StatusCode'?
Where msg.StatusCode == "200"      // load error: can't compare an int with a string

// A JSON message has no fixed shape, so this is checked as each message arrives:
Where msg.minutesAway < 30         // a missing or non-numeric value produces an ErrorMessage
```

I recommend gradual typing. (Q60)

### Hidden IDs (Q30)

With hidden IDs, the runtime would write a short ID after each unnamed module, e.g. `Extract "$.departures[*]"  @id=k3f9`. Your edits keep it.
- **Gains:** a module keeps its state even when you move or rewrite it, and write-back always knows which line a module came from.
- **Costs:** clutter on every line (the editor could dim or hide it), duplicate IDs when you copy and paste (the second copy would be treated as new), and noise in Git merges.

There's an alternative that needs no IDs in the file. Match by name first, then by structure (same verb, same upstream, similar parameters), and only then by position. Structure matters for the most common edit, inserting a module mid-chain. Matching by position alone would shift every module after the insertion by one, and hand each one's state to the wrong module.

I recommend name, then structure, then position, with no IDs in the file. (Q61)

### NuGet as a plugin source (Q34)

There are two ways a plugin can bring its dependencies:

1. **A self-contained zip on GitHub Releases.** The author runs `dotnet publish` and zips the output: the plugin DLL, its dependency DLLs, and a `.deps.json` file listing them. Meep checks the zip's SHA, unpacks it into its cache, and loads it into its own load context. .NET's `AssemblyDependencyResolver` then finds dependencies through the `.deps.json`. This is Microsoft's standard plugin pattern. It's simple and reproducible. Each plugin carries its own copies of its dependencies, but that only costs disk space, and two plugins can use different versions of the same library without clashing.
2. **A NuGet package.** The plugin is a `.nupkg` that lists its dependencies. Meep uses NuGet's own client libraries (the ones the `dotnet` CLI uses) to:
   - resolve the dependency graph
   - download from nuget.org or a private feed
   - verify package signatures and hashes
   - pick the files built for .NET 11

   A lock file of resolved versions and hashes keeps installs reproducible. There are more moving parts, but plugins get nuget.org's discovery, versioning and signing for free.

Whichever way it arrives, a plugin's load context must take `Meep2.SDK` from the host rather than load its own copy. Otherwise a plugin's `Message` would be a different type from the host's `Message`.

I recommend supporting both, with Meep converting a NuGet package into the same cached folder layout as a GitHub zip. The directive would look like `#plugin github.com/owner/repo 1.2.0 sha256:…` or `#plugin nuget:Meep.Transit 1.2.0`. (Q62)

### Securing the daemon (Q40)

**Local connections**
- Listen on a Unix domain socket in your own directory, readable only by you. This works on macOS, Linux and Windows 10+. A named pipe with access controls is the Windows alternative.
- Check which user is connecting. The operating system reports this for local sockets and pipes (SO_PEERCRED on Linux, getpeereid on macOS, client identity on Windows pipes). Only you, or a group you choose, get in.

**Remote connections** (needed now that multi-machine is in scope)
- **SSH tunnel:** the daemon only listens locally, and remote users connect through `ssh -L`. There's no new security code, and it reuses SSH keys you already have. A good first step.
- **Mutual TLS:** both sides present certificates, and the CLI manages a small private certificate authority (e.g. `meep trust add`). Strong and standard, with no passwords.
- **Tokens over TLS:** API tokens kept in the keychain. Simpler, but a leaked token works for whoever holds it.

**What a client may do:** each client gets scoped capabilities, such as observe, change parameters, load pipelines, or grant permissions. This mirrors how pipelines are granted permissions.

I recommend local sockets with the user check first, SSH tunnels for remote access to begin with, and mutual TLS once multi-machine work starts. (Q63)

### OpenTelemetry (Q43)

- **What it is:** OpenTelemetry (OTel) is an open standard for telemetry from running software. It covers three things:
  - **traces:** the path one piece of work takes through a system, as nested, timed steps called spans
  - **metrics:** counters and histograms
  - **logs**

  Apps send data to a collector or backend that stores it, and viewers display it.
- **In .NET** it's built in: `ActivitySource` for spans and `Meter` for metrics. When nothing is listening, each costs one cheap check, which suits goal 6.
- **For Meep:** each hop a message makes is a span, and its parent span comes from fam. One trace then shows a Tick's whole family across branches, and across machines too, because the trace ID can travel with serialised messages. Metrics per module would cover throughput, latency, queue depth, drops, expiries and errors.
- **Non-.NET GUIs:** yes. The data goes out in a language-neutral format (OTLP), and many viewers exist: Jaeger, Grafana, Zipkin, and the standalone Aspire dashboard, which accepts data from any language. A GUI of your own can query any of those.
- **The limit:** OTel is built for looking back. Data is sent in batches every few seconds and is usually sampled when volume is high. It isn't a live view of module state.

I recommend using OTel for operations and history, with the control API also offering a live stream for a patch-bay UI: module state, queue depths, and messages passing a chosen point. (Q64)

### Test framework (Q46)

I couldn't find 2026 popularity figures. Here's where the three stand:
- **xUnit** has long been the default for open-source .NET.
- **MSTest 4** is Microsoft's own and has improved a lot.
- **TUnit** is the newcomer. It generates its tests at build time, runs them in parallel by default, and is fastest in its own (vendor-run) benchmarks. It can also run tests compiled with NativeAOT.

That last point matters if the SDK is to stay AOT-compatible, because the tests can prove it. I'd pick TUnit, with xUnit v3 as the safe choice. Benchmarks would use BenchmarkDotNet either way. (Q65)

### Upstream modules that only declare `Message` (Q21)

- **Speed: small cost.** Each verb remembers which inflection it chose for each concrete type the first time it sees it, checking base classes and interfaces. After that, every message is one dictionary lookup.
- **Complexity: this is the real cost.** If an upstream only says `Message`, startup can't prove that anything downstream will receive messages, so mistakes appear at runtime instead. Interfaces add a wrinkle: a type can match two inflections equally well (one via `IHasUrl`, one via `IAcknowledgeable`). Startup can catch that when types are declared. At runtime it would have to become an ErrorMessage.
- **Suggestion:** allow it, but warn at load time when a verb's choice depends on a module that only declares `Message`, and show that warning in the VSCode extension.

### Git and SSH (Q6)

LibGit2Sharp's official native builds don't appear to include SSH support. The SSH-enabled builds I found are forks last updated in 2016–2018. LibGit2Sharp would still cover everything local: reading files at a given commit, and committing to `pending` without touching your checkout. It also handles HTTPS remotes, with tokens from the keychain. SSH remotes (`git@github.com:…`) would need the system `git` as a fallback, or a libgit2 build with SSH that we maintain ourselves. (Q66)

### First thoughts on fam with several parents (Q8)

These are for when we explore it before coding:
- `fam.Name` searches all parents breadth-first, and the nearest match wins. Ties go to parent order, so a Zip's left parent beats its right.
- `fam.all.Name` returns every match.
- Batch and Zip messages expose their members directly (`msg.Items`, `msg.Left`, `msg.Right`), so most templates wouldn't need fam to reach them.

(Q67)

## 3. Meeplang syntax ideas

### Building blocks

```
Verb positional-argument  name=value …  as Name  @modifier …
    name = value                // more parameters, one per line
```

- **The main parameter is positional** on the verb line, like the original's `DefaultProperty`. Others are added inline, or on indented lines only when needed. This is how parameters avoid clutter: most modules need one line.
- **`as Name`** names a module so `Tap` and fam can refer to it.
- **`Verb.Inflection`** picks an inflection explicitly (`Fetch.HTTPGet`). An inflection's own name works alone when it's unique.
- **Parameter lines use `=`**, not `:`, so `TimeSpan` values like `00:00:05` aren't ambiguous.
- **`@` marks runtime behaviour** that any module can have, keeping it apart from the module's own parameters:
  - `@bypass`: unsupported types skip this module and carry on downstream
  - `@buffer=1000`: this module's buffer size
  - `@expires=5s` or `@expires=none`: set or clear expiry on what this module emits
  - `@accept-expired`: receive expired messages, for cleanup or fault analysis
  - `@on-error=…`: error policy for this branch
  - on `Tap`, `@errors`: take only error messages
- **Comments use `//`**, since `#` is taken by `#include` and `#plugin`.
- **Strings use `{…}`** for expressions, with optional formats: `"{msg.Created:yyyy-MM-dd}"`. Conditions are bare expressions: `Where msg.minutesAway < 30`.
- **`secret("name")`** reads from the secret store. Write-back keeps the call and never the value.

### Layout: two options

Both versions below describe the same pipeline: every minute, fetch departures, save the raw response, show buses due within 30 minutes, and log errors from the fetch.

**A. Staircase:** every downstream module is indented under its upstream, and siblings are branches.

```
Interval 1m
    Fetch "{Api}/schedule/{Stop}" as Schedule
        timeout = 5s
        Extract "$.departures[*]"
            Where msg.minutesAway < 30
                WriteLine "{msg.route} in {msg.minutesAway} min"
        Save "~/bus/{msg.Created:yyyy-MM-dd}.json"

Tap Schedule @errors
    Log "Bus API failed: {msg.Error}"
```

**B. Chains with branches:** lines at the same indentation form a chain, each feeding the next. Lines indented under a module are its parameters (`name = value`) or extra branches. Every chain starts with `-`.

```
- Interval 1m
  Fetch "{Api}/schedule/{Stop}" as Schedule
      timeout = 5s
      - Save "~/bus/{msg.Created:yyyy-MM-dd}.json"
  Extract "$.departures[*]"
  Where msg.minutesAway < 30
  WriteLine "{msg.route} in {msg.minutesAway} min"

- Tap Schedule @errors
  Log "Bus API failed: {msg.Error}"
```

How they compare:
- **A** has one rule and no extra symbols. But long chains drift right, and **inserting a module mid-chain re-indents every line after it**. That makes large Git diffs, harder merges, and more re-indenting in write-back.
- **B** keeps the main line straight, and inserting a module is a one-line change. It costs a `-` per chain and a second rule: indentation marks a branch, not every step.

I recommend **B**, mostly because of insertions. The VSCode extension could draw the flow lines beside it. (Q56)

### A fuller example in layout B

```
// NextBus: departures from Hove station, every minute
defaults
    buffer = 500
    on-error = log, stop-branch after 5 in 1s, stop-pipeline at 10%

const Stop = "Hove Train Station"
const Api = "https://api.busline.example/v1"

#plugin github.com/cwenham/MeepTransit 1.2.0 sha256:3f9a…
#include "shared/transit.meep"            // provides the ResolveStop template

template Departures(StopId)
    - Fetch "{Api}/schedule/{StopId}" as Schedule
          key = secret("busline-api-key")
          timeout = 5s
      Extract "$.departures[*]"

pipeline NextBus
    expires = 50s                          // this pipeline's default
    - Interval 1m
      ResolveStop Stop
      Departures msg.StopId
      Where msg.minutesAway < 30
      WriteLine "{msg.route} in {msg.minutesAway} min"

    - Tap Schedule @errors
      Log "Bus API failed: {msg.Error}"
```

Bypass, with the health example:

```
- Listen "tcp://0.0.0.0:7000"
  ParseReading                            // emits Heartbeat, BloodPressure, Temperature …
  ToFahrenheit @bypass                    // converts Temperatures; everything else passes untouched
  Save "~/readings/{msg.Created:yyyy-MM-dd}.jsonl"
```

## 4. Where answers affect each other

1. **One tracking mechanism serves several answers.** The leases in Q9 need the runtime to know when a message and all its descendants have left the pipeline. So do:
   - halting (Q18)
   - committing or rolling back a source's transaction (Q41)
   - auto-acknowledging queue messages, as the original's AutoAck did

   I'd build it once in the runtime.
2. **Errors reaching modules that accept any `Message` (Q17).** Take `Interval → Fetch → Template → Save`. If Fetch throws, the ErrorMessage goes to Template, which accepts any Message, so Template formats the error as if it were normal data before Save writes it. Worse, a module like Fetch, which treats any Message as a trigger, would start a fetch whenever an error reached it. (Q52)
3. **An error short-circuiting is a bypass.** ErrorMessages skipping the modules that don't take them is the same mechanism as `@bypass` (Q16), so the runtime gets it once.
4. **One message at a time limits throughput (Q15).** A slow module caps its branch, and its buffer then drops the oldest messages. The only way to scale it is to run several copies. (Q53)
5. **The parameter-change hook (Q31).** `Dictionary<PropertyInfo, object>` depends on reflection, which allocates and isn't AOT-friendly. A source generator could instead create a parameters record per module, e.g. `FetchParameters`. The hook then becomes `bool ParametersChanged(FetchParameters old, FetchParameters next)`. Because a module handles one message at a time, the runtime can apply the change between two messages without locking. (Q55)
6. **Messages no inflection accepts.** When a verb routes at runtime and finds no inflection for a message, should it drop the message as the type filter does, or treat it as an error? (Q54)

## 5. Follow-up questions

### Plugins and runtime

47. Is a JIT host with unloadable plugins acceptable, with the SDK and core modules kept AOT-compatible so a sealed AOT build can come later?

    **Answer:** Yes.

48. Should live connections use leases counted by messages in flight, as described in section 2?

    **Answer:** Yes.

49. Should a cache that holds a message keep its connection open? I'd say no: caches don't hold leases, so a cached message's connection may be closed by the time it's used.

    **Answer:** No.

50. If a request's last message finishes without any response written, what should happen: a default reply (e.g. HTTP 500, configurable), or just close the connection?

    **Answer:** Default reply.

51. Should request and response bodies be copied into memory by default (up to a size limit), with an opt-in streaming mode where only one branch may read the stream?

    **Answer:** Yes.

52. Which modules should receive ErrorMessages?
    - (a) only modules that accept `ErrorMessage`, plus modules marked as sinks (Log, Save, Send, WriteLine)
    - (b) any module that accepts `Message`, as you wrote, with `@no-errors` to opt out
    - (c) only `Tap … @errors`; everything else goes to the error policy

    **Answer:** b. Filtering verbs are an example that would be desirable in error handling, as well as others I can't predict.

53. To scale a slow module, should there be a Distribute verb that spreads messages across N copies of the branch beneath it? Each copy still handles one message at a time, and order would be kept only per key (e.g. per customer).

    **Answer:** Yes.

54. When a verb finds no inflection for a message at runtime: drop it and count it, or emit an ErrorMessage?

    **Answer:** Emit an ErrorMessage.

55. Should parameter changes use generated parameter records (`ParametersChanged(FetchParameters old, FetchParameters next)`) instead of a `PropertyInfo` dictionary?

    **Answer:** Yes.

### Meeplang

56. Layout A (staircase) or B (chains with branches)?

    **Answer:** Before answering, my concern is support for multiple fountainheads per branch. This was the reason for the "upside-down tree" in the original Meep, where messages from multiple sources could trickle down into a trunk with stages common to all of them. Can you adapt this model to the proposed syntax?

57. `@` for runtime behaviour that any module can have (`@bypass`, `@buffer`, `@expires`, `@accept-expired`, `@on-error`, `@errors` on Tap)?

    **Answer:** Yes.

58. Comments with `//`? File extension `.meep` or `.mp`?

    **Answer:** Comments with '//' and .meep as the extension.

59. Do the other building blocks work for you: the `pipeline`, `template` and `defaults` keywords, `const`, `as Name`, `Verb.Inflection`, a positional main parameter, and `=` for parameters?

    **Answer:** Yes.

60. Gradual typing for expressions?

    **Answer:** Yes.

### Editing, plugins and hosting

61. Should module identity be name, then structure, then position, with no IDs in the file?

    **Answer:** Yes.

62. Should plugins come from both GitHub Release zips and NuGet, converted into one cached layout?

    **Answer:** Yes.

63. For the daemon: local sockets with a user check first, SSH tunnels for remote access to begin with, and mutual TLS later? Capabilities scoped per client?

    **Answer:** Yes.

64. OpenTelemetry for operations, plus a live stream on the control API for the UI?

    **Answer:** Yes. For debugging, I'd also like to allow a subscription to the messaging coming from any point in the pipeline. Like a "Tap" on demand, consumed by a UI over a local socket only.

65. TUnit or xUnit v3?

    **Answer:** TUnit.

66. For SSH Git remotes: fall back to the system `git`, support HTTPS only, or build libgit2 with SSH ourselves?

    **Answer:** Support HTTPS only.

67. For fam with several parents: are the first thoughts in section 2 a good starting point for the exploration?

    **Answer:** Yes.

## Sources

- [TUnit scale benchmarks](https://tunit.dev/docs/benchmarks/ScaleTests) (published by TUnit)
- [LibGit2Sharp-SSH native binaries on NuGet](https://www.nuget.org/packages/LibGit2Sharp-SSH.NativeBinaries-libssh2-1.8.1.0)
- [Create a .NET application with plugins (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
