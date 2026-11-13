# Step 3: Meeplang, first slice

Written 8 November 2026. This covers what step 3 built, Meeplang as it now works, the decisions I made while building it, first benchmark numbers, and questions numbered from 81 with answer lines.

## What's built

- **`meep run file.meep`** parses a pipeline file, loads it and runs it until it halts.
  - The first Ctrl+C stops the pipeline and lets messages already in it finish. A second Ctrl+C quits at once.
  - Mistakes in the file are reported as `file(line,column): error: …`, which editors can link to.
- **The bootstrap is itself a pipeline of modules (goal 8):** Read → Parse → Build → Host.
  - Parse, Build and Host are ordinary modules in `Meep2.Meeplang.Hosting`.
  - The CLI wires them together in C#, since the bootstrap is fixed.
- **Core modules:** Interval, Read, Fetch, Extract, Where, WriteLine, Save and Tap.
- **Meeplang:**
  - layout A (the staircase) with `+` lines
  - parameters, `as`, `@` modifiers, comments, `const` and `defaults`
  - one expression language with gradual typing, used in templates, conditions and values
- **Examples** in [examples/](../examples): `hello.meep`, `merge.meep`, and `github.meep`, which fetches a live API.
- **Tests:** 150 TUnit tests, 91 of them new. The suite passed 10 runs in a row after the last change.
- **Benchmarks** for working out expressions and loading a file.

## Meeplang as it stands

Here's round 2's bus example as it now runs:

```
const Api = "https://api.busline.example/v1"
const Stop = "hove-station"

Interval 1m
    Fetch "{Api}/schedule/{Stop}" as Schedule
        timeout = 5s
        Extract "$.departures[*]"
            Where msg.minutesAway < 30
                WriteLine "{msg.route} in {msg.minutesAway} min"
        Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl"

Tap Schedule @errors
    WriteLine "Bus API failed: {msg.Error}"
```

- **Structure:**
  - A module indented under another receives what it emits.
  - Modules at the same indentation under one module are branches.
  - A run of `+` lines feeds the next unmarked line at the same indentation, from every end of each `+` line's chain.
- **A module line:** `Verb [value] [name=value …] [as Name] [@modifier[=value] …]`. A line `name = value` indented under a module is another of its parameters.
- **Values:**
  - numbers, which are decimals, so `7 / 2` is 3.5. A number a decimal can't hold carries on as a big number (Q81).
  - durations: `250ms`, `5s`, `1.5m`, `2h`, `1d`, `1h30m`, or TimeSpan's `00:00:05` and `1.00:00:00`
  - `true`, `false` and `null`
  - two kinds of string:
    - `"…"` strings are templates, with `{expression}` and `{expression:format}` holes, and `\"`, `\\`, `\n`, `\t` escapes
    - `'…'` strings are taken literally, with no holes or escapes
- **Expressions:**
  - `msg`, `fam.Name`, and constants
  - members, method calls such as `msg.Error.Contains("timeout")`, and indexes such as `msg.stops[0]`
  - `== != < <= > >=`, `+ - * / %`, `and`, `or`, `not`, `is` and `is not`
- **Modifiers:** `@buffer=N`, `@bypass`, `@errors` and `@no-errors` work now. `@expires`, `@accept-expired` and `@on-error` arrive in step 4, and say so if used.
- **Defaults:** `buffer` works now. `expires` and `on-error` arrive in step 4.

## How checking works

**When a pipeline loads:**
- The loader works out which message types can reach each module, from what the modules before it declare they emit (`[Emits]`).
- Each expression is compiled against those types. If it fits none of them, that's an error, with a suggestion where there's an obvious one, such as `Tick has no 'Cuont'. Did you mean 'Count'?`
- A module whose input type can't arrive at all is an error too: Extract indented under Interval would never receive anything.
- If a module before doesn't say what it emits, there's a warning that the expression can't be checked yet (Q21).

**When messages arrive:**
- Expressions are compiled for each concrete message type the first time it arrives.
- A type an expression doesn't fit skips the module (Q69). For example, errors skip `Where msg.Count > 1` and carry on.
- JSON members are read by name as each message arrives. A missing member becomes an error message from that module.
- Expressions that don't use `msg` or `fam`, such as `"{Api}/schedule/{Stop}"`, are worked out once, when the pipeline loads.

## Decisions I made while building

1. **Layout A details:**
   - Indentation is counted in spaces, and a tab counts as four.
   - A `+` line starts a chain of its own, so it has to be a source or a Tap. Sources and Taps can't be indented under another module.
   - When a `+` chain branches, every end of it feeds the next line, as I proposed when step 1 finished.
   - `Tap X` needs a module named `X` with `as` (Q83).
2. **Parameters:**
   - A parameter written in Meeplang matches its property in C# ignoring case and hyphens, so `max-size` sets `MaxSize`.
   - Modules take a parameters record in their constructor, and leaving out a required property is a load error.
   - A record can check its own values; a bad value, such as `Interval 0s`, is reported at that value.
   - Generated records and `ParametersChanged` (Q55) come with live editing in step 8. Until then, the loader fills records in by reflection.
