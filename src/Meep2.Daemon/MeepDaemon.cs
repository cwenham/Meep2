using System.Collections.Concurrent;
using System.Reflection;
using Meep2.Daemon.Subscriptions;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Security;

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
/// <item>
/// <c>GET /pipelines</c>, <c>GET /pipelines/{name}</c>: what's running, each module's counts, and how many messages
/// each link of its graph has carried, by the link's key (observe). See <see cref="PipelineDetail"/>.
/// </item>
/// <item>
/// <c>GET /pipelines/{name}/stats?every=1s</c>: those counts as a live stream of server-sent events (observe). A
/// <c>graph</c> event comes between them, straight away, when the pipeline's graph changes.
/// </item>
/// <item>
/// <c>GET /pipelines/{name}/graph</c>: its modules, where each is written, its verb's role, and the links between
/// them with the message types they carry (observe). See <see cref="PipelineGraph"/>.
/// </item>
/// <item><c>GET /pipelines/{name}/modules/{module}/tap?where=…&amp;sample=…</c>: the messages passing a module, as a live stream (observe).</item>
/// <item><c>POST /pipelines/{name}/modules/{module}/inject</c>: sends text, or JSON, into a module (inject).</item>
/// <item><c>POST /pipelines</c>, <c>DELETE /pipelines/{name}</c>, <c>POST /stop</c>: start and stop pipelines, and the daemon (control).</item>
/// <item><c>GET /ws</c>: a WebSocket that carries all of the above, both ways, over one connection (Q108). See <see cref="SocketSession"/>.</item>
/// <item>
/// <c>POST /subscribe</c>, over <c>/ws</c> only: the pipelines, their counts, handling times, bytes and the host's
/// runtime, each at a rate of its own, sending only what changed, changed as it goes (observe). See
/// <see cref="Subscription"/>.
/// </item>
/// </list>
/// <para>
/// A pipeline that ends on its own stays listed for as long as its file's <c>keep-finished</c> says, 24 hours unless
/// it says otherwise, with its final counts and why it stopped (Q111). One that someone stops leaves the list.
/// </para>
/// </remarks>
public sealed class MeepDaemon : IAsyncDisposable, ISubscriptionHost
{
    private readonly Lazy<RuntimeMetrics> _runtime = new(() => new RuntimeMetrics());
    private readonly Lock _signalling = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostInfo? _identity;
    private IDisposable? _meepHosts;
    private Task _pruning = Task.CompletedTask;

    private readonly DaemonOptions _options;
    private readonly TrustStoreSource _trust;
    private readonly ConcurrentDictionary<string, HostedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _logging = new();
    private ControlSocket? _control;
    private int _stopped;

    /// <summary>Who may do what through the API, from the tokens file.</summary>
    internal DaemonAccess Access { get; }

