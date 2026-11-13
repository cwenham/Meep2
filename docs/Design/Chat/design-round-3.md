# Meep2 design, round 3

Written 7 November 2026 after reading your answers to Q47–67 in [design-round-2.md](design-round-2.md).

- Section 1 records what round 2 settled.
- Section 2 adapts the syntax so several sources can feed one trunk, your concern in Q56.
- Section 3 covers what happens when errors meet expressions, now that any module accepting `Message` receives errors.
- Section 4 sketches the on-demand debug tap from your answer to Q64.
- Section 5 explores fam with several parents.
- Section 6 proposes the order to build things in.
- Section 7 has questions, numbered from 68.

## 1. Decided in round 2

**Runtime**
- A JIT host loads plugins into unloadable contexts. The SDK and core modules stay AOT-compatible, using a generated module registry and generated parameter records, so a sealed AOT build remains possible later.
- Live connections are wrapped in leases, counted by messages in flight. Caches don't hold leases.
- If all of a request's messages finish without a response, the runtime sends a default reply (HTTP 500 unless configured otherwise).
- Request and response bodies are read into memory by default, up to a limit. Streaming is opt-in, and then only one branch may read the stream.
- Any module that accepts `Message` receives ErrorMessages, unless `@no-errors` opts it out.
- A Distribute verb spreads messages across N copies of a section and keeps order per key.
- When a verb finds no inflection for a message at runtime, it emits an ErrorMessage.
- Parameter changes arrive as generated records: `ParametersChanged(FetchParameters old, FetchParameters next)`.

**Meeplang**
- `@` marks runtime behaviour that any module can have.
- Comments use `//`, and files end in `.meep`.
- The keywords `pipeline`, `template`, `defaults` and `const`, plus `as Name`, `Verb.Inflection`, a positional main parameter, and `=` for parameters.
- Gradual typing for expressions.
- The choice between layout A and B waits on section 2.

**Editing, plugins and hosting**
- A module's identity is its name, then its structure, then its position. No IDs in the file.
- Plugins come from GitHub Release zips or NuGet, unpacked into one cached layout.
- The daemon uses local sockets with a user check, SSH tunnels for remote access first and mutual TLS later, and capabilities per client.
- OpenTelemetry for operations, a live stream on the control API for the UI, and on-demand debug taps (section 4).
- Tests use TUnit, and benchmarks use BenchmarkDotNet.
- Git remotes are HTTPS only. LibGit2Sharp is still maintained (0.32.0, July 2026). An SSH URL such as `git@github.com:…` fails at load, with a message giving the HTTPS form.
- The first thoughts on fam are the starting point for section 5.

## 2. Several sources, one trunk

### The problem

Pipelines are graphs, but indentation can only draw a tree. A tree shows branching in one direction for free; the other direction needs references by name.

- **The original Meep** put the last module at the root, and each module's children were its upstreams. Several sources merging into one trunk came for free. Splitting a stream into branches needed `Tap`.
- **Layouts A and B in round 2** put the source at the root. Splitting comes for free, but merging needed `Tap`. That's clumsy for the common case of several sources sharing the same steps.

### The proposal: `+` chains feed the chain after them

The fix is one new rule, and it works in either layout:

> A chain marked `+` feeds the next chain marked `-` at the same indentation. Several `+` chains in a row all feed it.

So `+` reads as "plus" and `-` as "then". Here are the two timers from the original README.

Original:

```xml
<WriteLine From="{fam.Timer.Payload}: {msg.Value}">
    <Reverse>
        <Get Url="{msg.Value}">
            <Timer Interval="3:00:00" Payload="http://doineedajacket.com">
            <Timer Interval="1.0:00:00" Payload="http://isabevigodadead.com">
        </Get>
    </Reverse>
</WriteLine>
```

Meeplang, layout B:

```
+ Interval 3h  payload="http://doineedajacket.com"
+ Interval 1d  payload="http://isabevigodadead.com"
- Fetch msg.Payload
  Reverse
  WriteLine "{fam.Interval.Payload}: {msg.Value}"
```

Sources can have their own steps before they join, and the file still reads top to bottom:

```
+ Listen "tcp://0.0.0.0:7000"
  ParseReading
+ Watch "~/inbox/*.csv"
  ReadLines
  ParseReading
+ Subscribe "mqtt://broker.local/readings/#"
  ParseReading
- Validate
  ToFahrenheit @bypass
  Save "~/readings/{msg.Created:yyyy-MM-dd}.jsonl"
```

