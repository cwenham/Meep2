using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Tests.Support;

/// <summary>Parses Meeplang text for tests.</summary>
public static class Script
{
    public static PipelineSyntax Parse(string text) => MeeplangParser.Parse(new SourceFile("test.meep", text));

    public static string Diagnostics(PipelineSyntax syntax) => string.Join("\n", syntax.Diagnostics);
}

/// <summary>Records every message it receives, labelled with the receiving module's name or verb.</summary>
public sealed class TestSink(ConcurrentQueue<(string Module, Message Message)> received) : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        received.Enqueue((context.Module.Name ?? context.Module.Verb, message));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Throws for every message.</summary>
public sealed class Boom : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");
}

/// <summary>A source registered through its [Verb] attribute: emits a Number for each value from 1 to its count.</summary>
[Verb("Numbers")]
[Emits(typeof(Number))]
public sealed class NumberSource(NumberSourceParameters parameters) : ASource
{
    protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= parameters.To; i++)
            context.Emit(new Number(i));
        return Task.CompletedTask;
    }
}

/// <summary>One of two inflections of Echo that take the same messages, for testing how a verb chooses (Q20).</summary>
[Verb("Echo", Inflection = "Loud")]
[EmitsInput]
public sealed class LoudEcho : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The other inflection of Echo.</summary>
[Verb("Echo", Inflection = "Quiet")]
[EmitsInput]
public sealed class QuietEcho : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

public sealed record NumberSourceParameters
{
    [Positional]
    public required int To { get; init; }
}

/// <summary>Counts the messages it has received and emits a Number of that count times its step: state a live change should keep.</summary>
[Verb("Tally")]
[Emits(typeof(Number))]
public sealed class Tally(TallyParameters parameters) : AInflection<Message>
{
    private int _seen;

    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(new Number(++_seen * parameters.Step));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Tally, but it takes new parameters while it runs and keeps counting.</summary>
[Verb("KeepingTally")]
[Emits(typeof(Number))]
public sealed class KeepingTally(TallyParameters parameters) : AInflection<Message>, IReconfigurable<TallyParameters>
{
    private TallyParameters _parameters = parameters;
    private int _seen;

    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(new Number(++_seen * _parameters.Step));
        return ValueTask.CompletedTask;
    }

    public bool ParametersChanged(TallyParameters old, TallyParameters next)
    {
        _parameters = next;
        return true;
    }
}

public sealed record TallyParameters
{
    public int Step { get; init; } = 1;
}

/// <summary>Loads Meeplang text with the core modules plus Sink, Boom and Numbers, and runs it.</summary>
public sealed class TestRun
{
    private TestRun(LoadResult result, ConcurrentQueue<(string, Message)> received, ConcurrentQueue<ErrorMessage> unhandled, ConcurrentQueue<string> notices, StringWriter output)
    {
        Result = result;
        Received = received;
        Unhandled = unhandled;
        Notices = notices;
        Output = output;
    }

    public LoadResult Result { get; }

    public ConcurrentQueue<(string Module, Message Message)> Received { get; }

    public ConcurrentQueue<ErrorMessage> Unhandled { get; }

    /// <summary>Notices about the pipeline, such as a branch the error policy stopped.</summary>
    public ConcurrentQueue<string> Notices { get; }

    /// <summary>What WriteLine wrote.</summary>
    public StringWriter Output { get; }

    public string Diagnostics => string.Join("\n", Result.Diagnostics);

    public IReadOnlyList<Message> At(string module) => [.. Received.Where(r => r.Module == module).Select(r => r.Message)];

    /// <summary>Loads the text and, if it loads, runs it until it halts.</summary>
    public static async Task<TestRun> Start(string text, bool run = true)
    {
        var testRun = Load(text);
        if (run && testRun.Result.Pipeline is { } pipeline)
        {
            await using (pipeline)
                await Wait.For(pipeline.RunAsync());
        }
        return testRun;
    }