3. **Strings:** a backslash that isn't a known escape is kept as it is, so `"C:\data"` stays as written. Single-quoted strings are literal (Q85).
4. **Templates:**
   - `{msg}` writes a message as its text content if it has any (such as an HTTP body or a JSON value), and otherwise as its description.
   - Times are in UTC. `{msg.Created.ToLocalTime():HH:mm}` gives local time (Q82).
5. **Names:**
   - Member names are case-sensitive, and JSON members are named exactly as in the JSON.
   - If a JSON member shares a name with a property of the message itself (`Id`, `Created`, `Text`, `Value`), the property wins. Write `msg["Text"]` to get the JSON member.
6. **Numbers** are decimals, and carry on as big numbers when a decimal can't hold them. See "After your answers" (Q81).
7. **Fetch:**
   - A status that isn't a success becomes an `HttpErrorMessage`, with the status code and body (Q86).
   - Timeouts and network failures become ordinary errors.
   - The body is read into memory, up to 16 MiB (`max-size`). The timeout is 100 seconds unless set.
   - Requests send `User-Agent: Meep2/1.0`, which APIs such as GitHub's require.
8. **Extract** supports the common JSONPath selectors, but not filters; use Where on the results instead. It emits one `JsonMessage` per match.
9. **Save:**
   - It adds a line per message by default; `mode=overwrite` replaces the file instead (Q87).
   - It creates folders as needed. `~` means your home folder, and other relative paths start from the current folder.
10. **WriteLine and Save are passthrough modules**, so they never hold messages up.
11. **Interval** fires straight away, then once each period. `count` limits how many times, and `payload` adds text to each tick.
12. **Expected failures are emitted, not thrown.** A bad address, a timeout or a missing JSON member becomes an error message from the module. So the module isn't replaced, which the runtime does when a module throws.
13. **Exit codes:** `meep run` exits with 1 if the file couldn't be read or loaded, 2 for bad usage, and 0 otherwise. Errors left unhandled while it runs are written to standard error (Q84).
14. **Not supported yet:** `pipeline` and `template` blocks, `#include`, `#plugin`, `Verb.Inflection`, and `secret()`. Each one is reported as arriving later, rather than being misread.
15. **AOT:**
    - Meeplang itself isn't AOT-compatible: the expression compiler uses reflection and generates code at runtime.
    - The SDK, runtime and core modules still are.
    - A sealed AOT build would need generated accessors for expressions.
16. **New in the SDK:**
    - `BigDecimal`, for numbers a decimal can't hold (Q81)
    - `Computed<T>`, for parameters worked out for each message
    - attributes: `[Verb]`, `[Positional]`, `[Emits]` and `[EmitsInput]`
    - `ModuleReference`, for parameters that name another module
    - interfaces: `IHasText`, `IHasPath` and `IHasDynamicMembers`
    - `Message.ToText()`
    - on `ModuleContext`: `Stopping`, which lets Host stop the pipeline it runs, and `EmitError()`

## First benchmark numbers

Measured with BenchmarkDotNet's short run on the same Mac as step 2, so treat them as rough. Times are for one message, after the expression has been compiled for its type. Measured again after your answers, with numbers as decimals.

| What | Time | Allocated |
|---|---:|---:|
| `msg.Value < 30` on a typed message | 7 ns | 0 B |
| `msg.minutesAway < 30` on a JSON message | 125 ns | 64 B |
| `"{msg.Value} min"` on a typed message | 40 ns | 80 B |
| `"{msg.route} in {msg.minutesAway} min"` on a JSON message | 290 ns | 256 B |
| `msg.Value * 2 + 1 > 20` on a typed message | 21 ns | 0 B |
| `msg.minutesAway * 2 + 1 > 20` on a JSON message | 210 ns | 128 B |
| `msg.total * 2 + 1 > 20` on a 40-digit JSON number | 1.6 µs | 944 B |
| Parsing and loading a four-line file | 0.87 ms | 54 KB |

What they show:
- **Typed expressions run as fast as hand-written C#.** A condition takes about 7 ns, including finding the version compiled for the message's type, and allocates nothing.
- **Sums in decimal cost more than sums in whole numbers would,** but not much: about 21 ns for two sums and a comparison, with nothing allocated.
- **JSON members cost about 20 times as much,** because each is looked up by name and its value boxed. This is the case for gradual typing: modules that deserialise JSON into their own types get the fast path.
- **Big numbers cost about 1.5 µs a sum.** That's only paid for values a decimal can't hold.
- **Loading is dominated by compiling expressions,** about 0.2 ms each. That's paid once per expression and message type, not per message.

## Questions

81. Should dividing whole numbers give a whole number, as C# does (`7 / 2` is 3), or a fraction, as most scripting languages do (3.5)? At the moment, whole numbers from C# types give 3 and JSON numbers give 3.5.

    **Answer:** Fractions. Numbers should be decimal types. Can they also auto-upgrade to a bignum if they exceed Decimal's range?

82. Times in templates are in UTC unless converted with `.ToLocalTime()`. Should templates show local time by default instead?

    **Answer:** No, any conversion to local time needs to be explicit, usually in an output or UI.

