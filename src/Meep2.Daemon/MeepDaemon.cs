using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Meep2.Daemon;

/// <summary>
/// Runs pipelines in the background and answers a control API on a local socket (goal 8, Q40, Q63, Q64): the
/// <c>meep daemon</c> command, and anything else that wants to host pipelines this way.
/// </summary>
/// <remarks>
/// <para>
/// The API is HTTP with JSON, on a Unix domain socket in <c>~/.meep/daemon</c>, a folder only you can open. The
/// daemon also asks the system which user connected, and lets in only the one running it, unless the client shows a
/// token (<see cref="DaemonTokens"/>). Each client gets capabilities: the daemon's user gets all of them, and a token
/// the ones it names. Remote access is through an SSH tunnel to the socket, which arrives as your own user.
/// </para>
/// <para>The API, with the capability each part needs:</para>
/// <list type="bullet">
/// <item><c>GET /</c>: the daemon and what you may do.</item>
/// <item><c>GET /pipelines</c>, <c>GET /pipelines/{name}</c>: what's running, and each module's counts (observe).</item>
/// <item><c>GET /pipelines/{name}/stats?every=1s</c>: those counts as a live stream of server-sent events (observe).</item>
/// <item><c>GET /pipelines/{name}/modules/{module}/tap?where=…&amp;sample=…</c>: the messages passing a module, as a live stream (observe).</item>
/// <item><c>POST /pipelines/{name}/modules/{module}/inject</c>: sends text, or JSON, into a module (inject).</item>
/// <item><c>POST /pipelines</c>, <c>DELETE /pipelines/{name}</c>, <c>POST /stop</c>: start and stop pipelines, and the daemon (control).</item>
/// <item><c>GET /ws</c>: a WebSocket that carries all of the above, both ways, over one connection (Q108). See <see cref="SocketSession"/>.</item>
/// </list>
/// <para>
/// A pipeline that ends on its own stays listed for as long as its file's <c>keep-finished</c> says, 24 hours unless
/// it says otherwise, with its final counts and why it stopped (Q111). One that someone stops leaves the list.
/// </para>
/// </remarks>
public sealed class MeepDaemon : IAsyncDisposable
{
    private const string PeerKey = "meep.peer";

    private readonly DaemonOptions _options;
    private readonly ConcurrentDictionary<string, HostedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _logging = new();
    private WebApplication? _app;
    private DaemonTokens _tokens = DaemonTokens.Load("");
    private DateTime _tokensWritten;
    private int _stopped;

    /// <summary>Makes a daemon. Start it with <see cref="StartAsync"/> or <see cref="RunAsync"/>.</summary>
    public MeepDaemon(DaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The control socket's path.</summary>
    public string SocketPath => _options.Socket;

    /// <summary>The pipelines running now, and those that ended recently.</summary>
    public IReadOnlyCollection<HostedPipeline> Pipelines
    {
        get
        {
            Prune();
            return [.. _pipelines.Values];
        }
    }

    /// <summary>Completes when someone asks the daemon to stop, through the API.</summary>
    public Task StopRequested => _stopRequested.Task;

    private static string Version =>
        typeof(MeepDaemon).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Opens the control socket.</summary>
    /// <exception cref="DaemonException">Another daemon is using the socket, or its path is too long.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var socket = Path.GetFullPath(SocketPath);
        PrepareSocket(socket);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenUnixSocket(socket, listen => listen.Use(next => connection =>
            {
                // Who's connecting, as the system tells it, for HandleAsync to decide what they may do.
                if (connection.Features.Get<IConnectionSocketFeature>()?.Socket is { } peer)
                    connection.Items[PeerKey] = PeerCredentials.UserOf(peer);
                return next(connection);
            }));
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        _app.Run(HandleAsync);
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Log($"meep daemon {Version} is listening on {socket}");

        // The pipelines its command line and settings file name. A file that doesn't load is logged, not fatal.
        foreach (var file in _options.Start)
        {
            try
            {
                await StartPipelineAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DaemonException exception)
            {
                Log($"[{Path.GetFileNameWithoutExtension(file)}] {exception.Message}");
                foreach (var detail in exception.Details)
                    Log($"  {detail}");
            }
        }
    }

