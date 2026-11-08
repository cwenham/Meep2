using Meep2.Runtime.Internal;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>
/// Puts a pipeline together from C#: add modules, connect them, then <see cref="Build"/>.
/// The Meeplang loader builds pipelines the same way.
/// </summary>
/// <remarks>
/// Modules are added as factories because the runtime makes a fresh module whenever one throws.
/// The factories are called when the pipeline starts running.
/// </remarks>
public sealed class PipelineBuilder
{
    private readonly List<NodeSpec> _specs = [];
    private readonly List<(ModuleInfo From, ModuleInfo To)> _connections = [];

    /// <summary>Starts an empty pipeline.</summary>
    public PipelineBuilder(string name = "Pipeline", PipelineOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Options = options ?? PipelineOptions.Default;
    }

    /// <summary>The pipeline's name.</summary>
    public string Name { get; }

    /// <summary>Settings for the whole pipeline.</summary>
    public PipelineOptions Options { get; }

    /// <summary>Adds a source (fountainhead).</summary>
    /// <param name="create">Makes the source.</param>
    /// <param name="name">A name other modules and <c>fam</c> can refer to it by, like Meeplang's <c>as Name</c>.</param>
    public ModuleInfo AddSource<TSource>(Func<TSource> create, string? name = null)
        where TSource : ASource
    {
        ArgumentNullException.ThrowIfNull(create);
        return AddSpec(typeof(TSource), () => create(), name, NodeOptions.Default, isSource: true);
    }

    /// <summary>Adds a module that handles messages.</summary>
    /// <param name="create">Makes the module. It's called again whenever the module needs replacing.</param>
    /// <param name="name">A name other modules and <c>fam</c> can refer to it by, like Meeplang's <c>as Name</c>.</param>
    /// <param name="options">How the runtime treats the module, like Meeplang's <c>@</c> modifiers.</param>
    public ModuleInfo Add<TModule>(Func<TModule> create, string? name = null, NodeOptions? options = null)
        where TModule : AInflection
    {
        ArgumentNullException.ThrowIfNull(create);
        return AddSpec(typeof(TModule), () => create(), name, options ?? NodeOptions.Default, isSource: false);
    }

    /// <summary>Sends everything <paramref name="from"/> emits to <paramref name="to"/>.</summary>
    /// <remarks>A module connected to several modules feeds each of them (a branch). Several modules connected to one
    /// module all feed it (a merge).</remarks>
    public PipelineBuilder Connect(ModuleInfo from, ModuleInfo to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        Find(from, nameof(from));
        if (Find(to, nameof(to)).IsSource)
            throw new ArgumentException($"{to} is a source, so nothing can feed it.", nameof(to));
        _connections.Add((from, to));
        return this;
    }

    /// <summary>Connects each module to the next.</summary>
    public PipelineBuilder Chain(params ReadOnlySpan<ModuleInfo> modules)
    {
        for (var i = 1; i < modules.Length; i++)
            Connect(modules[i - 1], modules[i]);
        return this;
    }

    /// <summary>Makes a pipeline that's ready to run. The builder can be used again afterwards.</summary>
    public Pipeline Build() => new(Name, Options, [.. _specs], [.. _connections]);

    private ModuleInfo AddSpec(Type type, Func<AModule> create, string? name, NodeOptions options, bool isSource)
    {
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_specs.Exists(spec => spec.Info.Name == name))
                throw new ArgumentException($"This pipeline already has a module called {name}.", nameof(name));
        }

        var info = new ModuleInfo(_specs.Count + 1, VerbOf(type), name);
        _specs.Add(new NodeSpec(info, create, options, isSource));
        return info;
    }

    private NodeSpec Find(ModuleInfo module, string parameter) =>
        _specs.Find(spec => ReferenceEquals(spec.Info, module))
        ?? throw new ArgumentException($"{module} isn't part of this pipeline.", parameter);

    // Until verbs arrive, a module's verb is its class name without any generic arity ("Collect`1" → "Collect").
    private static string VerbOf(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