83. Should `Tap X` be able to find a module by its verb when only one module has that verb, or should Tap always need an `as` name?

    **Answer:** No, Tap should require a name so you don't get unexpected behavior if an edit adds a second use of the same verb.

84. Should `meep run` exit with 1 when an error went unhandled during the run, as it does now, or only when the file couldn't load?

    **Answer:** Only if the Meeplang file doesn't load. Meep should log unhandled errors and keep going until it reaches one of the abort thresholds.

85. Are single-quoted strings, taken literally without holes or escapes, worth keeping, for Windows paths and text with braces?

    **Answer:** Yes.

86. Should Fetch treat statuses that aren't a success (such as 404) as errors, as it does now, or emit them as ordinary responses?

    **Answer:** Treat them as errors. They would not be fatal errors since we don't know the problem isn't at the remote host (and the author may be using Meep as a watchdog for that host).

87. Should Save add a line to its file for each message by default, as it does now, or replace the file?

    **Answer:** Append/Create by default. Have a parameter to force replacing the file each time.

## After your answers

- **Q81: numbers are decimals now.** `7 / 2` is 3.5, and `0.1 + 0.2 == 0.3` is true. Your question was whether a number can switch to a big number when it goes past decimal's range. It can, without the pipeline's author doing anything, and it's built:
  - **What a big number is:** .NET has no big decimal type, so the SDK has its own, `BigDecimal`: a `BigInteger` times a power of ten.
    - Adding, subtracting and multiplying keep every digit.
    - Dividing keeps at least 28 significant digits, as decimal does.
  - **When a number becomes big:**
    - A number written in a file or read from JSON becomes big if a decimal can't hold it exactly: bigger than about 7.9 × 10²⁸, smaller than 10⁻²⁸, or with more than 28 digits.
    - A sum whose answer doesn't fit carries on as a big number. Decimal throws when an answer is too big, and Meep catches that. Meep also checks for answers too small for decimal, which decimal would round to zero.
  - **Back again:** a big number turns back into a decimal as soon as it fits, so ordinary values stay fast.
  - **What it costs:**
    - Sums on typed members are compiled to work in decimal. The first time one overflows, that expression is compiled again for that message type to use big numbers, and keeps that version.
    - So a pipeline that never sees huge numbers pays nothing for them, and a typed condition still takes about 7 ns. A sum on big numbers takes about 1.5 µs.
  - **A limit:** written out in full, a big number can have at most 1,000 digits. Without a limit, a JSON value such as `1e999999999` could make a sum use up memory. Python limits how long a whole number it will read from text, for the same reason.
  - **Formats:** big numbers take the standard formats (`N2`, `F0`, `E3`, `P1`, and `D` for whole numbers), but not custom ones such as `0.00`. A custom format on a big number is an error that says which formats work.
  - **Dividing by zero** is an error, even with doubles, which would otherwise give infinity.
  - **Whole-number parameters** only take whole numbers: `count=2.5` is a load error, "Expected a whole number, but this is the number 2.5."
  - **Doubles** from C# modules stay doubles for now (Q88).
- **Q82:** no change. Templates stay in UTC.
- **Q83:** `Tap` needs an `as` name now.
  - `Tap Interval`, where Interval is a verb, gets: "There's no module called Interval. Modules are found by name, so give the Interval you mean a name with 'as', and use that."
  - `fam.Interval` still finds a module by its verb, as you chose in Q74. It only searches a message's own ancestors, so adding another Interval elsewhere can't change what it finds.
- **Q84:** `meep run` exits with 1 only when the file can't be read or loaded. Unhandled errors are written to standard error, and the pipeline carries on.
  - The abort thresholds (`stop-branch after 5 in 1s`, `stop-pipeline at 10%`) arrive with the error policy in step 4.
  - Step 4's notes will propose an exit code for a pipeline those thresholds stop.
- **Q85:** no change. Single-quoted strings stay.
- **Q86:** no change. Fetch emits these as `HttpErrorMessage` rather than throwing, so they never restart Fetch or stop the pipeline. A watchdog can catch them with `Tap … @errors`.
- **Q87:** no change. Save already adds a line by default, and `mode=overwrite` replaces the file.

## Questions from your answers

88. A C# module can emit a `double`, as `TimeSpan.TotalSeconds` does. For now, a double stays a double, and a sum with one is done in double, as in C#. A double is approximate already, and converting one to decimal has costs:
    - It takes about 85 ns.
    - .NET converts a double's exact binary value, so 0.1 becomes 0.1000000000000000055511151231.
    - Converting through the double's shortest text instead gives 0.1, but takes longer.

    Should doubles stay doubles, or become decimals like every other number?

    **Answer:** Stay as doubles.

89. Like C#'s decimal, Meeplang numbers keep trailing zeros. `1.50 * 2` is 3.00, and a JSON price of 12.50 is written as 12.50. Should Meeplang keep them, or drop them when a number is written without a format? A format such as `{msg.price:F2}` would still give a fixed number of places.

    **Answer:** Keep them.

Both answers keep what's built, so nothing changed for them.
