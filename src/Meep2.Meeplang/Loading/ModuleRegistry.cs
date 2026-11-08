using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Meep2.Meeplang.Compilation;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// The verbs a pipeline can use, and the message types <c>is</c> can name. Register module classes with a
/// <see cref="VerbAttribute"/>, or add modules by hand.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, ModuleRegistration> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _messageTypes = new(StringComparer.Ordinal);

    /// <summary>Starts with no verbs, and the message types <c>Message</c> and <c>ErrorMessage</c>.</summary>
    public ModuleRegistry()
    {
        AddMessageType(typeof(Message));
        AddMessageType(typeof(ErrorMessage));
    }

    /// <summary>The verbs registered so far.</summary>
    public IEnumerable<string> Verbs => _modules.Keys;

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
    /// Registers one module class with a <see cref="VerbAttribute"/>. It needs a public constructor that takes its
    /// parameters record, or a public parameterless one if it has no parameters.
    /// </summary>
    public ModuleRegistry Add(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        var verb = moduleType.GetCustomAttribute<VerbAttribute>()?.Name
            ?? throw new ArgumentException($"{moduleType.Name} has no [Verb] attribute.", nameof(moduleType));

        var constructors = moduleType.GetConstructors();
        var withParameters = constructors.FirstOrDefault(c => c.GetParameters() is [{ ParameterType.IsClass: true }]);
        if (withParameters is not null)
        {
            var parametersType = withParameters.GetParameters()[0].ParameterType;
            return Register(new ModuleRegistration(verb, moduleType, parametersType, p => (AModule)withParameters.Invoke([p])));
        }
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? throw new ArgumentException($"{moduleType.Name} needs a public constructor that takes its parameters, or none.", nameof(moduleType));
        return Register(new ModuleRegistration(verb, moduleType, null, _ => (AModule)parameterless.Invoke(null)));
    }

    /// <summary>Registers a module without parameters, made by <paramref name="create"/>.</summary>
    public ModuleRegistry Add<TModule>(string verb, Func<TModule> create)
        where TModule : AModule
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, typeof(TModule), null, _ => create()));
    }

    /// <summary>Registers a module with a parameters record, made by <paramref name="create"/>.</summary>
    public ModuleRegistry Add<TModule, TParameters>(string verb, Func<TParameters, TModule> create)
        where TModule : AModule
        where TParameters : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(create);
        return Register(new ModuleRegistration(verb, typeof(TModule), typeof(TParameters), p => create((TParameters)p!)));
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

    /// <summary>Finds a verb.</summary>
    public bool TryGetModule(string verb, [NotNullWhen(true)] out ModuleRegistration? registration) =>
        _modules.TryGetValue(verb, out registration);

    /// <summary>Finds a message type by name.</summary>
    public bool TryGetMessageType(string name, [NotNullWhen(true)] out Type? type) =>
        _messageTypes.TryGetValue(name, out type);

    private ModuleRegistry Register(ModuleRegistration registration)
    {
        if (_modules.TryGetValue(registration.Verb, out var existing) && existing.ModuleType != registration.ModuleType)
        {
            throw new ArgumentException(
                $"The verb {registration.Verb} is already provided by {existing.ModuleType.Name}. Verbs with several inflections arrive later.");
        }
        _modules[registration.Verb] = registration;
        foreach (var emitted in registration.Emits)
            AddMessageType(emitted);
        return this;
    }
}

/// <summary>What the loader knows about a verb.</summary>
public sealed class ModuleRegistration
{
    internal ModuleRegistration(string verb, Type moduleType, Type? parametersType, Func<object?, AModule> create)
    {
        Verb = verb;
        ModuleType = moduleType;
        ParametersType = parametersType;
        Create = create;
        IsSource = typeof(ASource).IsAssignableFrom(moduleType);
        IsPassthrough = DerivesFrom(moduleType, typeof(APassthrough<>));
        InputType = IsSource ? null : FindInputType(moduleType);
        Emits = [.. moduleType.GetCustomAttributes<EmitsAttribute>().Select(a => a.MessageType)];
        EmitsInput = moduleType.IsDefined(typeof(EmitsInputAttribute), inherit: false);
    }

    /// <summary>The verb's name.</summary>
    public string Verb { get; }

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

    internal Func<object?, AModule> Create { get; }

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
}
