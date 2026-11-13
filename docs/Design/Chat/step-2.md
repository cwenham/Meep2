# Step 2: messages and the push core

Written 8 November 2026. This covers what step 2 built, the decisions I made while building it, first benchmark numbers, and questions numbered from 76 with answer lines.

## What's built

**In the SDK** (what module authors use):
- `Message`, the base record. Each message has an `Id` (UUID version 7, so ids sort by time), `Created`, `Expires`, `Parents`, `EmittedBy` and `Fam`.
- `ErrorMessage`, `Parents`, `Deadline` and `Family`.
- Three base classes for modules:
  - `ASource` for fountainheads
  - `AInflection<TIn>` for modules that handle messages
  - `APassthrough<TIn>` for modules whose work doesn't change the message
- `ModuleContext`, which modules use to `Emit` messages, or `EmitTracked` to learn when a message and everything made from it have finished.

**In the runtime:**
- `PipelineBuilder` and `Pipeline`. A pipeline can be run, stopped, disposed, and asked for per-module counts.
- `NodeOptions`, which are Meeplang's `@` modifiers in C# form: buffer size, bypass, `@errors` and `@no-errors`.
- `PipelineOptions`: the default buffer size, how long stopping waits for messages to finish, and where unhandled errors go.

**Tests and benchmarks:**
- 56 TUnit tests. The suite passed 16 runs in a row after the last change.
- To check the tests can fail, I broke two behaviours on purpose (dropping the newest message instead of the oldest, and treating every error as handled). The matching tests failed, and I restored the code.
- Benchmarks for making messages, looking up fam, and pushing messages through chains and branches.

Here's what writing a module and running a pipeline looks like from C#:

```csharp
// Handles Numbers and emits a doubled copy, which becomes a child of the original.
public sealed class Double : AInflection<Number>
{
    protected override ValueTask HandleAsync(Number message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(message with { Value = message.Value * 2 });
        return ValueTask.CompletedTask;
    }
}

// Numbers and Log stand in for core modules that arrive in step 3.
var builder = new PipelineBuilder();
var source = builder.AddSource(() => new Numbers(1, 2, 3), name: "Source");
var doubled = builder.Add(() => new Double());
var errors = builder.Add(() => new Log(), options: new NodeOptions { Errors = ErrorDelivery.Only });
builder.Chain(source, doubled);
builder.Connect(doubled, errors);

await using var pipeline = builder.Build();
await pipeline.RunAsync(); // returns once the source has finished and every message has left
```

## How it works

- **Buffers and loops:** each module that handles messages has a buffer and a loop on its own task, which hands it one message at a time, in order. The buffer drops its oldest message when it's full.
- **Emitting never waits:** it puts the message straight into each downstream module's buffer, so no module waits on another.
- **Each delivery follows these rules:**
  - a module that handles the message's type gets it in its buffer
  - an error skips any module that doesn't take it, and looks further downstream for one that does
  - with bypass, other messages skip a module too
  - for a passthrough module, the message also goes straight on to the modules after it
- **Halting:** one counter per pipeline covers messages in buffers, messages being handled, and sources still running. The pipeline halts when it reaches zero.
- **Tracking:** some messages carry tracking scopes, which their descendants inherit, so a scope's count covers a message and everything made from it.
  - Tracked emits and every error use scopes now.
  - Leases on live connections will use the same mechanism in step 5.
  - Ordinary messages carry no scopes and pay nothing for them.

## Decisions I made while building

1. **Equality.** Two messages are equal only when they have the same `Id`. A record's normal equality would compare every field, including the parents and so the whole ancestry, which is slow. And two messages with the same contents are still different messages. (Q76)
2. **Ids are made without the operating system's random generator.**
   - The first benchmarks showed that making a message cost about 350 ns, almost all of it in `Guid.CreateVersion7`. That draws on the operating system's secure random generator, which on this Mac takes about 280 ns and goes through a lock. With four threads making ids at once, each id took about 2.7 µs.
   - Ids are still UUID version 7, but their random bits now come from .NET's per-thread generator, which takes no lock.
   - Ids only need to be unique, so this is safe. They're not unpredictable, though, so they shouldn't be used as secrets, such as access tokens in URLs.
3. **Parents are set for you:**
   - a copy made with `with` is a child of the original
   - a new message emitted while handling another becomes its child
   - copies a module makes but doesn't emit, such as intermediate steps, are given parents the same way
4. **Expiry uses the monotonic clock** (`Deadline`), as round 1 suggested, so system clock changes don't affect it.
   - When a message is emitted, its expiry becomes the earliest of its own and its parents'. A module can bring expiry forward but can't put it back.
   - Clearing or extending expiry is left to Meeplang's `@expires` (Q78).
   - Expired messages aren't blocked yet; that's still step 4.