    /// <summary>Opens the control socket and runs until <paramref name="cancellationToken"/> is cancelled or a client asks it to stop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C.
        }
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Stops every pipeline, letting their messages finish, then closes the socket.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        foreach (var hosted in _pipelines.Values)
            hosted.Stop();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_pipelines.Values.Select(hosted => hosted.Finished)).ConfigureAwait(false);
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        TryDelete(SocketPath);
        _stopRequested.TrySetResult();
        Log("meep daemon has stopped");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>
    /// Loads a pipeline file and starts it, returning once it's running. One of the same name that has ended is
    /// replaced.
    /// </summary>
    /// <exception cref="DaemonException">A pipeline of that name is running already, or the file didn't load.</exception>
    public async Task<HostedPipeline> StartPipelineAsync(string path, bool developerMode = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Volatile.Read(ref _stopped) != 0)
            throw new DaemonException(503, "The daemon is stopping.");
        path = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var hosted = new HostedPipeline(name, path);
        while (!_pipelines.TryAdd(name, hosted))
        {
            if (_pipelines.TryGetValue(name, out var existing) && existing.Ended is null)
                throw new DaemonException(409, $"A pipeline called {name} is {existing.State} already. Stop it first with: meep stop {name}");
            if (existing is not null)
                _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, existing));
        }

        var problems = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, _options.Output));
        var hostedOptions = new PipelineOptions
        {
            UnhandledError = e => Log(e.AnsweredBy is { } answerer
                ? $"[{name}] error from {e.EmittedBy?.ToString() ?? "the pipeline"}, answered by {answerer}: {e.Error}"
                : $"[{name}] error from {e.EmittedBy?.ToString() ?? "the pipeline"}: {e.Error}"),
            Notice = notice => Log($"[{name}] {notice}"),
        };
        var bootstrapOptions = new PipelineOptions
        {
            UnhandledError = e =>
            {
                if (!ready.Task.IsCompleted)
                {
                    // Loading failed: the client that asked for it is told why.
                    if (e is MeeplangErrorMessage meeplang)
                    {
                        foreach (var diagnostic in meeplang.Diagnostics)
                            problems.Enqueue(diagnostic.ToString());
                    }
                    else
                    {
                        problems.Enqueue(e.Error);
                    }
                    return;
                }
                hosted.Failed(e is PipelineStoppedMessage stopped ? stopped.Reason : e.Error);
                Log($"[{name}] {e.Error}");
            },
            Notice = notice => Log($"[{name}] {notice}"),
        };
        var plugins = _options.Plugins with { DeveloperMode = _options.Plugins.DeveloperMode || developerMode };

        var builder = new PipelineBuilder($"{name} (bootstrap)", bootstrapOptions);
        builder.Chain(
            builder.AddSource(() => new Read(new ReadParameters { Path = path })),
            builder.Add(() => new Parse()),
            builder.Add(() => new Install(registry, new PluginInstaller(plugins))),
            builder.Add(() => new Build(registry, hostedOptions)),
            builder.Add(() => new Host(
                running: built =>
                {
                    hosted.Running(built);
                    if (!ready.TrySetResult(true))
                        Log($"[{name}] is running again");
                },
                restarting: restart =>
                {
                    hosted.Restarting(restart);
                    Log($"[{name}] {restart}");
                })));
        var bootstrap = builder.Build();
        hosted.Bootstrap = bootstrap;
        hosted.Finished = Task.Run(async () =>
        {
            try
            {
                await bootstrap.RunAsync(_stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                await bootstrap.DisposeAsync().ConfigureAwait(false);
                if (!ready.TrySetResult(false))
                {
                    hosted.End();
                    if (hosted.StoppedOnRequest || hosted.KeepFinished == TimeSpan.Zero)
                        _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, hosted));
                    Log($"[{name}] has {hosted.State}, because {hosted.Reason}");
                }
                else
                {
                    _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, hosted));
                }
            }
        }, CancellationToken.None);

        if (!await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            await hosted.Finished.ConfigureAwait(false);
            throw new DaemonException(400, $"{name} didn't load.", [.. problems]);
        }
        foreach (var warning in hosted.Warnings)
            Log($"[{name}] {warning}");
        Log($"[{name}] is running, from {path}");
        return hosted;
    }

    /// <summary>
    /// Stops a pipeline, letting its messages finish, and waits for it. One that has ended already is taken off the
    /// list.
    /// </summary>
    /// <exception cref="DaemonException">There's no pipeline of that name.</exception>
    public async Task StopPipelineAsync(string name)
    {
        var hosted = Find(name);
        if (hosted.Ended is not null)
        {
            _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, hosted));
            return;
        }
        hosted.Stop();
        await hosted.Finished.ConfigureAwait(false);
    }

    // Takes pipelines off the list once they've been listed for as long as their files ask.
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, hosted) in _pipelines)
        {
            if (hosted.Expired(now))
                _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, hosted));
        }
    }

    // Someone else's daemon may be on the socket, or an old one may have left it behind.
    private static void PrepareSocket(string socket)
    {
        var limit = OperatingSystem.IsLinux() ? 107 : 103;
        if (Encoding.UTF8.GetByteCount(socket) > limit)
            throw new DaemonException(0, $"The socket path {socket} is longer than the {limit} characters this system allows. Set MEEP_HOME to a shorter folder.");
        var folder = Path.GetDirectoryName(socket)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            // A folder only you can open, so nobody else can reach the socket.
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!File.Exists(socket))
            return;
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(socket));
        }
        catch (SocketException)
        {
            File.Delete(socket); // left by a daemon that didn't stop cleanly
            return;
        }
        throw new DaemonException(0, $"A meep daemon is running already, on {socket}.");
    }

    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            var capabilities = CapabilitiesOf(context.Request.Headers.Authorization.ToString(), context.Features.Get<IConnectionItemsFeature>()?.Items);
            if (context.Request.Path == "/ws")
            {
                await SocketAsync(context, capabilities).ConfigureAwait(false);
                return;
            }
            var request = await ReadRequestAsync(context, capabilities).ConfigureAwait(false);
            switch (await RouteAsync(request).ConfigureAwait(false))
            {
                case Answer answer:
                    context.Response.StatusCode = answer.Status;
                    if (answer.Body is not null)
                    {
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await WriteAsync(context, answer.Body).ConfigureAwait(false);
                    }
                    break;
                case Events events:
                    await StartEventsAsync(context).ConfigureAwait(false);
                    await foreach (var e in events.Stream(context.RequestAborted).ConfigureAwait(false))
                        await SendEventAsync(context, e).ConfigureAwait(false);
                    break;
            }
        }
        catch (DaemonException exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = exception.Status;
                context.Response.ContentType = "application/json; charset=utf-8";
                await WriteAsync(context, ProblemOf(exception)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away.
        }
    }

    // An HTTP request as the API sees it. Its body is JSON if it says so, and otherwise text.
    private static async Task<ApiRequest> ReadRequestAsync(HttpContext context, Capabilities capabilities)
    {
        var request = context.Request;
        string? text = null;
        JsonElement? json = null;
        if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                json = ParseJson(body);
            else
                text = body;
        }
        return new ApiRequest(
            request.Method,
            request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(),
            request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal),
            text,
            json,
            capabilities,
            context.RequestAborted);
    }

    private static JsonElement ParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new DaemonException(400, $"That isn't JSON: {exception.Message}");
        }
    }

    // The API itself, whether the request came by HTTP or over a WebSocket.
    private async Task<ApiReply> RouteAsync(ApiRequest request)
    {
        var capabilities = request.Capabilities;
        Prune();
        switch (request.Method, request.Parts)
        {
            case ("GET", []):
                Require(capabilities, Capabilities.None);
                return Answer.Json(200, new DaemonInfo(Version, Environment.ProcessId, SocketPath, CapabilityNames.Of(capabilities), [.. _pipelines.Keys.Order()]), DaemonJson.Default.DaemonInfo);
            case ("POST", ["stop"]):
                Require(capabilities, Capabilities.Control);
                _ = Task.Run(StopAsync);
                return new Answer(202, null);
            case ("GET", ["pipelines"]):
                Require(capabilities, Capabilities.Observe);
                return Answer.Json(200, (IReadOnlyList<PipelineSummary>)[.. _pipelines.Values.OrderBy(p => p.Name).Select(p => p.Summary())], DaemonJson.Default.IReadOnlyListPipelineSummary);
            case ("POST", ["pipelines"]):
            {
                Require(capabilities, Capabilities.Control);
                var start = request.Json is { } json
                    ? Deserialize(json, DaemonJson.Default.StartRequest)
                    : throw new DaemonException(400, "Starting a pipeline needs a JSON body, such as {\"path\": \"/home/you/ticker.meep\"}.");
                var hosted = await StartPipelineAsync(start.Path, start.DeveloperMode, request.Aborted).ConfigureAwait(false);
                return Answer.Json(201, hosted.Summary(), DaemonJson.Default.PipelineSummary);
            }
            case ("GET", ["pipelines", var name]):
                Require(capabilities, Capabilities.Observe);
                return Answer.Json(200, Find(name).Detail(), DaemonJson.Default.PipelineDetail);
            case ("DELETE", ["pipelines", var name]):
                Require(capabilities, Capabilities.Control);
                await StopPipelineAsync(name).ConfigureAwait(false);
                return new Answer(204, null);
            case ("GET", ["pipelines", var name, "stats"]):
                Require(capabilities, Capabilities.Observe);
                return Stats(Find(name), request.Query);
            case ("GET", ["pipelines", var name, "modules", var module, "tap"]):
                Require(capabilities, Capabilities.Observe);
                return Tap(Find(name), module, request.Query);
            case ("POST", ["pipelines", var name, "modules", var module, "inject"]):
                Require(capabilities, Capabilities.Inject);
                return Inject(Find(name), module, request);
            default:
                throw new DaemonException(404, $"The daemon has nothing at {request.Method} /{string.Join('/', request.Parts.Select(Uri.EscapeDataString))}.");
        }
    }

    private static T Deserialize<T>(JsonElement json, JsonTypeInfo<T> type)
    {
        try
        {
            return json.Deserialize(type) ?? throw new DaemonException(400, "The request needs a JSON body.");
        }
        catch (JsonException exception)
        {
            throw new DaemonException(400, $"The request's JSON isn't what the daemon expects: {exception.Message}");
        }
    }

    // A token says what its holder may do. Without one, the daemon's own user may do everything, and nobody else
    // anything.
    private Capabilities CapabilitiesOf(string authorization, IDictionary<object, object?>? connection)
    {
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Tokens().Find(authorization["Bearer ".Length..].Trim())?.Capabilities
                ?? throw new DaemonException(401, "That token isn't one this daemon knows.");
        }
        var peer = connection is not null && connection.TryGetValue(PeerKey, out var value) ? value as uint? : null;
        var owner = PeerCredentials.CurrentUser;
        if (owner is null || peer == owner)
            return Capabilities.All;
        throw new DaemonException(403, $"Only the user running the daemon may use it without a token, and you're user {peer?.ToString() ?? "unknown"}.");
    }

    // The tokens file, read again when it changes.
    private DaemonTokens Tokens()
    {
        var written = File.Exists(_options.TokensFile) ? File.GetLastWriteTimeUtc(_options.TokensFile) : default;
        if (written != _tokensWritten)
        {
            _tokens = DaemonTokens.Load(_options.TokensFile);
            _tokensWritten = written;
        }
        return _tokens;
    }

    private static void Require(Capabilities have, Capabilities need)
    {
        if (need != Capabilities.None && !have.HasFlag(need))
            throw new DaemonException(403, $"That needs the {CapabilityNames.Of(need)[0]} capability, and you have {(have == Capabilities.None ? "none" : string.Join(", ", CapabilityNames.Of(have)))}.");
    }

    private HostedPipeline Find(string name) =>
        _pipelines.TryGetValue(name, out var hosted)
            ? hosted
            : throw new DaemonException(404, $"There's no pipeline called {name}." + (_pipelines.IsEmpty ? " Nothing is running." : $" The daemon has: {string.Join(", ", _pipelines.Keys.Order())}."));

    private static Pipeline Running(HostedPipeline hosted) =>
        hosted.Pipeline is { IsRunning: true } pipeline && hosted.Ended is null
            ? pipeline
            : throw new DaemonException(409, $"{hosted.Name} isn't running. It's {hosted.State}" + (hosted.Reason is { } reason ? $", because {reason}." : "."));

    // A module by its number, its name, or a verb or inflection only one module has.
    private static ModuleInfo FindModule(Pipeline pipeline, string id)
    {
        if (int.TryParse(id, out var number) && pipeline.Modules.FirstOrDefault(m => m.Number == number) is { } numbered)
            return numbered;
        var matches = pipeline.Modules.Where(m => m.Name == id).ToList();
        if (matches.Count == 0)
            matches = [.. pipeline.Modules.Where(m => m.IsCalled(id))];
        return matches switch
        {
            [var one] => one,
            [] => throw new DaemonException(404, $"{pipeline.Name} has no module called {id}. Its modules are {string.Join(", ", pipeline.Modules)}."),
            _ => throw new DaemonException(400, $"{pipeline.Name} has more than one {id}: {string.Join(", ", matches)}. Say which by number."),
        };
    }

    // The modules' counts, every so often, until the pipeline stops or the client goes. A restarted pipeline's
    // counts start again from zero, under the same name.
    private static Events Stats(HostedPipeline hosted, IReadOnlyDictionary<string, string> query)
    {
        var every = TimeSpan.FromSeconds(1);
        if (query.GetValueOrDefault("every") is { Length: > 0 } text
            && !(Meep2.Meeplang.Parsing.Durations.TryParse(text, out every) && every >= TimeSpan.FromMilliseconds(50)))
            throw new DaemonException(400, $"every is a duration of at least 50ms, such as 1s or 500ms, not {text}.");
        Running(hosted);

        return new Events(Stream);

        async IAsyncEnumerable<ApiEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var detail = hosted.Detail();
                yield return new ApiEvent("stats", writer => JsonSerializer.Serialize(writer, detail, DaemonJson.Default.PipelineDetail));
                if (hosted.Ended is not null)
                    break;
                await Task.WhenAny(hosted.Finished, Task.Delay(every, cancellationToken)).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
                yield return End($"{hosted.Name} has {hosted.State}" + (hosted.Reason is { } reason ? $", because {reason}." : "."));
        }
    }

    // Each message passing a module, as JSON, until the pipeline stops or the client goes.
    private static Events Tap(HostedPipeline hosted, string module, IReadOnlyDictionary<string, string> query)
    {
        var pipeline = Running(hosted);
        var info = FindModule(pipeline, module);
        MessageCondition? where = null;
        if (query.GetValueOrDefault("where") is { Length: > 0 } condition)
        {
            try
            {
                where = MessageCondition.Parse(condition, hosted.Registry);
            }
            catch (FormatException exception)
            {
                throw new DaemonException(400, $"The condition {condition} isn't one Meeplang can read: {exception.Message}");
            }
        }
        var sample = 1.0;
        if (query.GetValueOrDefault("sample") is { Length: > 0 } rate
            && !(double.TryParse(rate, System.Globalization.CultureInfo.InvariantCulture, out sample) && sample is > 0 and <= 1))
            throw new DaemonException(400, $"sample is a share of messages, more than 0 and at most 1, such as 0.1, not {rate}.");

        return new Events(Stream);

        async IAsyncEnumerable<ApiEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DebugTap? tap = null;
            try
            {
                tap = pipeline.Tap(info, new DebugTapOptions { Where = where is null ? null : where.Matches, Sample = sample });
            }
            catch (InvalidOperationException)
            {
                // It finished in the meantime.
            }
            if (tap is not null)
            {
                using (tap)
                {
                    await foreach (var message in tap.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var dropped = tap.Dropped;
                        yield return new ApiEvent("message", writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("dropped", dropped);
                            writer.WritePropertyName("message");
                            MessageJson.Write(writer, message);
                            writer.WriteEndObject();
                        });
                    }
                }
            }
            if (!cancellationToken.IsCancellationRequested)
                yield return End($"{pipeline.Name} has stopped.");
        }
    }

    // Text, or JSON, sent into a module as a test.
    private static Answer Inject(HostedPipeline hosted, string module, ApiRequest request)
    {
        var pipeline = Running(hosted);
        var info = FindModule(pipeline, module);
        Message message = request.Json is { } json ? new JsonMessage(json) : new TextMessage(request.Text ?? "");
        try
        {
            pipeline.Inject(info, message);
        }
        catch (InvalidOperationException exception)
        {
            throw new DaemonException(409, exception.Message);
        }
        return Answer.Json(202, new Injected(message.Id, message.GetType().Name, info.ToString()), DaemonJson.Default.Injected);
    }

    private static ApiEvent End(string reason) => new("end", writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("reason", reason);
        writer.WriteEndObject();
    });

    private static Action<Utf8JsonWriter> ProblemOf(DaemonException exception) =>
        writer => JsonSerializer.Serialize(writer, new Problem(exception.Message, exception.Details.Count > 0 ? exception.Details : null), DaemonJson.Default.Problem);

    // A WebSocket that carries the API both ways (Q108).
    private async Task SocketAsync(HttpContext context, Capabilities capabilities)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw new DaemonException(400, "/ws is a WebSocket. Connect to it with a WebSocket client, or use the HTTP API.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        await new SocketSession(socket, capabilities, RouteAsync, ProblemOf).RunAsync(ending.Token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext context, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            body(writer);
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task StartEventsAsync(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // One server-sent event, its data a line of JSON.
    private static async Task SendEventAsync(HttpContext context, ApiEvent e)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(Encoding.UTF8.GetBytes($"event: {e.Name}\ndata: "));
        using (var writer = new Utf8JsonWriter(buffer))
            e.Data(writer);
        buffer.Write("\n\n"u8);
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private void Log(string text)
    {
        lock (_logging)
            _options.Log.WriteLine(text);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for next time; the next daemon removes a socket nothing answers on.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
