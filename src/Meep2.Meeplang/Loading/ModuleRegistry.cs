using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Meep2.Meeplang.Compilation;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// The verbs a pipeline can use, with their inflections, and the message types <c>is</c> can name. Register module
/// classes with a <see cref="VerbAttribute"/>, or add modules by hand.
/// </summary>
/// <remarks>
/// A plugin's verbs and message types (<see cref="AddPlugin"/>) join the others, and can also be reached by the
/// plugin's name, as in <c>Transit.Departures</c> (Q105). Where a plugin uses a name the core modules have, the core
/// one keeps it. Where two plugins use one, it's only reachable by the plugin's name.
/// </remarks>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, List<ModuleRegistration>> _verbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _messageTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.Ordinal);

    // Names two plugins both use, so they need the plugin's name: Verb, Verb.Inflection, or a message type's.
    private readonly Dictionary<string, List<string>> _shared = new(StringComparer.Ordinal);

    /// <summary>Starts with no verbs, and the message types <c>Message</c> and <c>ErrorMessage</c>.</summary>
    public ModuleRegistry()
    {
        AddMessageType(typeof(Message));
        AddMessageType(typeof(ErrorMessage));
    }

    /// <summary>A copy to add to, such as one pipeline's plugins, leaving this registry as it is.</summary>
    public ModuleRegistry Clone()
    {
        var copy = new ModuleRegistry();
        foreach (var (verb, inflections) in _verbs)
            copy._verbs[verb] = [.. inflections];
        foreach (var (name, type) in _messageTypes)
            copy._messageTypes[name] = type;
        foreach (var (name, plugin) in _plugins)
            copy._plugins[name] = plugin;
        foreach (var (name, plugins) in _shared)
            copy._shared[name] = [.. plugins];
        return copy;
    }

    /// <summary>The plugins added, by name.</summary>
    public IEnumerable<string> Plugins => _plugins.Keys;

    /// <summary>Whether a plugin of this name has been added.</summary>
    public bool IsPlugin(string name) => _plugins.ContainsKey(name);

    /// <summary>
    /// Why a plugin can't take this name, or null if it can. A name is a word, not already a plugin's or a verb's,
    /// so that <c>Name.Verb</c> can only mean the plugin's verb.
    /// </summary>
    public string? WhyNotPluginName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return $"A plugin's name is a word of letters and digits, and {name} isn't one.";
        if (_plugins.ContainsKey(name))
            return $"There's already a plugin called {name}.";
        if (_verbs.ContainsKey(name))
            return $"{name} is a verb's name, so it can't be a plugin's too.";
        return null;
    }

    /// <summary>
    /// Registers a plugin's modules and message types, under its own name as well as theirs, so <c>Name.Verb</c> and
    /// <c>is Name.Type</c> reach them (Q105).
    /// </summary>
    /// <exception cref="ArgumentException">The name can't be used (<see cref="WhyNotPluginName"/>), or the plugin's own modules clash with each other.</exception>
    public ModuleRegistry AddPlugin(string name, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (WhyNotPluginName(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var entry = new PluginEntry(name);
        var modules = new List<ModuleRegistration>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract)
                continue;
            if (type.GetCustomAttribute<VerbAttribute>() is not null && typeof(AModule).IsAssignableFrom(type))
                modules.Add(Registration(type, name));
            else if (typeof(Message).IsAssignableFrom(type))
                entry.MessageTypes[ExpressionBinder.FriendlyName(type)] = type;
        }

        // The plugin's own verbs have to agree with each other, as any verb's inflections do.
        foreach (var registration in modules)
        {
            if (!entry.Verbs.TryGetValue(registration.Verb, out var own))
                entry.Verbs[registration.Verb] = own = [];
            if (Problem(own, registration) is { } clash)
                throw new ArgumentException(clash);
            own.Add(registration);
            foreach (var emitted in registration.Emits.Where(t => t.Assembly == assembly))
                entry.MessageTypes[ExpressionBinder.FriendlyName(emitted)] = emitted;
        }
        if (entry.Verbs.ContainsKey(name))
            throw new ArgumentException($"{name} is the name of one of the plugin's own verbs, so it can't be the plugin's too.", nameof(name));
        if (entry.Verbs.Keys.FirstOrDefault(_plugins.ContainsKey) is { } taken)
            throw new ArgumentException($"The plugin has a verb called {taken}, which is another plugin's name. Give that plugin another name with as.");
        _plugins[name] = entry;

        // Then they join everyone's, unless the name is taken.
        foreach (var registration in modules)
        {
            if (!_verbs.TryGetValue(registration.Verb, out var inflections))
                _verbs[registration.Verb] = inflections = [];
            var existing = inflections.FirstOrDefault(r => r.Inflection == registration.Inflection);
            if (existing is null && Problem(inflections, registration) is null)
                inflections.Add(registration);
            else if (existing?.ModuleType != registration.ModuleType && (existing ?? inflections[0]).Plugin is { } other)
                Share(existing is null ? registration.Verb : $"{registration.Verb}.{registration.Inflection}", other, name);
            foreach (var emitted in registration.Emits.Where(t => t.Assembly != assembly))
                _messageTypes.TryAdd(ExpressionBinder.FriendlyName(emitted), emitted);
        }
        foreach (var (typeName, type) in entry.MessageTypes)
        {
            _messageTypes[$"{name}.{typeName}"] = type;
            if (!_messageTypes.TryGetValue(typeName, out var existing))
                _messageTypes[typeName] = type;
            else if (existing != type && PluginOf(existing) is { } other)
                Share(typeName, other, name);
        }
        return this;
    }

    /// <summary>
    /// The plugins that share a name, if they do: a verb, a verb and inflection, or a message type that two plugins
    /// both have, so only the plugin's name tells them apart.
    /// </summary>
    public IReadOnlyList<string>? SharedBy(string name) => _shared.TryGetValue(name, out var plugins) ? plugins : null;

    /// <summary>Finds a plugin's own inflections of a verb.</summary>
    public bool TryGetPluginVerb(string plugin, string verb, [NotNullWhen(true)] out IReadOnlyList<ModuleRegistration>? inflections)
    {
        inflections = null;
        if (!_plugins.TryGetValue(plugin, out var entry) || !entry.Verbs.TryGetValue(verb, out var list))
            return false;
        inflections = list;
        return true;
    }

    /// <summary>A plugin's verbs.</summary>
    public IEnumerable<string> PluginVerbs(string plugin) => _plugins.TryGetValue(plugin, out var entry) ? entry.Verbs.Keys : [];

    /// <summary>Finds a plugin's inflections by their own name, across its verbs.</summary>
    public IReadOnlyList<ModuleRegistration> FindPluginInflection(string plugin, string name) =>
        _plugins.TryGetValue(plugin, out var entry) ? [.. entry.Verbs.Values.SelectMany(inflections => inflections).Where(r => r.Inflection == name)] : [];

    private void Share(string name, string first, string second)
    {
        if (!_shared.TryGetValue(name, out var plugins))
            _shared[name] = plugins = [first];
        if (!plugins.Contains(second))
            plugins.Add(second);
    }

    private string? PluginOf(Type messageType) =>
        _plugins.Values.FirstOrDefault(p => p.MessageTypes.ContainsValue(messageType))?.Name;

    /// <summary>The verbs registered so far.</summary>
    public IEnumerable<string> Verbs => _verbs.Keys;

    /// <summary>The inflections' own names, across every verb.</summary>
    public IEnumerable<string> Inflections => _verbs.Values.SelectMany(inflections => inflections).Select(r => r.Inflection).Distinct();

    /// <summary>The message type names registered so far.</summary>
    public IEnumerable<string> MessageTypeNames => _messageTypes.Keys;

    /// <summary>Registers every public module class with a <see cref="VerbAttribute"/> in an assembly, and its public message types.</summary>
    public ModuleRegistry AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract)
                continue;
            if (type.GetCustomAttribute<VerbAttribute>() is not null && typeof(AModule).IsAssignableFrom(type))
                Add(type);
            else if (typeof(Message).IsAssignableFrom(type))
                AddMessageType(type);
        }
        return this;
    }

    /// <summary>
    /// Registers one module class with a <see cref="VerbAttribute"/>, as an inflection of its verb. It needs a public
    /// constructor that takes its parameters record, or a public parameterless one if it has no parameters.
    /// </summary>
    public ModuleRegistry Add(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        return Register(Registration(moduleType, plugin: null));
    }

    private static ModuleRegistration Registration(Type moduleType, string? plugin)
    {
        var attribute = moduleType.GetCustomAttribute<VerbAttribute>()
            ?? throw new ArgumentException($"{moduleType.Name} has no [Verb] attribute.", nameof(moduleType));
        var inflection = attribute.Inflection ?? ExpressionBinder.FriendlyName(moduleType);

        var constructors = moduleType.GetConstructors();
        var withParameters = constructors.FirstOrDefault(c => c.GetParameters() is [{ ParameterType.IsClass: true }]);
        if (withParameters is not null)
        {
            var parametersType = withParameters.GetParameters()[0].ParameterType;
            return new ModuleRegistration(attribute.Name, inflection, moduleType, parametersType, p => (AModule)withParameters.Invoke([p])) { Plugin = plugin };
        }
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? throw new ArgumentException($"{moduleType.Name} needs a public constructor that takes its parameters, or none.", nameof(moduleType));
        return new ModuleRegistration(attribute.Name, inflection, moduleType, null, _ => (AModule)parameterless.Invoke(null)) { Plugin = plugin };
    }

    /// <summary>
    /// Registers a module without parameters, made by <paramref name="create"/>. Registering the same class again
    /// replaces it.
    /// </summary>
    /// <param name="inflection">The inflection's name. Defaults to the one in the class's <see cref="VerbAttribute"/>, or the class name.</param>
    public ModuleRegistry Add<TModule>(string verb, Func<TModule> create, string? inflection = null)
        where TModule : AModule
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, inflection ?? InflectionOf(typeof(TModule)), typeof(TModule), null, _ => create()));
    }

    /// <summary>
    /// Registers a module with a parameters record, made by <paramref name="create"/>. Registering the same class
    /// again replaces it, as the meep command does to give WriteLine its output.
    /// </summary>
    /// <param name="inflection">The inflection's name. Defaults to the one in the class's <see cref="VerbAttribute"/>, or the class name.</param>
    public ModuleRegistry Add<TModule, TParameters>(string verb, Func<TParameters, TModule> create, string? inflection = null)
        where TModule : AModule
        where TParameters : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, inflection ?? InflectionOf(typeof(TModule)), typeof(TModule), typeof(TParameters), p => create((TParameters)p!)));
    }

    /// <summary>Lets <c>is</c> name a message type.</summary>
    public ModuleRegistry AddMessageType(Type type, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Message).IsAssignableFrom(type) && !type.IsInterface)
            throw new ArgumentException($"{type.Name} isn't a message type.", nameof(type));
        _messageTypes[name ?? ExpressionBinder.FriendlyName(type)] = type;
        return this;
    }

    /// <summary>Finds a verb's inflections, in the order they were registered.</summary>
    public bool TryGetVerb(string verb, [NotNullWhen(true)] out IReadOnlyList<ModuleRegistration>? inflections)
    {
        var found = _verbs.TryGetValue(verb, out var list);
        inflections = list;
        return found;
    }

    /// <summary>Finds inflections by their own name, across every verb.</summary>
    public IReadOnlyList<ModuleRegistration> FindInflection(string name) =>
        [.. _verbs.Values.SelectMany(inflections => inflections).Where(r => r.Inflection == name)];

    /// <summary>Finds a message type by name: its own, or <c>Plugin.Name</c> for a plugin's. A name two plugins share isn't found.</summary>
    public bool TryGetMessageType(string name, [NotNullWhen(true)] out Type? type)
    {
        type = null;
        return !_shared.ContainsKey(name) && _messageTypes.TryGetValue(name, out type);
    }

    private static string InflectionOf(Type moduleType) =>
        moduleType.GetCustomAttribute<VerbAttribute>()?.Inflection ?? ExpressionBinder.FriendlyName(moduleType);

    private ModuleRegistry Register(ModuleRegistration registration)
    {
        if (!_verbs.TryGetValue(registration.Verb, out var inflections))
            _verbs[registration.Verb] = inflections = [];

        var index = inflections.FindIndex(r => r.Inflection == registration.Inflection);
        if (index >= 0 && inflections[index].ModuleType == registration.ModuleType)
            inflections[index] = registration;
        else if (Problem(inflections, registration) is { } problem)
            throw new ArgumentException(problem);
        else
            inflections.Add(registration);

        foreach (var emitted in registration.Emits)
            AddMessageType(emitted);
        return this;
    }

    // Why an inflection can't join a verb's others, or null if it can.
    private static string? Problem(List<ModuleRegistration> inflections, ModuleRegistration registration)
    {
        if (inflections.FirstOrDefault(r => r.Inflection == registration.Inflection) is { } same)
            return $"{registration.Verb}.{registration.Inflection} is already provided by {same.ModuleType.Name}. Give one of them another inflection name.";
        if (inflections.Count == 0)
            return null;
        var first = inflections[0];
        if (first.IsSource != registration.IsSource)
            return $"{registration.Verb}'s inflections have to be all sources or none, but {first.Inflection} and {registration.Inflection} differ.";
        if (first.IsPassthrough != registration.IsPassthrough)
            return $"{registration.Verb}'s inflections have to be all passthrough modules or none, but {first.Inflection} and {registration.Inflection} differ.";
        return null;
    }

    // A plugin's verbs and message types, under its name.
    private sealed class PluginEntry(string name)
    {
        public string Name { get; } = name;

        public Dictionary<string, List<ModuleRegistration>> Verbs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Type> MessageTypes { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>What the loader knows about one inflection of a verb.</summary>
public sealed class ModuleRegistration
{
    internal ModuleRegistration(string verb, string inflection, Type moduleType, Type? parametersType, Func<object?, AModule> create)
    {
        Verb = verb;
        Inflection = inflection;
        ModuleType = moduleType;
        ParametersType = parametersType;
        Create = create;
        IsSource = typeof(ASource).IsAssignableFrom(moduleType);
        IsPassthrough = DerivesFrom(moduleType, typeof(APassthrough<>));
        InputType = IsSource ? null : FindInputType(moduleType);
        var emits = moduleType.GetCustomAttributes<EmitsAttribute>().ToList();
        Emits = [.. emits.Select(a => a.MessageType)];
        _emitsWhen = [.. emits.Select(a => (a.MessageType, a.When is null ? null : SwitchFor(moduleType, parametersType, a)))];
        EmitsInput = moduleType.IsDefined(typeof(EmitsInputAttribute), inherit: false);
        WhyNot = FindWhyNot(moduleType, parametersType);
        Reconfigure = FindReconfigure(moduleType, parametersType);
    }

    /// <summary>The verb's name.</summary>
    public string Verb { get; }

    /// <summary>The plugin it comes from, by the name the pipeline gave it, or null for one that came with Meep.</summary>
    public string? Plugin { get; init; }

    /// <summary>The inflection's own name, such as <c>HTTP</c>.</summary>
    public string Inflection { get; }

    /// <summary>The class that provides it.</summary>
    public Type ModuleType { get; }

    /// <summary>The module's parameters record, or null if it takes none.</summary>
    public Type? ParametersType { get; }

    /// <summary>Whether it's a source (fountainhead).</summary>
    public bool IsSource { get; }

    /// <summary>Whether messages pass straight through it.</summary>
    public bool IsPassthrough { get; }

    /// <summary>The most general message type it handles, or null for a source.</summary>
    public Type? InputType { get; }

    /// <summary>The message types it declares it emits.</summary>
    public IReadOnlyList<Type> Emits { get; }

    /// <summary>Whether it passes on the messages it receives.</summary>
    public bool EmitsInput { get; }

    private readonly List<(Type Type, PropertyInfo? When)> _emitsWhen;

    /// <summary>
    /// The message types it emits with these parameters: all it declares, leaving out those whose
    /// <see cref="EmitsAttribute.When"/> parameter is off.
    /// </summary>
    public IReadOnlyList<Type> EmitsWith(object? parameters) =>
        _emitsWhen.All(e => e.When is null)
            ? Emits
            : [.. _emitsWhen.Where(e => e.When is null || (parameters is not null && e.When.GetValue(parameters) is true)).Select(e => e.Type)];

    /// <summary>Whether its parameters can rule it out (<see cref="IChosenByParameters{TParameters}"/>).</summary>
    public bool IsChosenByParameters => WhyNot is not null;

    internal Func<object?, AModule> Create { get; }

    /// <summary>The inflection's <see cref="IChosenByParameters{TParameters}.WhyNot"/>, taking its parameters record.</summary>
    internal Func<object, Message, string?>? WhyNot { get; }

    /// <summary>
    /// Offers a running instance new parameters (<see cref="IReconfigurable{TParameters}"/>), or null if the class
    /// can't take them.
    /// </summary>
    internal Func<AModule, object?, object?, bool>? Reconfigure { get; }

    // The bool parameter an [Emits(When = ...)] names.
    private static PropertyInfo SwitchFor(Type moduleType, Type? parametersType, EmitsAttribute emits)
    {
        var property = parametersType?.GetProperty(emits.When!, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool))
        {
            throw new ArgumentException(
                $"{moduleType.Name} emits {emits.MessageType.Name} when {emits.When} is on, but {emits.When} isn't a bool parameter of {parametersType?.Name ?? "its (missing) parameters"}.");
        }
        return property;
    }

    private static bool DerivesFrom(Type type, Type genericBase)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericBase)
                return true;
        }
        return false;
    }

    private static Type? FindInputType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AInflection<>))
                return current.GetGenericArguments()[0];
        }
        return null;
    }

    // A call to the class's ParametersChanged, if it takes new parameters of its record's type.
    private static Func<AModule, object?, object?, bool>? FindReconfigure(Type moduleType, Type? parametersType)
    {
        if (parametersType is null)
            return null;
        var contract = moduleType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IReconfigurable<>)
            && i.GetGenericArguments()[0].IsAssignableFrom(parametersType));
        if (contract is null)
            return null;

        var module = Expression.Parameter(typeof(AModule), "module");
        var old = Expression.Parameter(typeof(object), "old");
        var next = Expression.Parameter(typeof(object), "next");
        var argument = contract.GetGenericArguments()[0];
        var call = Expression.Call(
            Expression.Convert(module, contract),
            contract.GetMethod(nameof(IReconfigurable<object>.ParametersChanged))!,
            Expression.Convert(old, argument),
            Expression.Convert(next, argument));
        var hook = Expression.Lambda<Func<AModule, object?, object?, bool>>(call, module, old, next).Compile();
        // Only an instance of this class can take them; one made from an older copy of a reloaded plugin can't.
        return (instance, before, after) => moduleType.IsInstanceOfType(instance) && before is not null && after is not null && hook(instance, before, after);
    }

    // Finds the class's WhyNot for its parameters, whether it's written as a public method or an explicit interface
    // implementation, and compiles a call to it.
    private static Func<object, Message, string?>? FindWhyNot(Type moduleType, Type? parametersType)
    {
        if (parametersType is null)
            return null;
        var contract = moduleType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IChosenByParameters<>)
            && i.GetGenericArguments()[0].IsAssignableFrom(parametersType));
        if (contract is null)
            return null;

        var argument = contract.GetGenericArguments()[0];
        var method = moduleType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m =>
                (m.Name == "WhyNot" || m.Name.EndsWith(".WhyNot", StringComparison.Ordinal))
                && m.ReturnType == typeof(string)
                && m.GetParameters() is [var first, var second]
                && first.ParameterType == argument
                && second.ParameterType == typeof(Message));
        if (method is null)
            return null;

        var parameters = Expression.Parameter(typeof(object), "parameters");
        var message = Expression.Parameter(typeof(Message), "message");
        var call = Expression.Call(method, Expression.Convert(parameters, argument), message);
        return Expression.Lambda<Func<object, Message, string?>>(call, parameters, message).Compile();
    }
}
