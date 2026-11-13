# Meep2 design questions

Written 5 November 2026, before any Meep2 code is written or changed. It covers how I read the brief, what I'd count as skeletal and core modules, and questions for you to answer. Each question has an **Answer** line below it to fill in.

## Findings to know first

- **AsyncRx.NET looks stalled.** `System.Reactive.Async` is still at 6.0.0-alpha.18 from May 2023, and the Rx.NET README still calls it an experimental preview. Meanwhile, main Rx.NET reached 7.0.0 in July 2026. I don't think we can count on it maturing in the coming months (Q2).
- **.NET 11 is at RC2** (released 13 October, licensed for production use). The final release is expected at .NET Conf, 10–13 November. C# 15 adds `union` types and `closed` hierarchies, and both would suit message types. This Mac only has the .NET 10.0.100 SDK.
- **NextBus.mp suggests a different model from the brief.** It reads like named values worked out from what they depend on, closer to a spreadsheet or Make than to Unix pipes (Q23).
- **The sketch doesn't build yet**, which is fine for thinking out loud. `MeePlang.HandleMessage` is a `public override` of a `protected virtual` method, and `Program.cs` calls `WebApplication` in a project that isn't a web project. Two smaller notes for later:
  - `MessageId` allocates on every message. A struct around `Guid.CreateVersion7()` would be cheaper and sorts by time.
  - `Message.Inflection` holds on to the module that made the message, which would stop plugins unloading (Q35).

## How I read it

Mostly as you wrote it. These are the interpretations worth confirming:

- The **runtime**, not the modules, enforces type filtering, expiry and passthrough. A module never sees a message it can't handle, so inflections stay simple.
- "Mutating" a message means a C# `with` copy of the record, possibly as a different type. The new message's parent is the old one.
- Users write **verbs**, and **inflections** are what actually runs. Choosing the inflection is the runtime's job.
- Self-hosting means pipeline definitions (both text and AST) are messages flowing through a hard-coded bootstrap pipeline. Reloading and writing back are then ordinary pipeline work.

Here are the original Meep's architectural mistakes as I see them in the code:

- Pipelines were written inside-out: downstream modules contained their upstreams, so files read backwards.
- `ShippingAndHandling` blocked a thread on `task.Wait(Timeout)`, and hitting the timeout didn't cancel the work.
- One message in, at most one out. Anything else meant overriding `GetMessagingSource` with raw Rx.
- Messages could change after creation (`Christen` set `Name`).
- Every module received every message and had to check types itself (`Ack` searched ancestors with `FirstByClass`).
- Exceptions were logged and swallowed.
- Shared global state: a static message cache and `AHostProxy.Current`.
- Three separate expression systems: SmartFormat templates, NCalc conditions, and data selectors with prefixes.

## Skeletal and core modules

**Skeletal** modules are what the bootstrap pipeline needs so Meep can load, run and rewrite itself:

- **Read and Watch:** file, stdin, IPC socket, HTTP, Git
- **Verify:** SHA and signature checks for pipelines and plugins
- **Parse:** Meeplang text → a syntax tree that keeps comments and whitespace → an AST message
- **Resolve:** verbs → inflections, plus the list of plugins needed
- **Install:** download from GitHub Releases, verify, cache, and load in a way that allows unloading (a collectible AssemblyLoadContext)
- **Build:** AST → running module graph
- **Diff and Apply:** compare the old and new AST and patch the running graph
- **Write:** turn AST changes back into source text and commit to the `pending` branch
- **Host and Spawn:** run a pipeline in this process or a child process, plus the daemon's control listener
- **Log and Fault:** logging and the error path

**Core** modules are general-purpose, ship in the SDK, and have no heavy third-party dependencies:

- **Triggers:** Interval, Once, Emit (literal values), Random
- **Flow:** Tap, Merge, Route (by type or condition), Batch/Window, Unbatch, Throttle/Debounce, Delay, Take/Skip, Distinct, Zip/Join, Retry
- **Filter and transform:** Where, Match (regex), Template, Extract (JSON path, XPath, regex), Parse/Format (JSON, XML, CSV, lines)
- **State:** Cache with the standard query interface, Count/Aggregate
- **I/O:** Fetch/Send (HTTP), Listen/Respond, file Read/Write/Append, TCP/UDP/WebSocket, WriteLine, Exec
- **Ack/Decline**

Everything else would be a plugin: SQL, SSH, message queues, chat APIs and so on. Git is the awkward case (Q6).