5. **fam includes the message itself**, as the original's did. So `fam.Map` on a message that Map emitted returns that message (Q77).
6. **Modules are added as factories** (`() => new Double()`), because after a module throws, the runtime disposes it and makes a fresh one.
   - A source that throws reports its error and counts as finished. When the error policy arrives in step 4, it will decide whether sources restart (Q79).
7. **Moved forward from step 4:** errors skipping modules that don't take them, `@bypass`, `@errors` and `@no-errors`. These turned out to be part of how delivery works, so they belonged in the core. The rest of step 4 is unchanged: verbs, expiry, and the error policy.
8. **Handled errors (Q71):**
   - A module handles an error by taking it in and not passing that same error on.
   - Passthrough and errors-only modules count as handling every error they take, because for them the runtime decides what goes on, not the module (Q80).
9. **Unhandled errors** go to a callback, which writes to standard error by default. The error policy will build on this.
10. **`EmitTracked` reports an outcome:** either `Completed`, or flags for `Dropped`, `UnhandledError` and `Abandoned`. A source can acknowledge on `Completed` and retry otherwise.
11. **Stopping:**
    - Cancelling `RunAsync`, or calling `Stop()`, stops the sources and lets messages already in flight finish.
    - After `DrainTimeout` (30 seconds by default), modules are cancelled and whatever is left is abandoned.
    - A module that ignores cancellation can still hold up shutdown.
12. **Small things:**
    - The default buffer is 1024 messages per module, until benchmarks suggest better.
    - A module's verb is its class name until verbs arrive in step 4.
    - Inputs aren't numbered yet; Zip will need that in step 9.

## First benchmark numbers

Measured with BenchmarkDotNet's short run on this Mac (Intel Xeon W-2140B, .NET 11 RC3), so treat them as rough. Times and allocations are per message. "Was" is the first run, before the change to ids.

| What | Time | Allocated |
|---|---:|---:|
| Make a message (`new`) | 64 ns (was 353 ns) | 88 B |
| Copy a message (`with`) | 64 ns (was 329 ns) | 88 B |
| Find an ancestor 20 generations up | 89 ns | 0 B |
| Source → 1 module → sink, passing messages on | 0.81 µs | 99 B |
| Source → 4 modules → sink, passing messages on | 1.18 µs | 108 B |
| Source → 4 modules → sink, each module making a new message | 1.72 µs (was 6.35 µs) | 459 B |
| One source feeding 4 branches | 2.68 µs | 88 B |

What they show:
- **Moving a message through a module allocates nothing.** The 88 bytes or so per message are the message itself.
- **Cost per module:** each extra module that passes messages on adds about 125 ns. A module that makes a new message adds about 135 ns more.
- **Throughput:** a chain of four modules handles roughly 600,000 to 850,000 messages a second.
- **Branching costs more than it should.** A source feeding four branches manages about 370,000 messages a second. The likely causes are waking each branch's loop for every message, and the one counter that every module in the pipeline updates. Nothing depends on this yet, so I've left it for later.
- **Buffer size:** the benchmarks use buffers as big as the run. Otherwise a source this fast would fill them, and the oldest messages would be dropped, by design. These numbers don't yet suggest a better default buffer size, so it stays at 1024.

## Questions

76. Should messages be equal only when they have the same `Id`, rather than by comparing their contents?

    **Answer:** Yes.

77. Should fam include the message itself, as the original's did?

    **Answer:** No.

78. Should a module be able to bring expiry forward but not put it back, leaving extending or clearing it to Meeplang's `@expires`?

    **Answer:** Yes.

79. For step 4: when a source throws, should it restart by default (waiting a little longer each time), or stop?

    **Answer:** Restart by default after a fixed delay, defaulting to 100ms, until it fails a default 3 times in a row without emitting messages. Emitting a non-error message resets the count. Defaults can be overridden the same way as other defaults at the pipeline and verb level in Meeplang. 

80. Should passthrough and errors-only modules count as handling every error they take?

    **Answer:** No, only if they don't pass it on.

## After your answers

- **Q77:** `fam` now searches only a message's ancestors, starting with its parents.
- **Q80:** passthrough and errors-only modules no longer handle every error they take. Every module now follows the same rule: it handles an error unless it passes that same error on to a module after it.
  - I've read "passes it on" as "there's a module after it to receive it". So the last module in a branch handles any error it takes, even a passthrough module or one that emits the error again.
  - That keeps your answer to Q17 working: errors from `Tap X @errors` that reach a final `Log` count as handled.
- **Q79:** recorded for step 4. A source that throws restarts after a fixed delay (100 ms by default), until it fails three times in a row without emitting messages. Emitting a non-error message resets the count. Pipelines and verbs can override the defaults in Meeplang.
