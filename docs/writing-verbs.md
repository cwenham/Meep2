# Writing verbs and inflections

A guide for developers adding a verb to Meep, either to the core modules or in a plugin. It's written against the SDK as it stands on 14 November 2026. For the verbs that already exist, see [verbs.md](verbs.md).

## The words

- **A verb** is a word in Meeplang, such as `Fetch` or `Where`. It's what a pipeline writer types at the start of a line.
- **An inflection** is one C# class that does a verb's work. Most verbs have one. A verb with several, such as `Fetch` (`HTTP` and `File`), has a class for each, and the runtime picks one for each message.
- **A module** is an instance of an inflection, sitting on a line of a pipeline.
- **A message** is an immutable record that flows between modules. Every message type derives from `Message`.

Everything a module author needs is in `Meep2.SDK`: the base classes and attributes in `Meep2.SDK.Modules`, and the message types in `Meep2.SDK.Messages`.

## A first verb

Here's a complete verb, `Shout`, adapted from the test plugin in `tests/Meep2.TestPlugin/Shout.cs` with the error handling a real verb needs:

```csharp
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

/// <summary>Text, shouted: the plugin's own message type.</summary>
public sealed record Shouted(string Text) : Message, IHasText;

/// <summary>Shouts its value: "tick 1" becomes "TICK 1!".</summary>
[Verb("Shout")]
[Emits(typeof(Shouted))]
public sealed class Shout(ShoutParameters parameters) : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = parameters.Text.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }
        context.Emit(new Shouted(text.ToUpperInvariant() + "!"));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Shout"/>.</summary>
public sealed record ShoutParameters
{
    /// <summary>What to shout.</summary>
    [Positional]
    public required Computed<string> Text { get; init; }
}
```

In Meeplang:

```
Interval 1s count=2
    Shout "tick {msg.Count}"
        WriteLine
```

That's the whole pattern: a class with `[Verb]`, a parameters record, `HandleAsync`, and `[Emits]` for the message types it makes. The rest of this guide fills in the details.

## Choosing a base class

Derive from one of three classes. Don't derive from `AModule` or the non-generic `AInflection` directly; their constructors aren't public.

| Base class | For | Example |
|---|---|---|
| `ASource` | A module with no input, which emits of its own accord: a timer, a listener, a file read once. | `Interval`, `Read`, `Listen` |
| `AInflection<TIn>` | A module that handles messages and emits zero or more for each. | `Fetch`, `Where`, `Batch` |
| `APassthrough<TIn>` | A module whose work doesn't change the message, such as writing it somewhere. | `WriteLine`, `Save`, `Send` |

### AInflection&lt;TIn&gt;

`TIn` is the most general type the module can handle: a message type, or an interface that message types implement, such as `IHasText` or `IHasBody`. The runtime never hands the module anything else. Messages of other types skip it.

`HandleAsync` is called one message at a time, in order, so a module can keep state in its fields without locking. Whatever it emits while handling a message becomes a child of that message. Emitting the message it was given passes it on unchanged, which is how a filter works:

```csharp
[Verb("Where")]
[EmitsInput]
public sealed class Where(WhereParameters parameters) : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (parameters.Condition.Evaluate(message))
                context.Emit(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
        }
        return ValueTask.CompletedTask;
    }
}
```

### APassthrough&lt;TIn&gt;

The runtime passes each message downstream straight away, and the module handles its copy on its own thread, so it never holds up the pipeline. When it falls behind, its buffer drops the oldest messages. Because errors are passed on too, a passthrough module only handles an error when nothing comes after it.

A passthrough module can still emit an error if its own work fails, as `WriteLine` does when its text can't be worked out.

### ASource

Override `RunAsync`. The source finishes when it returns, and a pipeline halts once every source has finished and every message has left it. A source that runs until stopped should return, or throw `OperationCanceledException`, when its cancellation token fires.