    /// <summary>
    /// Loads the text and starts running it, for pipelines that don't halt on their own, such as ones that listen.
    /// Dispose the result to stop the pipeline and wait for it to finish.
    /// </summary>
    public static Running Begin(string text)
    {
        var testRun = Load(text);
        var pipeline = testRun.Result.Pipeline ?? throw new InvalidOperationException($"The pipeline didn't load:\n{testRun.Diagnostics}");
        return new Running(testRun, pipeline, pipeline.RunAsync());
    }

    private static TestRun Load(string text)
    {
        var testRun = new TestRun(new LoadResult(null, []), new(), new(), new(), new SharedText());
        var result = new PipelineLoader(testRun.Registry()).Load(Script.Parse(text), testRun.Options);
        return new TestRun(result, testRun.Received, testRun.Unhandled, testRun.Notices, testRun.Output);
    }

    /// <summary>
    /// Loads the text as a pipeline that can be changed while it runs, and starts it. Dispose the result to stop it.
    /// </summary>
    public static Live BeginLive(string text)
    {
        var testRun = new TestRun(new LoadResult(null, []), new(), new(), new(), new SharedText());
        var (live, diagnostics) = Meep2.Meeplang.Editing.LivePipeline.Load(Script.Parse(text), testRun.Registry(), testRun.Options);
        if (live is null)
            throw new InvalidOperationException($"The pipeline didn't load:\n{string.Join("\n", diagnostics)}");
        return new Live(testRun, live, live.Pipeline.RunAsync());
    }

    private PipelineOptions Options => new() { UnhandledError = Unhandled.Enqueue, Notice = Notices.Enqueue };

    // The core modules, plus test modules that record what they receive in this run.
    private ModuleRegistry Registry()
    {
        var output = TextWriter.Synchronized(Output);
        return new ModuleRegistry()
            .AddAssembly(CoreModules.Assembly)
            .Add("Sink", () => new TestSink(Received))
            .Add("Boom", () => new Boom())
            .Add(typeof(NumberSource))
            .Add(typeof(LoudEcho))
            .Add(typeof(QuietEcho))
            .Add(typeof(Tally))
            .Add(typeof(KeepingTally))
            .AddMessageType(typeof(Word))
            .AddMessageType(typeof(Number))
            .Add<WriteLine, WriteLineParameters>("WriteLine", p => new WriteLine(p, output));
    }

    /// <summary>A pipeline that can be changed, running in the background.</summary>
    public sealed class Live(TestRun run, Meep2.Meeplang.Editing.LivePipeline pipeline, Task running) : IAsyncDisposable
    {
        public TestRun Run { get; } = run;

        public Meep2.Meeplang.Editing.LivePipeline Pipeline { get; } = pipeline;

        public Task Running { get; } = running;

        /// <summary>The numbers Sink has received, in order.</summary>
        public IReadOnlyList<int> Numbers => [.. Run.At("Sink").OfType<Number>().Select(n => n.Value)];

        public async ValueTask DisposeAsync()
        {
            Pipeline.Pipeline.Stop();
            await Wait.For(Running);
            await Pipeline.Pipeline.DisposeAsync();
        }
    }

    /// <summary>A pipeline running in the background.</summary>
    public sealed class Running(TestRun run, Pipeline pipeline, Task running) : IAsyncDisposable
    {
        public TestRun Run { get; } = run;

        public Pipeline Pipeline { get; } = pipeline;

        public async ValueTask DisposeAsync()
        {
            Pipeline.Stop();
            await Wait.For(running);
            await Pipeline.DisposeAsync();
        }
    }
}

/// <summary>A small HTTP server on a free local port, for testing Fetch.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();

    public TestServer(Func<HttpListenerContext, Task> handle)
    {
        Url = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = ServeAsync(handle);
    }

    public string Url { get; }

    public ValueTask DisposeAsync()
    {
        // Close alone: Stop then Close removes the listener twice, and .NET's managed HttpListener binds the port
        // again to remove it the second time, which fails if another test has just taken that port.
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    public static async Task Reply(HttpListenerContext context, int status, string body, string contentType = "application/json")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeAsync(Func<HttpListenerContext, Task> handle)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await handle(context);
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
                {
                    // The client went away, as a timeout test means it to.
                }
            });
        }
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