**Answer** (anything to add or move between these lists):

## Questions

If you'd rather start with a few, the ★ ones shape the core design most. "(Not in the brief)" marks topics I think were left out.

### The original

1. Is my list of the original's mistakes your list, or were there others?

   **Answer:** My mistakes. If there's more, they're all my fault, too.

### Dependencies and platform

2. ★ Given AsyncRx.NET's state, are you attached to Rx's operators and behaviour? Or are you open to Meep2 having its own small push core, built on `ValueTask` and System.Threading.Channels, with adapters to Rx and `IAsyncEnumerable`? I'd lean to the second. It puts backpressure, scheduling and allocation under our control, and goals 1–3 and 6 depend on those.

   **Answer:** Not attached to AsyncRx.Net or Rx.Net in general. It seemed to be the best tool at the time.

3. Is it OK to install the .NET 11 SDK and move both projects to `net11.0` with `LangVersion` set to preview?

   **Answer:** Yes

4. Which platforms: macOS, Linux, Windows? For the daemon, that would mean launchd, systemd and a Windows service.

   **Answer:** All three.

5. Loading plugins at runtime rules out ahead-of-time compilation (NativeAOT) for the main host. Is a JIT-only Meep fine?

   **Answer:** In that case, I think I'll need to rethink how plugins would work. Would a NativeAOT host be able to link to other NativeAOT .DLLs, import their types and explore them by reflection? If so, I'd be willing to give up support for dynamic reloading/unloading of plugins for the performance gain.

    In the original Meep, there was a core distribution of plugins packaged with the host and they were loaded by adding them to the same AppDomain and using reflection to discover types that inherited AModule. I imagined the same style for Meep2, pushing SQL and SSH into bundled plugins, but this can change for the sake of performance. 

6. Git is needed for goals 4 and 7. Should it be built into the SDK (LibGit2Sharp, which brings a native dependency), done by running `git`, or a plugin the bootstrap installs?

   **Answer:** It should be built-in to the SDK. Meep should "speak Git" natively.

### Message model

7. ★ The fam chain keeps every ancestor alive. Tick → Fetch (a 5 MB body) → Split into 10,000 records keeps the body in memory while any record lives, including records held in caches. Should ancestry be trimmable, with a depth limit, a module that cuts it, or summaries of old ancestors?

   **Answer:** Not yet. This is something I would consider premature optimisation and reevaluate after benchmarking.

8. Fan-in (Batch, Zip, Join) gives a message several parents, so fam becomes a graph rather than a list. How should `fam.Name` resolve when several ancestors match?

   **Answer:** I haven't thought about this in detail, so let's explore it again before coding.

9. ★ Some messages hold live resources. A stream can only be read once, which clashes with branching, and garbage collection won't close sockets. The options I see:
   - copy payloads into memory
   - count references to each resource and close it when the last branch is done
   - keep resources inside modules and give messages handles to them

   Request/response (Listen → … → Respond) is the main case. Which do you prefer?

   **Answer:** I can say that an important concept in Meep is that serving queries is done by passing a live socket down the pipeline where it is replied to one or more times by modules.

    The background to this was an API I had to use where you opened a TCP/IP socket and then just sat and listened. A heartbeat message was sent by the remote host every 45 seconds until there was information to pass. If the connection was dropped or no heartbeat received, my app had to retry connections forever. In Meep that would translate to a TCPClient module (an "Open" verb and TCP inflection) configured for the remote address, a 46 second timeout, an EOM pattern, and an infinite retry. Received messages would flow down the pipeline as they arrived. There would be no response, so the live socket wouldn't go with them.

    A similar system from a different vendor used HTTP long poll, where you made a GET request and sat on it for 5 seconds for a response, then closed and recycled whether you received a response or not. In that case the live socket would travel with each message: An Interval module generates Tick messages every 5 seconds, an HTTPGet module begins the request and then emits a message with the open socket, and a Wait module configured for a 5 second timeout holds the live message and only emits if it receives anything in that time. In this case, the live connection is only passed between two modules.

    While Meep is not intended to be a full HTTP server, it needs to have the ability to receive HTTP requests and process them via the pipeline. Modules need to be as stateless as possible, so an HTTPListen module only concerns itself with receiving connections, and then pipeline decides how to route, assemble a response and send it around the context of the message and its chain of ancestors. Meep could also act like the server-side of the first scenario where a remote client establishes a TCP/IP connection to a TCPListen module and a message with the live connection is passed downstream for other modules to handle.

    I want to avoid having modules maintain collections of resources; I want to look at the pipeline itself as the store of those resources before looking at other means.

    Reference counting sounds like the best solution so far, but I'm open to feedback!