The README's claims example merges and splits in the same pipeline.

Original:

```xml
<WriteLine Name="Claims" From="Claim filed at {msg.URL}">
    <Listen Base="http://local.domain.com/Spam" />
    <Listen Base="http://local.domain.com/Copyright" />
</WriteLine>

<BayesTrain Class="{msg.URL}">
    <Tap From="Claims"/>
</BayesTrain>

<Respond>
    <Text>Thank you for your claim, it will be processed in the next 4-5
    business days.</Text>
    <Tap From="Claims"/>
</Respond>
```

Meeplang:

```
+ Listen "http://local.domain.com/Spam"
+ Listen "http://local.domain.com/Copyright"
- WriteLine "Claim filed at {msg.URL}"
      - BayesTrain class="{msg.URL}"
  Respond "Thank you for your claim, it will be processed in the next 4-5 business days."
```

No `Tap` is needed here: merging is `+` and splitting is an indented `-` branch. `Tap` remains for joining things that sit far apart, such as across pipelines or files, and for diamonds (below).

### What follows from the rule

- **Joining partway down a chain:** split the chain where the join happens, so everything before the join becomes a `+` chain. In the readings example, `Listen → ParseReading` is the first half of what would otherwise be one chain.
- **Distribute gets a natural scope.** Distribute copies the rest of its chain. Inside a `+` chain, that means up to where the chain feeds the next `-` chain, so the copies' output merges back into one stream:

  ```
  + Listen "http://0.0.0.0:8080/quote"
    Distribute 4 by msg.CustomerId
    PriceLookup                         // slow, so four copies run side by side
  - Respond "{msg.Price}"               // one Respond, in order per customer
  ```

- **Inputs are numbered.** The `+` chains feeding a module are its inputs 1, 2, 3, in file order. Most modules ignore the numbers, but Zip and Join use them to tell left from right (section 5).
- **Diamonds use Tap.** A stream that splits and later rejoins needs one name:

  ```
  + Listen "tcp://0.0.0.0:7000" as Readings
    Where msg is Temperature
    ToFahrenheit
  + Tap Readings
    Where msg is not Temperature
  - Save "~/readings/{msg.Created:yyyy-MM-dd}.jsonl"
  ```

  For this particular job, `ToFahrenheit @bypass` is simpler.
- **The rule works inside branches too.** Under a module, `+` chains feed the `-` branch that follows them, alongside that module's own output.
- **A `+` chain must feed something.** One with no `-` chain after it is a load error.
- **Merging isn't the same as several parents.** Each merged message still has one parent; it just arrives at a shared module. Several parents only come from modules that combine messages (section 5).

### Layout A with the same rule

Layout A can use the rule as well, with `+` chains feeding the next unmarked line at the same indentation:

```
+ Interval 3h  payload="http://doineedajacket.com"
+ Interval 1d  payload="http://isabevigodadead.com"
Fetch msg.Payload
    Reverse
        WriteLine "{fam.Interval.Payload}: {msg.Value}"
```

So merging doesn't decide between A and B. Insertion still does: inserting a module partway down a chain is one line in B, but in A it re-indents the rest of the chain. I still recommend B. (Q68)

## 3. Errors meet expressions

You chose (b) in Q52: any module that accepts `Message` receives ErrorMessages. Gradual typing (Q60) then needs a rule for when an expression meets an error. Take this pipeline:

```
- Interval 1m
  Fetch "{Api}/schedule/{Stop}" as Schedule
      - Save "~/bus/{msg.Created:yyyy-MM-dd}.json"
  Extract "$.departures[*]"
  Where msg.minutesAway < 30
  WriteLine "{msg.route} in {msg.minutesAway} min"
```

When Fetch fails, its ErrorMessage travels down both paths.
- Extract only takes JSON, so the error skips it.
- `Where` accepts any Message, but `msg.minutesAway` doesn't exist on an ErrorMessage. The loader knows this, because ErrorMessage is a fixed type.

**Proposal: a module only accepts the types its expressions make sense for.** The loader checks each expression against every type that can reach the module. If an expression uses a member that a type lacks, messages of that type skip the module, as they would with `@bypass`. (Q69)

