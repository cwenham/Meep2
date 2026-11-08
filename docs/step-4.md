# Step 4: verbs, expiry and the error policy

Written 8 November 2026. This covers what step 4 built, the decisions I made while building it, benchmark numbers, and questions numbered from 90 with answer lines.

## What's built

- **Verbs with several inflections.**
  - A verb gives each message to the inflection whose input type fits it most closely.
  - When several fit equally well, their parameters choose between them (Q19, Q22).
  - `Fetch.HTTP` names an inflection, and so does `HTTP` on its own when no other verb has an inflection of that name.
- **Fetch has two inflections:** `HTTP`, and a new `File`, which reads a file for each message.
  - `Fetch "https://…"` uses HTTP, `Fetch "~/stops.json"` uses File.
  - `Fetch "{msg.url}"` chooses for each message.
  - An address neither can use is a load error, with both reasons.
- **A message no inflection takes becomes an error** (Q54), rather than being dropped quietly as in step 2.
- **Expiry** (Q12, Q78):
  - Expired messages stop at the next module.
  - A module's cancellation token fires when its message expires.
  - New settings: `@expires`, `@accept-expired`, and an `expires` default.
- **The error policy** (Q17), set in `defaults` for the pipeline and with `@on-error` for a branch:
  - `log` or `ignore`
  - `stop-branch after 5 in 1s`
  - `stop-pipeline at 10%`
  - `restart after 100ms up to 3 times`, which is Q79's restart for sources
- **`meep run` exits with 3** when the error policy stops the pipeline (Q90).
- **Examples:** `examples/watchdog.meep` checks a website and reports when it's down. All the examples run.
- **Tests:** 242 TUnit tests, 45 of them new. The suite passed 6 runs in a row after the last change, and 13 before it. Breaking expiry, and then routing by parameters, on purpose made their tests fail, and the code is restored.

## In Meeplang

```
defaults
    expires = 5m
    on-error = log, stop-branch after 5 in 1s, stop-pipeline at 10%, restart after 100ms up to 3 times

const Api = "https://api.busline.example/v1"
const Stop = "hove-station"

Interval 30s @expires=10s
    Fetch "{Api}/schedule/{Stop}" as Schedule
        Extract "$.departures[*]" @on-error="ignore, stop-branch never"
            Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl" @accept-expired
    Fetch.File "~/bus/stops.json"
```

- **`Fetch.File`** names an inflection. `File` alone works too, while no other verb has an inflection called File.
- **`@expires=10s`** sets the expiry of the messages a module makes. `@expires=none` clears it.
- **`@accept-expired`** lets expired messages reach a module, for cleaning up or looking into faults.
- **`@on-error="…"`** sets the error policy for a module and the modules indented under it. It needs quotes on a module line, because a policy has spaces in it (Q94).
- **Under `defaults`:**
  - `expires` sets how long messages from sources last.
  - `on-error` sets the pipeline's error policy. It's written without quotes.
- **Parts of a policy that a line leaves out** come from the level above. A branch inherits from the module it's indented under, and the pipeline from the system defaults.

## How a verb chooses an inflection

1. **By name:** `Fetch.HTTP` or `HTTP` leaves only that inflection.
2. **By the parameters the line uses:** an inflection whose parameters record has nothing for a parameter the line sets is left out.
3. **By parameter values:** an inflection can implement `IChosenByParameters<TParameters>` and say why it can't take some values, as HTTP does for an address that isn't http or https.
   - Values that are the same for every message are checked when the pipeline loads, and inflections that refuse them are left out. If none is left, the file doesn't load, and the error gives each inflection's reason.
   - Values worked out per message, such as `"{msg.url}"`, are checked for each message.
4. **By message type, for each message:**
   - The closest fit wins: the message's own type, then its base classes nearest first, then interfaces, then `Message`.
   - Among equally close fits, the first whose parameters take the message wins. If they all refuse it, the next closest fits are tried.
5. **If nothing takes the message**, it becomes an error from that module, made from the message (Q54).

When the pipeline loads, every message type a module can receive must have an inflection to go to (Q20). Otherwise the file doesn't load, and the error suggests `@bypass` or a filter. Inflections that tie for a type need parameters worked out per message to choose between them; otherwise the line has to name one.

## Decisions I made while building

1. **Names:**
   - An inflection's name comes from `[Verb("Fetch", Inflection = "HTTP")]`, or else its class name.
   - Messages record which inflection made them, so `fam.HTTP` and `fam.Fetch` both find a message from Fetch's HTTP inflection (Q74).
2. **A verb's inflections must agree** on whether they're sources, and on whether they're passthrough modules, because the runtime treats those differently before it knows which inflection a message will go to.
3. **Sources** have nothing to route, so their inflection is settled when the pipeline loads. A source line that leaves more than one is a load error.
4. **Refusals (Q54):**
   - A message a module doesn't take becomes `ErrorMessage` "Map #2 doesn't take Number messages", with the message as its parent.
   - This replaces step 2's quiet filtering, in C# pipelines too (Q90).
   - Errors-only modules (`@errors`) still let other messages go quietly, because that's what the author asked for.
   - The counts are `Refused` and `Filtered`.