10. Records only allow single inheritance. Should type routing also respect interfaces (e.g. `IAcknowledgeable`, `IHasUrl`)? Or C# 15 unions, so a module can accept Heartbeat or BloodPressure without them sharing a base type?

    **Answer:** Interfaces should be respected. I don't think unions would be desirable, and message types like Heartbeat and BloodPressure would need to inherit from the same parent.

11. Most API traffic is JSON. Should every API get its own C# record type, or a generic JSON message read on demand, or message types declared in Meeplang?

    **Answer:** Some modules, especially if they're from a plugin and know what they're getting, would deserialise the JSON to a C# type. Otherwise a generic JSON message would be passed, and there might be another module downstream that translates or deserialises it some way, even if it's just an "Extract" verb using JSONPath to extract some values.

12. Does a derived message inherit its parent's expiry time? Should an expired message cancel work already in progress, or only be stopped at the next hop? (I'd measure deadlines on a monotonic clock. `DateTime.UtcNow` is too coarse for sub-millisecond deadlines and jumps when the system clock changes.)

    **Answer:** Yes, unless the mutating module resets it, but that should require explicitly saying so in Meeplang. Expired messages would set a CancellationToken that the module can obey in its own code, but processing shouldn't be preempted by the runtime.

### Flow (mostly not in the brief)

13. ★ Backpressure: when a branch can't keep up, should it buffer and then drop the oldest messages, drop the newest, slow the producer, or spill to disk? Which is the default? Slowing the producer lets one slow branch hold back its siblings, which conflicts with each reference moving at its own pace.

    **Answer:** Buffer and then drop the oldest, no other behaviour. It'd be better to redesign the pipeline than try to make the system cope somehow. There should be a systemwide default buffer size we can pick after some benchmarking, overridable by a pipeline default set in Meeplang, followed by per-module tuning in Meeplang.

14. Same question for passthrough modules: what happens when their background work falls behind?

    **Answer:** Same answer. Buffer, then discard oldest.

15. Must a module see messages in the order its branch produced them? Should a module be allowed to handle several at once? That breaks the order unless the results are put back in sequence.

    **Answer:** Only in order, and only one at a time. The only way to handle multiple messages at once is if they were put into a batch message first. A module can emit zero or more messages for each incoming message (such as a "Split"), and that module would decide the order.

16. The brief says unsupported messages don't get past a module. But for a converter in a mixed stream (Celsius → Fahrenheit among other HealthRecordings), you'd want the other messages to carry on untouched. Should there be an opt-in bypass mode for that?

    **Answer:** Hmm, I think syntax for bypasses should be added to Meeplang in that case. 

