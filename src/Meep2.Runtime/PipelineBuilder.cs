using Meep2.Runtime.Internal;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>One of a verb's inflections, for <see cref="PipelineBuilder.AddVerb"/>.</summary>
/// <param name="Name">The inflection's name, such as <c>HTTP</c>.</param>
/// <param name="Create">Makes the inflection. It's called again whenever the inflection needs replacing.</param>
/// <param name="WhyNot">
/// For each message, why this inflection can't take it, or null if it can: how parameters worked out per message
/// choose between inflections. Null takes every message of the inflection's input type.
/// </param>
public sealed record VerbInflection(string Name, Func<AInflection> Create, Func<Message, string?>? WhyNot = null)
{
    /// <summary>The parameters it's made with, if it has a record of them, so a running instance can be offered new ones.</summary>
    public object? Parameters { get; init; }

    /// <summary>
    /// Offers a running instance new parameters: the instance, its old parameters and the new ones. Returns whether it
    /// took them (<see cref="IReconfigurable{TParameters}"/>). Null if it can't take new parameters.
    /// </summary>
    public Func<AModule, object?, object?, bool>? Reconfigure { get; init; }
}

/// <summary>How to make a source, with a record of its parameters so it can be offered new ones while it runs.</summary>
/// <param name="Create">Makes the source.</param>
public sealed record SourceSetup(Func<ASource> Create)
{
    /// <summary>The parameters it's made with, if it has a record of them.</summary>
    public object? Parameters { get; init; }

    /// <summary>Offers a running instance new parameters, returning whether it took them. Null if it can't take any.</summary>
    public Func<AModule, object?, object?, bool>? Reconfigure { get; init; }
}

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

    /// <summary>Adds a source (fountainhead). Its verb is its class name.</summary>
    /// <param name="create">Makes the source. It's called again if the source throws and the error policy restarts it.</param>
    /// <param name="name">A name other modules and <c>fam</c> can refer to it by, like Meeplang's <c>as Name</c>.</param>
    /// <param name="options">How the runtime treats the source. Only <see cref="NodeOptions.Expires"/> and <see cref="NodeOptions.ErrorPolicy"/> apply to sources.</param>
    public ModuleInfo AddSource<TSource>(Func<TSource> create, string? name = null, NodeOptions? options = null)
        where TSource : ASource
    {
        ArgumentNullException.ThrowIfNull(create);
        return AddSource(VerbOf(typeof(TSource)), () => create(), name, options);
    }

    /// <summary>Adds a module that handles messages. Its verb is its class name.</summary>
    /// <param name="create">Makes the module. It's called again whenever the module needs replacing.</param>
    /// <param name="name">A name other modules and <c>fam</c> can refer to it by, like Meeplang's <c>as Name</c>.</param>
    /// <param name="options">How the runtime treats the module, like Meeplang's <c>@</c> modifiers.</param>
    public ModuleInfo Add<TModule>(Func<TModule> create, string? name = null, NodeOptions? options = null)
        where TModule : AInflection
    {
        ArgumentNullException.ThrowIfNull(create);
        return Add(VerbOf(typeof(TModule)), () => create(), name, options);
    }

    /// <summary>Adds a source under a given verb name, as the Meeplang loader does.</summary>
    /// <param name="inflection">The inflection's name, if it has one of its own.</param>
    public ModuleInfo AddSource(string verb, Func<ASource> create, string? name = null, NodeOptions? options = null, string? inflection = null) =>
        AddSource(verb, new SourceSetup(create), name, options, inflection);

    /// <summary>Adds a source with a record of its parameters, so it can be offered new ones while it runs.</summary>
    public ModuleInfo AddSource(string verb, SourceSetup setup, string? name = null, NodeOptions? options = null, string? inflection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(setup);
        options ??= NodeOptions.Default;
        if (options.BufferSize is not null || options.Bypass || options.Errors != ErrorDelivery.Include || options.AcceptExpired || options.HandlesType is not null)
            throw new ArgumentException("A source doesn't receive messages, so only Expires and ErrorPolicy apply to it.", nameof(options));
        var info = NewInfo(verb, name, inflection);
        _specs.Add(new NodeSpec(info, [new InflectionSpec(info, setup.Create, WhyNot: null) { Parameters = setup.Parameters, Reconfigure = setup.Reconfigure }], options, IsSource: true));
        return info;
    }

    /// <summary>Adds a module that handles messages under a given verb name, as the Meeplang loader does.</summary>
    /// <param name="inflection">The inflection's name, if it has one of its own.</param>
    public ModuleInfo Add(string verb, Func<AInflection> create, string? name = null, NodeOptions? options = null, string? inflection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        var info = NewInfo(verb, name, inflection);
        _specs.Add(new NodeSpec(info, [new InflectionSpec(info, create, WhyNot: null)], options ?? NodeOptions.Default, IsSource: false));
        return info;
    }

    /// <summary>
    /// Adds a module whose verb routes each message to one of several inflections: the one whose input type fits the
    /// message most closely, and whose parameters take it (Q19, Q20).
    /// </summary>
    /// <param name="inflections">The inflections, in the order to try them when several fit a message equally well.</param>
    public ModuleInfo AddVerb(string verb, IReadOnlyList<VerbInflection> inflections, string? name = null, NodeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(inflections);
        if (inflections.Count == 0)
            throw new ArgumentException("A verb needs at least one inflection.", nameof(inflections));
        if (inflections.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != inflections.Count)
            throw new ArgumentException($"{verb}'s inflections need different names.", nameof(inflections));

        if (inflections.Count == 1)
        {
            var only = inflections[0];
            var single = NewInfo(verb, name, only.Name);
            _specs.Add(new NodeSpec(single, [Spec(single, only)], options ?? NodeOptions.Default, IsSource: false));
            return single;
        }

        var info = NewInfo(verb, name, inflection: null);
        var specs = inflections
            .Select(i => Spec(new ModuleInfo(info.Number, verb, name, i.Name), i))
            .ToArray();
        _specs.Add(new NodeSpec(info, specs, options ?? NodeOptions.Default, IsSource: false));
        return info;
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

    /// <summary>What the builder has so far, for changing a running pipeline into it.</summary>
    internal PipelineDefinition Definition() => new(Name, Options, [.. _specs], [.. _connections]);

    private static InflectionSpec Spec(ModuleInfo info, VerbInflection inflection) =>
        new(info, inflection.Create, inflection.WhyNot) { Parameters = inflection.Parameters, Reconfigure = inflection.Reconfigure };

    private ModuleInfo NewInfo(string verb, string? name, string? inflection)
    {
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_specs.Exists(spec => spec.Info.Name == name))
                throw new ArgumentException($"This pipeline already has a module called {name}.", nameof(name));
        }
        return new ModuleInfo(_specs.Count + 1, verb, name, inflection);
    }

    private NodeSpec Find(ModuleInfo module, string parameter) =>
        _specs.Find(spec => ReferenceEquals(spec.Info, module))
        ?? throw new ArgumentException($"{module} isn't part of this pipeline.", parameter);

    // A module added from C# without a verb name takes its class name, without any generic arity ("Collect`1" → "Collect").
    private static string VerbOf(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