In the example:
- The error skips `Where msg.minutesAway < 30` and `WriteLine "{msg.route} in …"`. It reaches the end of the branch and goes to the error policy, which logs it by default.
- `Save "~/bus/{msg.Created:yyyy-MM-dd}.json"` does take it, because every message has `Created`. So the error is written into the day's file. That's (b) working as intended. Add `@no-errors` if you don't want errors in that file.
- `WriteLine "{msg}"` would print everything, errors included.
- `Where msg is ErrorMessage and msg.Error.Contains("timeout")` keeps only timeout errors. After `msg is ErrorMessage`, the loader knows the type, so `msg.Error` is checked at load time.

Generic JSON has no fixed type, so the loader can't rule anything out for it. A JSON message without `minutesAway` still produces an ErrorMessage at runtime, as agreed in Q60.

**Modules whose expressions don't mention `msg`.** The rule can't help here. `Fetch "{Api}/static.json"` treats any message as a trigger, so it would start a fetch for every error that reaches it. The author of an inflection like that knows an error isn't a trigger, so they could declare it. Meeplang would then need to override in either direction, which suggests one modifier with three values instead of two:

- `@errors=only`: only errors. This is what `@errors` on Tap means now.
- `@errors=none`: no errors. This is what `@no-errors` means now.
- `@errors=also`: errors as well, even where the author said no. The loader refuses it where an expression can't apply to errors.

`@errors` and `@no-errors` can stay as short forms. (Q70)

**When does an error count as handled?** The error policy acts on errors nobody handled, so (b) needs a rule for that too. Proposal: an error is handled once a module takes it in and doesn't pass that same ErrorMessage on. It may emit nothing (a filter dropped it) or emit a new message made from it (a template turned it into text). An error that only skips modules, or passes through them unchanged, is still unhandled when it reaches the end of its branch. (Q71)

## 4. On-demand debug taps

What you described fits the control API, not the `.meep` file.

**How it works**
- A UI asks the daemon to tap the output of any module, optionally with a filter expression and a sample rate.
- The runtime adds a hidden tap at that point. It's never written back to the file, and it goes away when the UI disconnects.

**Who can use it**
- It's available over the local socket only, to clients with the "observe" capability.
- An SSH tunnel arrives as a local connection from your own user, so remote debugging works without opening anything else.
- Over mutual TLS (later), debug taps stay off unless you allow them.

**It must never slow the pipeline or change what it does**
- Each tap has its own small buffer that drops the oldest message, so a slow UI loses messages instead of holding anything up.
- A tap holds no leases, like a cache, and doesn't count towards halting, acknowledgements or transactions.
- When no tap is open, the cost is one cheap check per message, and nothing is copied.

**Showing message contents**
- The UI needs to see what's in a message, so the runtime needs to write any message as JSON. This is for display only; moving messages between processes stays the pipeline author's job (Q38).
- The SDK's source generator would write a JSON writer for each message type, including those in plugins, which keeps it AOT-compatible.
- Leases show as, for example, `lease 12 (open)`. Each message comes with its fam, so the UI can show where it came from.
- Values from `secret()` are masked wherever the runtime can track them, such as module parameters. A message that copies a secret into its own content can't be masked.

A natural partner to tapping is injecting: sending a test message into any point of a running pipeline. It would need a stronger capability than "observe". (Q72)

## 5. fam with several parents

### Where several parents come from

- **Zip and Join** pair messages from two inputs, such as an order and its payment matched by order ID.
- **Batch** turns many messages from one stream into one message.
- **Aggregate and Window** combine recent messages, such as the average of the last 10 readings.

Merging with `+` doesn't create several parents (section 2).

### Storing parents

- Most messages have one parent, so that case must cost nothing extra. A small struct, like ASP.NET Core's `StringValues`, holds either one parent directly or an array of several.
- `msg.Parent` is the first parent, and `msg.Parents` lists them all.
- A Batch's items are its parents, so `msg.Items` is a view of `msg.Parents`, not a second copy.

### Looking up `fam.Name`

- A name matches a module's `as` name. A module with no `as` name matches its verb or inflection name instead (`fam.Fetch`, `fam.HTTPGet`), as in the original. (Q74)
- The search goes breadth-first, one generation at a time, so the nearest match wins. At equal distance, the lower input number wins: a Zip's input 1 beats its input 2.
- `fam.all.Name` returns every match, nearest first.
- The loader resolves names against the graph. It knows that `fam.Schedule` means the Fetch named Schedule, and what type that emits, so `fam.Schedule.StatusCode` is checked at load time just like `msg`. If Schedule isn't upstream on every path into the module, the loader warns that `fam.Schedule` can be empty.
- Matching compares numbers, not strings. Each module gets a number at load, and each message records which module emitted it. The search walks parent links; benchmarks will show whether it needs caching.