17. Errors: should they travel as messages to an error branch? Do you want retry policies and circuit breakers? Should Meeplang let you attach a branch that only receives errors from a named module?

    **Answer:** Yes, errors are messages. A pipeline should be a train that never stops, so a well written module could trap and emit an ErrorMessage, or the module itself explodes with an exception and the runtime must handle it, emit an ErrorMessage that short-circuits down the pipeline until it reaches a module that handles them (which could include modules that handle any Message type, since the pipeline author may intend to filter and have them write the error to a file, a table, send a message somewhere, etc.), and restart the module, even if that means disposing and re-instantiating it. 

    Where errors are expected and the pipeline author knows what they want to do with them, the Tap verb would have syntax that means tap only error messages, upon which the error is considered fully handled and the runtime need not be concerned anymore.

    Meeplang should have cascading syntax to set behaviour for untapped errors. The system default is to log them, stop a branch after X many runtime-handled exceptions in Y milliseconds, and stop the pipeline when Z% messages result in untapped errors. Those can then be overridden individually in Meeplang for each pipeline, then for individual branches in the pipeline (maybe there's a circumstance where the poor author has to deal with a very smelly API).

18. When a source finishes (a file read once), should that flow downstream and end the pipeline? Should shutdown let messages already in flight finish first?

    **Answer:** Yes. Meep is really meant for building systems that run continuously, but it could be used for applications with a halting state. Messages should be allowed to finish their flight down the pipeline. Once all sources (aka Fountainheads) have signalled their halting state and all messages have passed through their terminal module, then the pipeline should be unloaded. If Meep is running from the command line, it would exit.

### Verbs and inflections

19. ★ Is the inflection chosen once, when the pipeline is built (e.g. from a literal `https://` URL)? Or per message, when the URL comes from a template? Choosing per message makes each verb instance a router over several inflections.

    **Answer:** If not made explicitly or implicitly definite via Meeplang, it would be chosen at runtime per message. Verbs are expected to be routers. Making them explicit (by naming) or implicit (by configuration) would be an optimisation, for whatever its worth.

20. When two plugins offer an inflection for the same context, how is the tie broken: priority, most specific input type, or explicit syntax like `Fetch.HTTPGet`?

    **Answer:** Most specific input type, then by explicit syntax in Meeplang. Meep should abort with an error at startup if it cannot resolve an inflection either way (when the emittable types declared by the upstream module cannot be accounted for in the downstream module and its config).

21. Should inflections declare the types they emit as well as the types they accept? Then the loader can warn about a branch that can never receive anything.

    **Answer:** Yes. That'll be important for cases like Question 20. It is allowable for a module's emitted types to include just "Message", however, but please warn me if you see this creating a lot of complexity or performance harm.

22. Is a verb only a name, or a set of parameters its inflections share (e.g. every Fetch takes a source and emits a resource)?

    **Answer:** Name plus parameters. The parameters may be enough to resolve the specific inflection at startup, but if not then the verb becomes router of messages to inflections.

### Meeplang

23. ★ NextBus.mp reads like values worked out from what they depend on. `BusStopID = ResolveBusID(BusStopLocation)`, and `atcoAreaCode:` is a block that ResolveBusID's URL refers to. Is that the direction? If so, do values update live (change `BusStopLocation` and everything that depends on it is recomputed)? And how do they relate to pipelines of modules?

    **Answer:** NextBus.mp shouldn't be taken as a requirement, it was an exercise to explore possible syntax for Meeplang and I abandonned it in frustration. I'd like ideas for shaping the final syntax. I had trouble with the conflicting desires of representing the structure of pipelines without clutter while also adding parameters to each verb. But I'd sacrifice some visual clarity if it can be restored with custom editors and VSCode extensions.

24. Are named, indented blocks like `ResolveBusID:` reusable composite modules that take parameters?

    **Answer:** They would have been pipeline templates, so they would be copied into the main pipeline before instantiating it.

25. Should files read source first, top to bottom? Indentation that matters, as in the sketch, or braces? A pipe operator?

    **Answer:** Source first, top to bottom. Indentation would signify parent-children relationships. I'm not considering braces or pipe operators unless there's good reason.

26. Should there be one built-in expression language for templates, conditions and data selection, compiled to delegates? Should it be typed, so mistakes show up at load time?

    **Answer:** One built-in expression language, compiled if possible. I'd like to see some explanation and examples that could help me decide if it should be typed.

27. Some small choices:
    - durations written as `10s` or `250ms`?
    - file extension `.meep` (as in the brief) or `.mp` (as in the sketch)?
    - comment syntax?

    **Answer:** I'd like to support multiple syntaxes for durations, from those supported by Timespan.Parse() to "10s", "250ms", or "5m".

28. How are includes and plugin namespaces declared? (The original used xmlns, `<Plugin>` elements, and XInclude with fallbacks.)

    **Answer:** #include and #plugin at the top of the file before any pipeline or template definitions and after any pipeline defaults and constants are declared.

29. Secrets (not in the brief): environment variables, the keychain, a separate file? Write-back must never write them into a file.

    **Answer:** Keychain or platform-specific secrets store first, environment variables second. I don't want to support secrets in files. The CLI should support adding secrets to the store.

### Bidirectional editing

30. To compare old and new pipelines, each module needs a stable identity. Should that be its name, with position as the fallback for unnamed modules, or a hidden ID the runtime adds to the file?

    **Answer:** Name first with fallback to position. I'd like to hear more about how a hidden ID would work, though.

31. When only a module's parameters change, should it keep its state (cache contents, counters, open connections)?

    **Answer:** It should keep its state and have the chance to deal with parameter changes, perhaps by overriding a method like `bool ConfigurationChanged(Dictionary<PropertyInfo,object> newParams)`. Returning true would mean it reconfigured itself with the new values, and false means the runtime should write the new values to each property.

32. Does "save to a `pending` branch" mean committing there without touching your working tree or checked-out branch? And if you've edited the file since the runtime loaded it, which change wins?

    **Answer:** Yes, it should commit to the 'pending' branch if the UI or tool making the runtime change didn't otherwise provide one, and not touch the checked-out branch. It then becomes the user's responsibility to merge this change as they wish, otherwise edits to the original branch before a merge would win. It'll be up to the UI to visualise this to the user.

33. What makes changes to a running pipeline: a UI you're planning (a patch-bay view?), a control API on the daemon, or modules? Is the UI part of this project?

    **Answer:** A separate UI via a control API. If a module makes changes, it'd do so via that API. I'd like to explore ways this API could be secured, too.

### Plugins

34. What does the SHA cover: the release asset or the DLL? How does a plugin get its own NuGet dependencies? Would NuGet be acceptable as a source alongside GitHub Releases?

    **Answer:** The release asset. NuGet sounds like it could be a source as well, can you tell me what options there are for resolving NuGet dependencies and using it as a source of plugins?

35. A plugin can only unload once nothing refers to its types. Messages of plugin types held in caches or fam chains keep it loaded, and so would `Message.Inflection` in the sketch. Is "stop routing to it, and unload when its last message is gone" acceptable?

    **Answer:** Yes. Also, I added the Inflection reference to Message in anticipation of needing it, but it can be dropped if it turns out to be unnecessary.

36. Should plugins be signed as well as pipelines? A developer mode for unsigned local plugins, as the original had?

    **Answer:** Yes.

### Hosting and security

37. Can a running pipeline change its own bootstrap pipeline (say, swap in a new parser), or is the bootstrap fixed for the life of the process?

    **Answer:** Bootstrap is fixed.

38. Messages sent to child processes have to be serialised. Must every message type be serialisable (with System.Text.Json source generation)?

    **Answer:** No, sending messages across process boundaries is a special case and the pipeline author must decide how to handle translation to serialisable types.

39. Running across several machines (the original had ProvisioningPlan XML): in scope or out?

    **Answer:** In scope.

40. Permissions (not in the brief): a pipeline loaded from a URL can write files, open sockets and run processes. Should pipelines be granted specific permissions? Who may connect to the daemon?

    **Answer:** Yes, pipelines would need to be granted specific permissions. Can you tell me about security options for connecting to the daemon?

41. Durability (not in the brief): on restart, are caches and in-flight messages lost? Or should Meep2 save checkpoints and guarantee delivery at least once, using Ack?

    **Answer:** All caches and in-flight messages are lost. I'd like to support transactions if a source does.

### Performance, testing and tooling

42. Do you have concrete targets: messages per second through a five-module chain, latency per hop, allocations per message? Should we benchmark against the original Meep?

    **Answer:** No. Let's find out what we can get.

43. Observability (not in the brief): OpenTelemetry tracing with each hop as a span (fam maps naturally onto a trace)? Per-module throughput, latency, queue depth and drop counts?

    **Answer:** That sounds good. I don't know anything about OpenTelemetry, can you give me a summary, and would it support visualisation of message flow and state in non-dotNet GUIs?

44. Do you want a language server and syntax highlighting for Meeplang? The comment-keeping syntax tree that write-back needs would serve that too.

    **Answer:** Yes. I'd like a syntax extension for VSCode, at least.

45. Besides the CLI and daemon, who else will use the SDK? A GUI app embedding Meep would need some modules to run on the UI thread.

    **Answer:** Other .Net apps that need to host a pipeline directly, which can include .Net native GUIs. For apps written in any language that can't embed .Net libraries, they would need to invoke a Meep process or daemon.

46. Do you prefer a test framework: xUnit v3, NUnit, or MSTest?

    **Answer:** Which is the current favourite for new .Net apps?

## Sources

- [System.Reactive.Async on NuGet](https://www.nuget.org/packages/System.Reactive.Async)
- [System.Reactive on NuGet](https://www.nuget.org/packages/System.Reactive)
- [dotnet/reactive README](https://github.com/dotnet/reactive)
- [What's new in C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)
- [What's new in .NET 11](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [C# 15 union types (.NET Blog)](https://devblogs.microsoft.com/dotnet/csharp-15-union-types/)
- [.NET 11 RC2 (Visual Studio Magazine)](https://visualstudiomagazine.com/articles/2026/10/14/net-11-rc2-adds-post-quantum-crypto-maui-csharp-ui-and-nuget-controls.aspx)