```csharp
[Verb("Read")]
[Emits(typeof(FileText))]
public sealed class Read(ReadParameters parameters) : ASource
{
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var path = Paths.Expand(parameters.Path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        context.Emit(new FileText(path, text));
    }
}
```

A source can emit at any time and from any thread through its context. What it emits has no parent.

## The Verb attribute and constructors

`[Verb("Name")]` names the verb. `Inflection = "HTTP"` names the inflection; without it, the inflection takes the class's name. A verb with one inflection doesn't need to name it.

The class needs one of these public constructors:

- one that takes its parameters record as its only argument, or
- one with no arguments, if the verb takes no parameters.

The registry looks for these when it registers the class and refuses it otherwise. Other constructors are fine alongside, as `WriteLine` has one that also takes a `TextWriter` for tests.

A new instance is made for each module in a pipeline, and again when the runtime replaces one (see [Errors](#errors)).

## Parameters

The parameters record is an ordinary C# record with public `init` properties and a public parameterless constructor (a record without a primary constructor has one). The loader fills it in from the pipeline line.

### Names

Properties are written in kebab case in Meeplang: `MaxSize` is `max-size`, `CountStart` is `count-start`. Matching ignores case and hyphens. A parameter can be given on the module's line or on a line of its own under it:

```
Fetch "https://api.example.com/big.json" timeout=30s
    max-size = 1048576
```

At most one property can be marked `[Positional]`. It's the value written straight after the verb without a name, such as the URL in `Fetch "https://…"`.

Mark properties the verb can't do without as `required`. The loader reports a missing one, saying how to give it. Give the others a default with a property initialiser.

### Types

| Property type | Written as | Notes |
|---|---|---|
| `string` | `"text"` | |
| Whole numbers, `decimal`, `double` and so on | `100`, `2.5` | The loader says when a number doesn't fit the type. |
| `bool` | `true`, `false` | |
| `TimeSpan` | `5s`, `250ms`, `1m` | |
| An enum | a bare name, as in `mode=append` | Matched ignoring case. A wrong name lists the choices. |
| Any type implementing `IParsable<T>` | `"text"` | Parsed when the pipeline loads, as `JsonPath` is. |
| `Computed<T>` | any expression | Worked out for each message. See below. |
| `ModuleReference` | a module's name, as in `Tap Schedule` | The loader connects the named module to this one. |
| `Book` | a book's name, as in `"Countries"` | Looked up in the library when the pipeline loads. |

Make the type nullable (`int?`, `TimeSpan?`, `Computed<string>?`) for an optional parameter with no default.

Every type except `Computed<T>` must be the same for every message: the loader reports a value that uses `msg` or `fam`, saying the parameter can't.

### Computed&lt;T&gt;: values worked out for each message

Use `Computed<T>` for anything a pipeline writer might want to take from the message: a URL template, a condition, a key. Call `Evaluate(message)` to get the value.

- A value that doesn't use the message is worked out once when the pipeline loads, and `IsConstant` is true.
- `Evaluate` throws `EvaluationException` when it can't work out a value, for instance when a member it reads isn't there. Catch it and emit an error, as the examples above do. Don't let it escape.
- A message whose type the expression doesn't fit skips the module, as with `@bypass`. So `Where msg.minutesAway < 30` lets messages without `minutesAway` past untouched.
- In C# code and tests, make one with `Computed<T>.Constant(value)` or `Computed<T>.From(message => …)`.

`[ForErrors]` marks a `Computed<T>` that's only worked out for errors, as `error-reply` is on a `Send` that replies. When a record has one, its other `Computed<T>` parameters are only for messages that aren't errors, so an expression written for one kind doesn't keep the other kind out of the module.

### Checking values

Check a value in the property's `init` accessor and throw `ArgumentOutOfRangeException`, `ArgumentException`, `FormatException` or `InvalidOperationException`. The loader reports the exception's message on the pipeline line, as a load error:

```csharp
/// <summary>How long between ticks.</summary>
[Positional]
public required TimeSpan Period
{
    get;
    init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Period), "The period must be longer than zero.");
}
```

Write the message for the person writing the pipeline. The loader strips .NET's ` (Parameter 'name')` suffix.

Checks that need several parameters together belong in `IChosenByParameters` (below), as `Batch` uses it to insist on a size or a time.

### The `by` keyword

A parameter called `By` can be written as a keyword without `=`, as in `Zip by msg.OrderId`. `Zip` and `Distribute` both use it, as a `Computed<object?>?`.

## Messages

### Making a message type

A message type is a record deriving from `Message`. Make it `sealed` unless other types should derive from it:

```csharp
/// <summary>What Interval emits each time it fires.</summary>
public sealed record Tick(long Count) : Message
{
    /// <summary>The text given with Interval's payload parameter, if any.</summary>
    public string? Payload { get; init; }
}
```

Its public properties are what Meeplang expressions read, as in `msg.Count`. Give them XML doc comments; they're the only documentation many pipeline writers will see.

Implement the content interfaces where they fit, so other verbs can take your messages without knowing your type:

| Interface | Means | Used by |
|---|---|---|
| `IHasText` | The content reads as text. `ToText()` and `{msg}` use it. | `Extract`, `WriteLine`, `Save`, `Classify` |
| `IHasBody` | It has a body of bytes, possibly streamed. | `Send` |
| `IHasPath` | It's about a file. | |
| `IHasDynamicMembers` | Its members are only known when it arrives, as JSON's are. Expressions read them by name for each message instead of checking them at load. | `JsonMessage` |

Messages don't change once made. To change one, copy it with `with`: the copy is a new message, with its own `Id`, whose parent is the original. Two messages are equal only when they have the same `Id`.

### Declaring what a module emits

The loader checks expressions against the types a module can emit, so declare them:

- `[Emits(typeof(Tick))]` for each message type the module makes. Use it more than once for several.
- `[Emits(typeof(TcpConnected), When = "Connections")]` for a type that a `bool` parameter turns on.
- `[EmitsInput]` for a module that passes on what it receives, as a filter does. Its output then has the same types as its input.

Errors never need declaring: any module can emit them.

Message types a module declares, and public message types in its assembly, become names that `is` can test, as in `Where msg is Tick`.

### Parents and family

A message emitted while handling another becomes its child. A message that combines several, such as a batch, names them itself:

```csharp
context.Emit(new Batched { Parents = new Parents(_items) });
```

`msg.Fam` (written `fam` in Meeplang) finds a message's ancestors by the name of the module that emitted them, so later modules can reach back to, say, the request a response came from.

### Expiry

A message's `Expires` is set to the earliest of its own and its parents' when it's emitted. A module can bring a deadline forward by setting `Expires` but can't put it back.

## Errors

There are two ways for a module to fail:

- **Expected failures**, such as a server that doesn't answer, a file that's missing, or an `EvaluationException`: call `context.EmitError("…", exception)`. This emits an `ErrorMessage` in place of a result, and the module carries on.
- **Unexpected failures**: just let the exception escape. The runtime emits an `ErrorMessage` with the message being handled as its parent, then disposes the module and makes a fresh one for the next message. Any state in its fields is lost.

So catch what you can foresee and report it in words a pipeline writer understands, such as `"https://example.com didn't answer within 10s"`.

To report an HTTP-style failure with more detail, derive a record from `ErrorMessage`, as `HttpErrorMessage` does, and emit it with `context.Emit`.

### Receiving errors

`ErrorMessage` derives from `Message`, so a module with `TIn` of `Message` takes errors too, unless the pipeline says otherwise with `@no-errors`. Errors skip modules whose input type they don't fit.

An error counts as handled once a module takes it and doesn't pass that same error on. So the last module in a branch handles any error it takes, and a filter that emits its input passes errors along. Two calls change that:

- `context.Answered(error)` says the module has answered the error for whoever was waiting, as `Send` does when it replies to a request with an error. The error policy then logs it but doesn't count it towards stopping the pipeline.
- `context.LeftAlone(error)` says the module took the error but did nothing with it, so it still reaches the error policy.

If your module has nothing sensible to do with errors, take a narrower `TIn` than `Message`, or check `message is ErrorMessage` and decide.

## Several inflections

To give a verb several inflections, write a class for each with the same verb name and different inflection names:

```csharp
[Verb("Fetch", Inflection = "HTTP")]
[Emits(typeof(HttpResponse))]
public sealed class HttpFetch(FetchParameters parameters) : AInflection<Message>, IChosenByParameters<FetchParameters> { … }

[Verb("Fetch", Inflection = "File")]
[Emits(typeof(FileText))]
public sealed class FileFetch(FetchParameters parameters) : AInflection<Message>, IChosenByParameters<FetchParameters> { … }
```

Rules:

- A verb's inflections must agree on whether they're sources, and on whether they're passthrough modules. The registry refuses one that doesn't.
- Two classes can't give the same verb the same inflection name.
- They can share a parameters record, as `HttpFetch` and `FileFetch` share `FetchParameters`, or have their own.

### How the runtime chooses

1. **By message type.** Each message goes to the inflection with the most specific `TIn` that accepts it.
2. **By parameters.** An inflection that implements `IChosenByParameters<TParameters>` can refuse parameters it can't serve:

```csharp
/// <summary>Takes http and https addresses.</summary>
public static string? WhyNot(FetchParameters parameters, Message message)
{
    ArgumentNullException.ThrowIfNull(parameters);
    var url = parameters.Url.Evaluate(message);
    return Addresses.IsWeb(url, out _) ? null : $"needs an http or https address, not {url}";
}
```

Return null to accept, or a short reason to refuse. When the parameters are the same for every message, `WhyNot` is asked once at load: refusing inflections are left out, and if none are left the pipeline fails to load with each reason. When a parameter is worked out for each message, it's asked for each message, and the message goes to the first inflection that takes it. Look at the parameters, not the message itself: at load time the message is a stand-in.

A verb with one inflection can use `WhyNot` too, just to check its parameters together.

3. **By name.** A pipeline can name one directly as `Fetch.HTTP`, or as `HTTP` alone if no other verb has an inflection of that name.

## More the context offers

`ModuleContext` is how a module talks to its pipeline. Beyond `Emit` and `EmitError`:

| Member | What it's for |
|---|---|
| `Emit(message, lease)` | Emits a message carrying a live resource, such as a request waiting for its reply. See [Leases](#leases). |
| `EmitTracked(message)` | Emits and returns a task that completes with a `MessageOutcome` once the message and everything made from it has left the pipeline. For sources that acknowledge or commit what they read. Don't await it inside `HandleAsync` if messages can loop back to the same module. |
| `Keep(message)` / `LetGo(message)` | Keeps a message in flight, leases and all, after `HandleAsync` returns, as `Batch` and `Zip` do while they wait for the messages that go with it. |
| `FlushAfter(delay)` | Asks for `FlushAsync` to be called after a while, between messages. A later request replaces an earlier one. |
| `Finishing` | During `FlushAsync`, true when this is the last chance to emit what the module keeps. |
| `BufferSize` | How many messages the module's buffer holds, for a module that needs a limit on what it keeps. |
| `Input` | Which line fed the message, counting from 1, for a module with several inputs (`Zip`). |
| `Outlet` | Emits at any time from any thread, for a module that handles messages but also receives things of its own accord, such as on a connection it opened. Take it while handling a message. |
| `Shared` | A dictionary the modules of one pipeline share by name, as `Send` finds a `Listen`'s connections. A plugin should start its keys with its own name. |
| `Stopping` | Cancelled when the pipeline is asked to stop. Most modules can ignore it. |
| `PipelineName` | The pipeline's file name without its extension, for modules that keep things per pipeline. |
| `Module` | The module's `ModuleInfo`: its number, verb, inflection and name. |

### Keeping messages and flushing

A module that gathers messages calls `Keep` for each, and later emits one made from them (with them among its `Parents`) or lets them go. Override `FlushAsync` to emit what's left when asked:

```csharp
protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
{
    context.Keep(message);
    _items.Add(message);
    if (_items.Count == 1 && parameters.Within is { } within)
        context.FlushAfter(within);
    if (_items.Count >= parameters.Size)
        Send(context);
    return ValueTask.CompletedTask;
}

protected override ValueTask FlushAsync(ModuleContext context, CancellationToken cancellationToken)
{
    if (_items.Count > 0)
        Send(context);
    return ValueTask.CompletedTask;
}
```

The runtime also calls `FlushAsync` when nothing else is left for the pipeline to do. Anything still kept after that is let go as the pipeline halts.

### Leases

A `Lease` is a live resource that travels with messages: an HTTP request waiting for its reply, an open response body, a connection. Derive from `Lease`, override `OnClosedAsync` to release the resource, and emit it with `context.Emit(message, lease)`. Messages made from that message inherit the lease. The runtime closes it once the last message carrying it has left the pipeline and nothing `Hold`s it. Override `OnFinishedAsync` to do something first, such as send a default reply.

## Optional contracts

Implement these interfaces when they fit. The registry finds them by reflection.

| Interface | For |
|---|---|
| `IChosenByParameters<TParameters>` | Refusing parameters the inflection can't serve. See above. |
| `IReconfigurable<TParameters>` | Taking new parameters while the pipeline runs, keeping state such as a count, a cache or an open connection. Return true from `ParametersChanged(old, next)` to carry on, or false to be replaced by a new instance. A handler is asked between messages. A source is asked while it runs, so it should only change what its loop reads afresh each time round, as `Interval` does with its period. Without this interface, a module whose line is edited is replaced. |
| `IEmitsAtOnce<TParameters>` | Saying how many messages the module can emit for one, from `static long? MostAtOnce(parameters)`. The loader warns when the next module's buffer is too small, since emitting never waits and a full buffer drops the oldest. `Enumerate` with `all=true` uses it. |
| `IRouteMessages` | Sending each message down one of several copies of the branch below, as `Distribute` does. `LaneOf` is called on the delivering thread, so it mustn't touch unsafe state. |
| `IAsyncDisposable` / `IDisposable` | Releasing resources. The module is disposed when its pipeline finishes, and when the runtime replaces it after it throws. |

## Library books

A verb can add books to the library that `Enumerate` reads from. Put `[LibraryBook("Name")]` on a public static property or field whose type is a list of strings:

```csharp
public static class Shouts
{
    [LibraryBook("Shouts")]
    public static readonly IReadOnlyList<string> Book = ["hey", "oi", "yo"];
}
```

The list is read when a pipeline names the book, not when the plugin loads, so a large book can load itself lazily. A plugin's books can also be named with the plugin's name, as in `"Transit.Stops"`.

A verb can take a book as a parameter by giving it a property of type `Book`.

## Where the code goes

### In the core modules

Put the class in `src/Meep2.Modules`, in the namespace `Meep2.Modules`. Every public class there with a `[Verb]` attribute is registered automatically, since the CLI, daemon and language server all call `AddAssembly(CoreModules.Assembly)`. Put message types in `Messages.cs` or beside the verb, as `Combining.cs` does with `Batched` and `Zipped`.

The project is AOT-compatible and treats warnings as errors.

### In a plugin

A plugin is a class library that references `Meep2.SDK`, and nothing else from Meep unless it needs the core message types. The host provides the SDK, so don't copy it beside the plugin:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Meep2\src\Meep2.SDK\Meep2.SDK.csproj" Private="false" />
  </ItemGroup>
</Project>
```

Every public, non-abstract class with `[Verb]` in the plugin's assembly becomes a verb, and every public message type a name `is` can test. Each plugin loads into a context of its own, so it can use its own versions of other libraries.

A pipeline names the plugin with a `#plugin` line, after `defaults` and `const` and before the modules:

```
#plugin "../MyPlugin/bin/Debug/net11.0/MyPlugin.dll" as Mine

Interval 1m
    Mine.Shout "tick"
```

Its verbs can be used by name alone, or by the plugin's name (`Mine.Shout`) when two plugins share one. Where a plugin uses a name a core verb has, the core verb keeps it. A plugin's name can't be one of its own verbs' names.

While developing, run with `meep run --dev`, which loads unsigned plugins from this machine. To share one:

```bash
dotnet publish -c Release -o publish
```

```bash
meep sign publish --key ~/.ssh/id_ed25519
```

This writes `meep-files.txt` and its signature into the folder. Share it as a GitHub release (users pin it with `#plugin github.com/owner/repo 1.2.0 sha256:…`), a NuGet package (`#plugin nuget:Package.Id 1.2.0`), or a folder. See `docs/Design/Chat/step-6.md` for how trust works.

## Testing

The tests use TUnit. `TestRun.Start` in `tests/Meep2.Tests/Support/MeeplangSupport.cs` loads Meeplang text with the core modules and some test modules, runs it until it halts, and records what reached each `Sink`:

```csharp
[Test]
public async Task Without_an_index_it_steps_through_the_book_in_order_and_wraps_around()
{
    var run = await TestRun.Start("""
        Interval 1ms count=3
            Enumerate "Reindeer"
                Sink
        """);

    var items = run.At("Sink").Cast<BookItem>().ToList();
    await Assert.That(items.Select(i => i.Text)).IsEquivalentTo(["Dasher", "Dancer", "Prancer"]);
}
```

Pass `run: false` to check only what the loader says, as for a parameter's error message:

```csharp
var run = await TestRun.Start("""
    Interval 10ms count=1
        Enumerate "Countrys"
    """, run: false);

await Assert.That(run.Diagnostics).Contains("There's no book called Countrys in the library.");
```

`TestRun.Begin` starts a pipeline that doesn't halt on its own, such as one that listens; dispose the result to stop it. `run.Output` holds what `WriteLine` wrote, and `run.Unhandled` the errors that reached the error policy.

To test a module outside the core, register it on a `ModuleRegistry` with `Add(typeof(MyVerb))`, or `Add("Name", () => new MyModule())` for one made by hand. The plugin tests in `PluginTests.cs` show how a built plugin is signed and loaded.

Worth testing for most verbs:

- what it emits for a typical message, and its parents;
- each parameter's check, as a load error on the right line;
- a `Computed<T>` that can't be worked out, which should become an error, not a replaced module;
- errors arriving from upstream;
- for several inflections, that each message reaches the right one, and the load error when none fits;
- for `IReconfigurable`, that state survives an edit (see `TestRun.BeginLive`).

## Checklist

Before calling a verb done:

- [ ] `[Verb]`, with `Inflection` if the verb has more than one.
- [ ] A public constructor taking the parameters record, or none.
- [ ] Parameters with XML doc comments, defaults, and checks in `init` with messages written for pipeline writers.
- [ ] `[Emits]` for every message type it makes, or `[EmitsInput]`.
- [ ] Expected failures emitted as errors; `EvaluationException` caught.
- [ ] Resources released in `Dispose` or `DisposeAsync`.
- [ ] Tests, including the load errors.
- [ ] For a core verb, an entry in [verbs.md](verbs.md), and its summary table and counts updated.
- [ ] An example in `examples/` if it opens up something new.