5. **Expiry:**
   - It's checked when a message is delivered, and again when it leaves a module's buffer. Expired messages are counted as `Expired`, and a tracked message reports `MessageOutcome.Expired`.
   - **Errors never expire,** so a failure is always reported, even when the message it came from has expired (Q95).
   - A module that stops because its token fired at the deadline hasn't failed. Nothing is emitted, and the module isn't replaced.
   - `@expires` applies to new messages a module makes. A message it passes on unchanged keeps its own expiry.
   - The `expires` default only applies to messages from sources that don't set their own. Everything else inherits.
6. **Error policy levels:**
   - A module's policy decides whether the errors it emits are logged, and when its own failures stop its branch.
   - Tap chains and the targets of `+` lines start from the pipeline's policy, because they aren't indented under anything.
   - `stop-pipeline` only goes in `defaults`, because it's about the whole pipeline.
7. **stop-branch** counts the times a module throws. Errors it emits on purpose, such as Fetch's, don't count (Q17's "runtime-handled exceptions").
   - When it stops a module, messages that reach that module are dropped. Errors dropped there are still reported as unhandled.
8. **stop-pipeline** counts messages from sources:
   - A message counts as failed if any error made from it went unhandled.
   - It counts the last 100 messages, once at least 20 have been sent (Q92).
   - Errors the policy ignores don't count.
9. **The system defaults** are your example from round 2: `log, stop-branch after 5 in 1s, stop-pipeline at 10%`, plus `restart after 100ms up to 3 times` from Q79 (Q93).
10. **Notices:**
    - The runtime gives notices such as "Stopped Map #2 and the branch below it, because it failed 5 times within 1s" to a new `Notice` callback. By default, and in `meep run`, they're written to standard error.
    - A pipeline the policy stopped says why in `Pipeline.StoppedBecause`.
11. **Fetch.File:**
    - A path without a scheme is a file, with `~` meaning your home folder.
    - A missing or unreadable file becomes an error, and so does one over `max-size`.
    - When Fetch has to choose for each message, the address is worked out twice: once to choose, and once to fetch.
12. **New in the SDK and runtime:**
    - `VerbAttribute.Inflection`, `IChosenByParameters<TParameters>`, `ModuleInfo.Inflection`, and `MessageOutcome.Expired`
    - `ErrorPolicy`, `FailureLimit`, `SourceRestart`, and `InflectionOrder`
    - `PipelineBuilder.AddVerb` with `VerbInflection`
    - On `NodeOptions`: `Expires`, `AcceptExpired` and `ErrorPolicy`
    - On `PipelineOptions`: `Expires`, `ErrorPolicy` and `Notice`
    - `ModuleStats.Expired` and `ModuleStats.Refused`, and `Pipeline.StoppedBecause`

## Benchmark numbers

Measured with BenchmarkDotNet's short run. Today this Mac measured step 3's code about 40% slower than when step 2 ran, so the table compares step 3's commit and step 4, both measured today. Times and allocations are per message, end to end.

| What | Step 3's code | Step 4 | Allocated |
|---|---:|---:|---:|
| Source → 1 module → sink | 1.17 µs | 1.17 µs | 99 B |
| Source → 4 modules → sink | 1.71 µs | 1.62 µs | 104 B |
| Source → 4 modules → sink, each making a new message | 2.57 µs | 2.39 µs | 459 B |
| One source feeding 4 branches | 4.37 µs | 4.70 µs | 88 B |
| Source → verb choosing by type → sink | | 1.23 µs | 99 B |
| Source → verb choosing by parameters → sink | | 1.43 µs | 99 B |

What they show:
- **Step 4 costs nothing measurable.** Expiry checks, refusals and the error policy left times and allocations where they were. The differences between the two columns are within the short run's margin of error.
- **Choosing by type is free:** a verb with two inflections runs as fast as a single module.
- **Choosing by parameters** adds about 0.2 µs a message, for asking the parameters, and allocates nothing.

## Questions

90. Q54 now turns a message that a module can't take into an error everywhere, including pipelines built in C#. Step 2 dropped these quietly and counted them. Errors-only modules still let other messages go quietly. Is that how you want it?

    **Answer:** Yes.

91. Q84 left room for this: `meep run` now exits with 3 when the error policy stops the pipeline, so a script can tell that apart from a file that didn't load (1). Is 3 right?

    **Answer:** Yes.

92. `stop-pipeline at 10%` counts the last 100 messages from sources, and only once 20 have been sent, so one early failure doesn't stop a slow pipeline. Is that a good window, or would you rather count over a time, such as the last minute?

    **Answer:** That's a good window for now. We'll have to see if it works out.

93. The system defaults stop a pipeline when 10% of its messages end in unhandled errors. A watchdog whose errors aren't tapped would therefore stop itself when the site goes down. With `Tap … @errors` its errors are handled, so it doesn't. Should `stop-pipeline` be on by default, or off until a file asks for it?

    **Answer:** On by default.

94. A policy has spaces in it, so on a module line it goes in quotes: `@on-error="stop-branch after 5 in 1s"`. Under `defaults` it doesn't need them. Is that all right, or would you like another way to write it on a module?

    **Answer:** That's alright.

95. Errors never expire, so a failure is reported even when the message it came from has expired. Is that right, or should expiry apply to errors too?

    **Answer:** Errors should never expire.

All six answers keep what's built, so nothing changed for them.
