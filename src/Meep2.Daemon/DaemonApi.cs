using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meep2.Daemon.Subscriptions;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>
/// The daemon's API, whether a request came by HTTP or over a WebSocket: each route, the capability it needs, and its
/// handler. See <see cref="MeepDaemon"/> for the routes.
/// </summary>
/// <param name="daemon">The daemon whose pipelines it reports on and changes.</param>
internal sealed class DaemonApi(MeepDaemon daemon)
{
    /// <summary>
    /// The longest pipeline text a client may send to start (Q236): 4 MiB, as for a pipeline from a URL, counted in
    /// UTF-8 once it's out of its JSON.
    /// </summary>
    internal const int MaxPipelineText = PipelineSources.MaxDownload;

    /// <summary>Answers a request, once its client is found to have the capability its route needs.</summary>
    /// <exception cref="DaemonException">There's no such route, the client may not use it, or it failed.</exception>
    public async Task<ApiReply> RouteAsync(ApiRequest request)
    {
        daemon.Prune();
        return (request.Method, request.Parts) switch
        {
            ("GET", []) => Allowed(request, Capabilities.None, () => About(request.Capabilities)),
            ("POST", ["subscribe"]) => Allowed(request, Capabilities.Observe, () => Subscribe(request)),
            ("POST", ["stop"]) => Allowed(request, Capabilities.Control, Stop),
            ("GET", ["pipelines"]) => Allowed(request, Capabilities.Observe, List),
            ("POST", ["pipelines"]) => await AllowedAsync(request, Capabilities.Control, () => StartAsync(request)).ConfigureAwait(false),
            ("GET", ["pipelines", var name]) => Allowed(request, Capabilities.Observe, () => Answer.Json(200, daemon.Find(name).Detail(), DaemonJson.Default.PipelineDetail)),
            ("DELETE", ["pipelines", var name]) => await AllowedAsync(request, Capabilities.Control, () => StopAsync(name)).ConfigureAwait(false),
            ("GET", ["pipelines", var name, "graph"]) => Allowed(request, Capabilities.Observe, () => Answer.Json(200, daemon.Find(name).Graph(), DaemonJson.Default.PipelineGraph)),
            ("GET", ["pipelines", var name, "stats"]) => Allowed(request, Capabilities.Observe, () => Stats(daemon.Find(name), request.Query)),
            ("GET", ["pipelines", var name, "modules", var module, "tap"]) => Allowed(request, Capabilities.Observe, () => Tap(daemon.Find(name), module, request.Query)),
            ("POST", ["pipelines", var name, "modules", var module, "inject"]) => Allowed(request, Capabilities.Inject, () => Inject(daemon.Find(name), module, request)),
            ("GET", ["pipelines", var name, "source"]) => Allowed(request, Capabilities.Observe, () => Source(name)),
            ("POST", ["pipelines", var name, "reload"]) => await AllowedAsync(request, Capabilities.Control, () => ReloadAsync(name, request)).ConfigureAwait(false),
            ("POST", ["pipelines", var name, "edits"]) => await AllowedAsync(request, Capabilities.Control, () => EditAsync(daemon.Find(name), request)).ConfigureAwait(false),
            _ => throw new DaemonException(404, $"The daemon has nothing at {request.Method} /{string.Join('/', request.Parts.Select(Uri.EscapeDataString))}."),
        };
    }

    /// <summary>
    /// Where the running pipelines' Fetches and Sends send, as their addresses say: those that reach the network, by
    /// an address fixed when the pipeline started or worked out for each message.
    /// </summary>
    internal static List<SendInfo> Sending(IEnumerable<HostedPipeline> pipelines)
    {
        var sending = new List<SendInfo>();
        foreach (var hosted in pipelines.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (hosted.Ended is not null || hosted.Pipeline is not { } pipeline || hosted.Live is not { } live)
                continue;
            var keys = live.ModuleKeys();
            foreach (var to in pipeline.GetSending())
            {
                if (SendInfoOf(hosted.Name, keys, to) is { } send)
                    sending.Add(send);
            }
        }
        return sending;
    }