    /// <summary>Makes a daemon. Start it with <see cref="StartAsync"/> or <see cref="RunAsync"/>.</summary>
    public MeepDaemon(DaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Access = new DaemonAccess(options.TokensFile);
        _trust = TrustStoreSource.Watching(TrustFolder.In(options.Plugins.Home), Log);
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

    /// <summary>How many pipelines are listed now, without taking off those that have stayed long enough, for tests.</summary>
    internal int ListedNow => _pipelines.Count;

    /// <summary>The names of the pipelines listed now.</summary>
    internal ICollection<string> Names => _pipelines.Keys;

    /// <summary>The pipelines listed now, without pruning.</summary>
    internal ICollection<HostedPipeline> Hosted => _pipelines.Values;

    /// <summary>Completes when someone asks the daemon to stop, through the API.</summary>
    public Task StopRequested => _stopRequested.Task;

    internal static string Version =>
        typeof(MeepDaemon).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>The daemon's lasting ID, kept in <c>daemon/id</c> in Meep's folder (<see cref="HostIdentity"/>).</summary>
    public string Id => ((ISubscriptionHost)this).Identity.Id;

    HostInfo ISubscriptionHost.Identity =>
        _identity ??= new HostInfo(HostIdentity.IdOf(_options.Plugins.Home), [Environment.MachineName], MachineAddresses.Addresses(), Version, Environment.ProcessId);

    IReadOnlyCollection<HostedPipeline> ISubscriptionHost.Listed => Pipelines;

    RuntimeMetrics ISubscriptionHost.Runtime => _runtime.Value;

    Task ISubscriptionHost.Changed
    {
        get
        {
            lock (_signalling)
                return _changed.Task;
        }
    }

    // A pipeline's graph or state changed: subscriptions look again straight away.
    private void Signal()
    {
        TaskCompletionSource changed;
        lock (_signalling)
        {
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>Opens the control socket.</summary>
    /// <exception cref="DaemonException">Another daemon is using the socket, or its path is too long.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var socket = Path.GetFullPath(SocketPath);
        ControlSocket.Prepare(socket);
        _identity = new HostInfo(HostIdentity.IdOf(_options.Plugins.Home), await MachineAddresses.NamesAsync(cancellationToken).ConfigureAwait(false), MachineAddresses.Addresses(), Version, Environment.ProcessId);
        // Meep hosts the settings name, and those learned before, get origins from the first request (Q213).
        Meep2.Modules.MeepHosts.Configure(_options.MeepHosts);
        // Set, never cleared: it's for the whole process, as meep run's option is, and a daemon without it in the same
        // process (as in tests) shouldn't undo it.
        if (_options.MeepHostPublic)
            Meep2.Modules.MeepHosts.AnswerAnyone = true;
        _meepHosts = Meep2.Modules.MeepHosts.Keep(_options.MeepHostsFile);

        _control = new ControlSocket(Access, new DaemonApi(this).RouteAsync, _stopping.Token);
        await _control.StartAsync(socket, cancellationToken).ConfigureAwait(false);
        Log($"meep daemon {Version} is listening on {socket}");
        // The trust store is read and watched from now, so one that can't be read is reported as the daemon starts.
        _ = _trust.Current;
        _pruning = PruneEveryAsync(_stopping.Token);
        await StartConfiguredAsync(cancellationToken).ConfigureAwait(false);
    }

    // The pipelines its command line and settings file name. A file that doesn't load is logged, not fatal.
    private async Task StartConfiguredAsync(CancellationToken cancellationToken)
    {
        foreach (var start in _options.Start)
        {
            try
            {
                await StartPipelineAsync(start.Path, start.DeveloperMode, start.Watch, cancellationToken).ConfigureAwait(false);
            }
            catch (DaemonException exception)
            {
                Log($"[{Path.GetFileNameWithoutExtension(start.Path)}] {exception.Message}");
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
        await _pruning.ConfigureAwait(false);
        if (_control is not null)
            await _control.DisposeAsync().ConfigureAwait(false);
        TryDelete(SocketPath);
        if (_runtime.IsValueCreated)
            _runtime.Value.Dispose();
        _stopRequested.TrySetResult();
        Log("meep daemon has stopped");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _meepHosts?.Dispose();
        _trust.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// Loads a pipeline file and starts it, returning once it's running. One of the same name that has ended is
    /// replaced.
    /// </summary>
    /// <exception cref="DaemonException">A pipeline of that name is running already, or the file didn't load.</exception>
    public async Task<HostedPipeline> StartPipelineAsync(string path, bool developerMode = false, CancellationToken cancellationToken = default) =>
        await StartPipelineAsync(path, developerMode, watch: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads a pipeline file and starts it, returning once it's running, and if <paramref name="watch"/> is set,
    /// reloads it whenever the file changes (goal 4).
    /// </summary>
    /// <exception cref="DaemonException">A pipeline of that name is running already, or the file didn't load.</exception>
    public async Task<HostedPipeline> StartPipelineAsync(string path, bool developerMode, bool watch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        PipelineAddress address;
        try
        {
            address = PipelineAddress.Parse(path);
        }
        catch (FormatException exception)
        {
            throw new DaemonException(400, exception.Message);
        }
        return await StartPipelineAsync(address, developerMode, watch, text: null, name: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a pipeline from its address (goal 7), or from <paramref name="text"/> sent over the socket, called
    /// <paramref name="name"/>.
    /// </summary>
    /// <exception cref="DaemonException">A pipeline of that name is running already, or it didn't load.</exception>
    public async Task<HostedPipeline> StartPipelineAsync(PipelineAddress address, bool developerMode, bool watch, string? text, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Volatile.Read(ref _stopped) != 0)
            throw new DaemonException(503, "The daemon is stopping.");
        if (text is not null)
            address = new PipelineAddress(PipelineOrigin.Text, name ?? "pipeline");
        if (watch && !address.IsFile)
            throw new DaemonException(400, $"Only a file on this machine can be watched, and {address} isn't one.");
        var hosted = Reserve(name ?? address.Name, address, developerMode);
        var bootstrap = new PipelineBootstrap(hosted, Log, _trust);
        var pipeline = bootstrap.Build(text, _options.Catalog, ((ISubscriptionHost)this).Identity.Id);
        hosted.Bootstrap = pipeline;
        hosted.Finished = Task.Run(() => RunBootstrapAsync(hosted, bootstrap, pipeline), CancellationToken.None);

        if (!await bootstrap.Loaded.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            await hosted.Finished.ConfigureAwait(false);
            throw new DaemonException(400, $"{hosted.Name} didn't load.", bootstrap.Problems);
        }
        foreach (var warning in hosted.Warnings)
            Log($"[{hosted.Name}] {warning}");
        if (watch)
            hosted.Watch(Log);
        Log($"[{hosted.Name}] is running, from {address}{(watch ? ", and reloads when it changes" : "")}");
        return hosted;
    }

    // Lists a new pipeline under name, with the core verbs, replacing one of that name that has ended.
    private HostedPipeline Reserve(string name, PipelineAddress address, bool developerMode)
    {
        var registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly).AddAssembly(RuntimeModules.Assembly);
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, _options.Output));
        var plugins = _options.Plugins with { DeveloperMode = _options.Plugins.DeveloperMode || developerMode };
        var hosted = new HostedPipeline(name, address, registry, plugins, Signal);
        while (!_pipelines.TryAdd(name, hosted))
        {
            if (_pipelines.TryGetValue(name, out var existing) && existing.Ended is null)
                throw new DaemonException(409, $"A pipeline called {name} is {existing.State} already. Stop it first with: meep stop {name}");
            if (existing is not null)
                _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, existing));
        }
        return hosted;
    }

    // Runs the bootstrap until the daemon stops or the pipeline ends, then lists it as ended, or takes it off the list
    // if it never loaded, was stopped, or isn't to be kept.
    private async Task RunBootstrapAsync(HostedPipeline hosted, PipelineBootstrap bootstrap, Pipeline pipeline)
    {
        try
        {
            await pipeline.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            if (bootstrap.End())
            {
                hosted.End();
                if (hosted.StoppedOnRequest || hosted.KeepFinished == TimeSpan.Zero)
                    _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(hosted.Name, hosted));
                Log($"[{hosted.Name}] has {hosted.State}, because {hosted.Reason}");
            }
            else
            {
                _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(hosted.Name, hosted));
            }
        }
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

    /// <summary>The pipeline called <paramref name="name"/>.</summary>
    /// <exception cref="DaemonException">There's none of that name.</exception>
    internal HostedPipeline Find(string name) =>
        _pipelines.TryGetValue(name, out var hosted)
            ? hosted
            : throw new DaemonException(404, $"There's no pipeline called {name}." + (_pipelines.IsEmpty ? " Nothing is running." : $" The daemon has: {string.Join(", ", _pipelines.Keys.Order())}."));

    /// <summary>Takes pipelines off the list once they've been listed for as long as their files ask. Returns whether it took any.</summary>
    internal bool Prune()
    {
        var now = DateTimeOffset.UtcNow;
        var pruned = false;
        foreach (var (name, hosted) in _pipelines)
        {
            if (hosted.Expired(now))
                pruned |= _pipelines.TryRemove(new KeyValuePair<string, HostedPipeline>(name, hosted));
        }
        return pruned;
    }

    // Prunes on a timer too, so ended pipelines go though nobody asks for the list (M1), telling subscriptions.
    private async Task PruneEveryAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.PruneEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                if (Prune())
                    Signal();
            }
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping.
        }
    }

    /// <summary>Writes a line to the daemon's log.</summary>
    internal void Log(string text)
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