### What a message inherits from several parents

- **Expiry:** the earliest of its parents' expiries, since a combined message is only as fresh as its oldest part. Meeplang can still reset it. (Q73)
- **Leases:** all of its parents' leases. A Zip of two requests keeps both connections open until it's finished.
- **Completion:** a message is finished when it and all its descendants are. A child with several parents keeps each of them open. So a Batch of 100 queue messages acknowledges all 100 only once the batch's own descendants finish, which is what at-least-once delivery needs.
- **Tracing:** an OpenTelemetry span has one parent but can have many links, and links exist for exactly this case. The first parent becomes the span's parent, and the others become links.

### Memory

A message keeps its ancestors alive. A Batch of 10,000 messages keeps all of them, and their own ancestry, until it's finished. As agreed in Q7, we measure before trimming anything.

## 6. Order of work

Round 2 settled most of the design, so here's a proposed order. Each step ends with something that runs, with tests and benchmarks.

1. **Set up.**
   - Remove the sketch code.
   - Move to .NET 11 and C# 15 preview. The final release is due at .NET Conf, 10–13 November; until then, RC2.
   - Create the projects: SDK, runtime, Meeplang, core modules, CLI, tests (TUnit) and benchmarks (BenchmarkDotNet). Plugins compile against the SDK but run on the host's copy, so the SDK stays small and is versioned with care.
   - Add a `.gitignore`, and make a first commit containing the design docs.
2. **Messages and the push core.**
   - Message, ErrorMessage, fam and the in-flight tracker.
   - Bounded buffers that drop the oldest message.
   - Each module running as its own task and handling one message at a time.
   - Tested from C# first, with no language yet.
3. **Meeplang, first slice.**
   - The lexer and parser: chains, `+` and `-`, parameters, `as`, `@` modifiers, comments, `const` and `defaults`.
   - The expression compiler with gradual typing.
   - The lexer and parser are written as modules from the start (goal 8), even though the bootstrap that wires them is fixed.
   - Ends with `meep run file.meep` working for Interval, Fetch, Extract, Where, WriteLine, Save and Tap.
4. **Verbs and errors:** inflection routing, ErrorMessage flow, the error policy, expiry and `@bypass`.
5. **Leases:** Listen and Respond over HTTP and TCP, default replies, and buffered or streamed bodies.
6. **Plugins:** unloadable load contexts, GitHub zips and NuGet, SHA checks, signing, and developer mode.
7. **Hosting:** the daemon, control API, capabilities, live stream, debug taps and OpenTelemetry.
8. **Write-back:** module identity matching, edits that keep comments, and commits to `pending`.
9. **The rest:**
   - loading pipelines from URL, Git, IPC and stdin
   - templates and `#include`
   - Distribute, Zip and Batch
   - the VSCode extension

(Q75)

## 7. Questions

68. With `+` chains for merging, layout A or B? And do `+` and `-` read well to you, or would you prefer other marks?

    **Answer:** Layout A. Your marks and symbols are good.

69. Should a module accept only the message types its expressions make sense for, with other types skipping it as they would with `@bypass`?

    **Answer:** Yes.

70. Should inflection authors be able to declare that errors aren't welcome (Fetch, for example), with `@errors=only|also|none` overriding that in Meeplang, and `@errors` and `@no-errors` kept as short forms?

    **Answer:** No. Let's keep that in Meeplang with '@' directives, it's the pipeline author's decision to make.

71. Should an error count as handled once a module takes it in and doesn't pass that same ErrorMessage on?

    **Answer:** Yes.

72. Should debug clients also be able to inject a test message at any point, given a stronger capability than "observe"?

    **Answer:** Yes.

73. When a message has several parents, should its expiry be the earliest of theirs?

    **Answer:** Yes.

74. Should `fam.Name` match a module's `as` name if it has one, and otherwise its verb or inflection name?

    **Answer:** Yes.

75. Is the order of work in section 6 right, and can coding start once these questions are answered?

    **Answer:** Yes.

## Sources

- [Meep README](https://github.com/cwenham/Meep) (the timers and claims examples)
- [LibGit2Sharp 0.32.0 listing](https://nubrowse.com/packages/libgit2sharp) (third-party NuGet browser)
- [Span links for async and batch spans (SigNoz)](https://signoz.io/docs/traces-management/guides/span-links/)