    // A Fetch's or Send's address, if it's somewhere on the network.
    private static SendInfo? SendInfoOf(string pipeline, IReadOnlyDictionary<ModuleInfo, string> keys, SendingTo to)
    {
        var endpoint = to.Computed ? Peers.EndpointOfTemplate(to.Address) : Peers.EndpointOf(to.Address);
        if (!to.Computed && endpoint is null)
            return null; // a file, or a Listen's name, rather than somewhere on the network
        if (to.Computed && endpoint is null && to.Address.TrimStart('"') is var written
            && (written.StartsWith('/') || written.StartsWith('~') || written.StartsWith('.') || written.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
            return null;
        return new SendInfo(pipeline, keys.GetValueOrDefault(to.Module) ?? "", to.Module.ToString(), to.Address)
        {
            Computed = to.Computed,
            Endpoint = endpoint,
        };
    }

    private static ApiReply Allowed(ApiRequest request, Capabilities need, Func<ApiReply> handler)
    {
        DaemonAccess.Require(request.Capabilities, need);
        return handler();
    }

    private static async Task<ApiReply> AllowedAsync<T>(ApiRequest request, Capabilities need, Func<Task<T>> handler)
        where T : ApiReply
    {
        DaemonAccess.Require(request.Capabilities, need);
        return await handler().ConfigureAwait(false);
    }

    // GET /: the daemon and what the client may do.
    private Answer About(Capabilities capabilities)
    {
        var identity = ((ISubscriptionHost)daemon).Identity;
        return Answer.Json(200, new DaemonInfo(MeepDaemon.Version, Environment.ProcessId, daemon.SocketPath, CapabilityNames.Of(capabilities), [.. daemon.Names.Order()])
        {
            Id = identity.Id,
            Names = identity.Names,
            Addresses = identity.Addresses,
            Listening = Listening(),
            Sending = Sending(daemon.Hosted),
            Clock = HostIdentity.Microseconds(DateTimeOffset.UtcNow),
        }, DaemonJson.Default.DaemonInfo);
    }

    private SubscribeReply Subscribe(ApiRequest request) => new(new Subscription(SubscriptionPlan.Parse(request.Json), daemon));

    private Answer Stop()
    {
        _ = Task.Run(daemon.StopAsync);
        return new Answer(202, null);
    }

    private Answer List() =>
        Answer.Json(200, (IReadOnlyList<PipelineSummary>)[.. daemon.Hosted.OrderBy(p => p.Name).Select(p => p.Summary())], DaemonJson.Default.IReadOnlyListPipelineSummary);

    // POST /pipelines: starts a pipeline from its address, or from the text sent.
    private async Task<Answer> StartAsync(ApiRequest request)
    {
        var start = request.Json is { } json
            ? Deserialize(json, DaemonJson.Default.StartRequest)
            : throw new DaemonException(400, "Starting a pipeline needs a JSON body, such as {\"path\": \"/home/you/ticker.meep\"}.");
        if (start.Text is { } text && Encoding.UTF8.GetByteCount(text) > MaxPipelineText)
            throw new DaemonException(413, $"A pipeline's text can be at most {MaxPipelineText / (1024 * 1024)} MiB, as one from a URL can.");
        var hosted = start.Text is not null
            ? await daemon.StartPipelineAsync(new PipelineAddress(PipelineOrigin.Text, start.Name ?? "pipeline"), start.DeveloperMode, watch: false, start.Text, start.Name, request.Aborted).ConfigureAwait(false)
            : await daemon.StartPipelineAsync(start.Path, start.DeveloperMode, start.Watch, request.Aborted).ConfigureAwait(false);
        return Answer.Json(201, hosted.Summary(), DaemonJson.Default.PipelineSummary);
    }

    private async Task<Answer> StopAsync(string name)
    {
        await daemon.StopPipelineAsync(name).ConfigureAwait(false);
        return new Answer(204, null);
    }

    private Answer Source(string name)
    {
        var hosted = daemon.Find(name);
        var text = hosted.SourceText ?? throw new DaemonException(409, $"{name} isn't running yet."); // one that has ended still shows what it ran
        return Answer.Json(200, new SourceText(hosted.Path, text, hosted.Edited), DaemonJson.Default.SourceText);
    }

    private async Task<Answer> ReloadAsync(string name, ApiRequest request)
    {
        var hosted = daemon.Find(name);
        LiveOf(hosted);
        var change = await hosted.ReloadAsync(request.Query.GetValueOrDefault("force") is "true" or "1", request.Aborted).ConfigureAwait(false);
        if (!change.Applied)
            throw new DaemonException(400, $"{name}'s file doesn't load, so it runs as it was.", [.. change.Diagnostics.Where(d => d.IsError).Select(d => d.ToString())]);
        daemon.Log($"[{name}] reloaded: {HostedPipeline.Summary(change.Report!)}");
        return Answer.Json(200, Reloading.Describe(change), DaemonJson.Default.ChangeResult);
    }

    // Where the running pipelines' Listens are bound now.
    private List<ListenInfo> Listening()
    {
        var listening = new List<ListenInfo>();
        foreach (var hosted in daemon.Hosted.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (hosted.Ended is not null || hosted.Pipeline is not { } pipeline || hosted.Live is not { } live)
                continue;
            var keys = live.ModuleKeys();
            foreach (var at in pipeline.GetListening())
                listening.Add(new ListenInfo(hosted.Name, keys.GetValueOrDefault(at.Module) ?? "", at.Module.ToString(), at.Address));
        }
        return listening;
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

    private static Pipeline Running(HostedPipeline hosted) =>
        hosted.Pipeline is { IsRunning: true } pipeline && hosted.Ended is null
            ? pipeline
            : throw new DaemonException(409, $"{hosted.Name} isn't running. It's {hosted.State}" + (hosted.Reason is { } reason ? $", because {reason}." : "."));

    private static LivePipeline LiveOf(HostedPipeline hosted) =>
        hosted.Ended is null && hosted.Live is { } live
            ? live
            : throw new DaemonException(409, $"{hosted.Name} isn't running" + (hosted.Ended is null ? " yet." : $". It's {hosted.State}."));

    // Edits the running pipeline's text, changes the pipeline to match, and commits the text to pending (Q32).
    private async Task<Answer> EditAsync(HostedPipeline hosted, ApiRequest request)
    {
        var edit = EditRequestOf(request);
        var live = LiveOf(hosted);
        var edits = edit.Edits!.Select(Reloading.EditOf).ToList();
        var next = await EditedAsync(hosted, live, edits, request.Aborted).ConfigureAwait(false);
        var change = await Reloading.ApplyAsync(live, next, hosted.BaseRegistry, hosted.Plugins, request.Aborted).ConfigureAwait(false);
        if (!change.Applied)
            throw new DaemonException(400, $"After those edits {hosted.Name} wouldn't load, so nothing changed.", [.. change.Diagnostics.Where(d => d.IsError).Select(d => d.ToString())]);
        daemon.Log($"[{hosted.Name}] edited: {string.Join("; ", edits)}. {HostedPipeline.Summary(change.Report!)}");
        hosted.GraphChanged("edit");

        var result = Reloading.Describe(change) with { Text = next.Source.Text };
        result = await SaveAsync(hosted, edit, next.Source.Text, [.. edits.Select(e => e.ToString())], result, request.Aborted).ConfigureAwait(false);
        return Answer.Json(200, result, DaemonJson.Default.ChangeResult);
    }

    private static EditRequest EditRequestOf(ApiRequest request)
    {
        var edit = request.Json is { } json
            ? Deserialize(json, DaemonJson.Default.EditRequest)
            : throw new DaemonException(400, "Editing needs a JSON body, such as {\"edits\": [{\"module\": \"Interval\", \"kind\": \"set\", \"name\": \"count\", \"value\": \"5\"}]}.");
        if (edit.Edits is not { Count: > 0 })
            throw new DaemonException(400, "There are no edits to make.");
        return edit;
    }

    // The running pipeline's text with the edits made, by keys if any edit names a module by its key.
    private static async Task<PipelineSyntax> EditedAsync(HostedPipeline hosted, LivePipeline live, List<SourceEdit> edits, CancellationToken cancellationToken)
    {
        try
        {
            return edits.Any(e => e.Kind < EditKind.SetDefault && DraftEditor.NamesByKey(e.Module))
                ? await EditByKeyAsync(hosted, live, edits, cancellationToken).ConfigureAwait(false)
                : SourceEditor.Apply(live.Syntax, edits);
        }
        catch (EditException exception)
        {
            throw new DaemonException(400, exception.Message);
        }
        catch (DraftEditException exception)
        {
            throw new DaemonException(400, exception.Message);
        }
    }

    // Saves an edit's text to the file, a branch or nowhere, as the request says and as where the pipeline came from
    // allows, and says where in the result.
    private async Task<ChangeResult> SaveAsync(HostedPipeline hosted, EditRequest edit, string text, List<string> described, ChangeResult result, CancellationToken cancellationToken)
    {
        if (!edit.Save)
        {
            hosted.EditedApart(described, commit: null);
            return result;
        }
        if (!hosted.Address.IsFile)
        {
            hosted.EditedApart(described, commit: null);
            return result with { NotSaved = $"{hosted.Name} came from {hosted.Path}, not a file on this machine, so there's nowhere to save the edit." };
        }
        if (Meep2.Git.PendingBranch.RepositoryOf(hosted.Path) is null)
        {
            // Not in a repository: the edit goes into the file itself (Q124), so a reload keeps it.
            await File.WriteAllTextAsync(hosted.Path, text, cancellationToken).ConfigureAwait(false);
            hosted.EditedInFile();
            daemon.Log($"[{hosted.Name}] wrote the edit to {hosted.Path}");
            return result with { WrittenTo = hosted.Path };
        }
        return Commit(hosted, edit, text, described, result);
    }

    private ChangeResult Commit(HostedPipeline hosted, EditRequest edit, string text, List<string> described, ChangeResult result)
    {
        var message = edit.Message ?? $"Edit {hosted.Name} while it runs\n\n{string.Join("\n", described.Select(e => "- " + char.ToUpperInvariant(e[0]) + e[1..]))}";
        try
        {
            var commit = Meep2.Git.PendingBranch.Commit(hosted.Path, text, message, edit.Branch ?? Meep2.Git.PendingBranch.Default);
            hosted.EditedApart(described, commit.Sha);
            daemon.Log($"[{hosted.Name}] committed to {commit.Branch} as {commit.Sha[..7]}{(commit.Unsigned is null ? ", signed" : $", unsigned: {commit.Unsigned}")}");
            return result with { Saved = new SavedCommit(commit.Branch, commit.Sha, commit.Repository, commit.Path) { Unsigned = commit.Unsigned } };
        }
        catch (Meep2.Git.PendingBranchException exception)
        {
            hosted.EditedApart(described, commit: null);
            return result with { NotSaved = exception.Message };
        }
    }

    // Makes edits that name modules by their keys in the pipeline's graph, or as +N, the module an earlier edit added
    // (Q199), as meep lsp makes them: each key names the module that has it after the edits before it, keyed as the
    // change will key it, from the running pipeline's keys.
    private static async Task<PipelineSyntax> EditByKeyAsync(HostedPipeline hosted, LivePipeline live, IReadOnlyList<SourceEdit> edits, CancellationToken cancellationToken)
    {
        var running = live.Shape().Modules.ToDictionary(m => m.Info.Number);
        var draft = new PipelineDraft();
        draft.SetBase(live.Syntax, live.Registry, (number, verb) => running.TryGetValue(number, out var module) && module.Info.Verb == verb ? module.Key : null)
            .ValueOrThrow(problem => new DaemonException(500, problem));
        draft.Update(live.Syntax, live.Registry);

        // Texts with other #plugin lines than the running one's have their plugins installed, as the change will.
        using var plugins = new PluginRegistries(new PluginInstaller(hosted.Plugins), new PluginCatalog());
        var path = live.Syntax.Source.Path;
        var made = await DraftEditor.ApplyAsync(
            draft,
            live.Syntax,
            edits,
            text => Meep2.Meeplang.Parsing.MeeplangParser.Parse(new SourceFile(path, text)),
            plugins.Reusing(live.Syntax, live.Registry, hosted.BaseRegistry),
            cancellationToken).ConfigureAwait(false);
        return made.Syntax;
    }

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
    // counts start again from zero, under the same name. When its graph changes, a graph event says so straight away,
    // followed by its counts.
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
            var told = hosted.GraphNotice.Serial;
            while (!cancellationToken.IsCancellationRequested)
            {
                var noticed = hosted.GraphNoticed;
                if (hosted.GraphNotice is { Notice: { } notice, Serial: var serial } && serial != told)
                {
                    told = serial;
                    yield return new ApiEvent("graph", writer => JsonSerializer.Serialize(writer, notice, DaemonJson.Default.GraphNotice));
                }
                var detail = hosted.Detail();
                yield return new ApiEvent("stats", writer => JsonSerializer.Serialize(writer, detail, DaemonJson.Default.PipelineDetail));
                if (hosted.Ended is not null)
                    break;
                await Task.WhenAny(hosted.Finished, Task.Delay(every, cancellationToken), noticed).ConfigureAwait(false);
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
        var where = WhereOf(query, hosted);
        var options = new DebugTapOptions { Where = where is null ? null : where.Matches, Sample = SampleOf(query) };

        return new Events(Stream);

        async IAsyncEnumerable<ApiEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            DebugTap? tap = null;
            try
            {
                tap = pipeline.Tap(info, options);
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
                        yield return Tapped(message, tap.Dropped);
                }
            }
            if (!cancellationToken.IsCancellationRequested)
                yield return End($"{pipeline.Name} has stopped.");
        }
    }

    // A tap's where=, the condition a message has to meet to be shown.
    private static MessageCondition? WhereOf(IReadOnlyDictionary<string, string> query, HostedPipeline hosted)
    {
        if (query.GetValueOrDefault("where") is not { Length: > 0 } condition)
            return null;
        try
        {
            return MessageCondition.Parse(condition, hosted.Registry);
        }
        catch (FormatException exception)
        {
            throw new DaemonException(400, $"The condition {condition} isn't one Meeplang can read: {exception.Message}");
        }
    }

    // A tap's sample=, the share of messages shown.
    private static double SampleOf(IReadOnlyDictionary<string, string> query)
    {
        var sample = 1.0;
        if (query.GetValueOrDefault("sample") is { Length: > 0 } rate
            && !(double.TryParse(rate, System.Globalization.CultureInfo.InvariantCulture, out sample) && sample is > 0 and <= 1))
            throw new DaemonException(400, $"sample is a share of messages, more than 0 and at most 1, such as 0.1, not {rate}.");
        return sample;
    }

    private static ApiEvent Tapped(Message message, long dropped) => new("message", writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("dropped", dropped);
        writer.WritePropertyName("message");
        MessageJson.Write(writer, message);
        writer.WriteEndObject();
    });

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
}
