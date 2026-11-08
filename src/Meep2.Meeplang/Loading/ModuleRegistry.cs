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
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, List<ModuleRegistration>> _verbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type> _messageTypes = new(StringComparer.Ordinal);

    /// <summary>Starts with no verbs, and the message types <c>Message</c> and <c>ErrorMessage</c>.</summary>
    public ModuleRegistry()
    {
        AddMessageType(typeof(Message));
        AddMessageType(typeof(ErrorMessage));
    }

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
        var attribute = moduleType.GetCustomAttribute<VerbAttribute>()
            ?? throw new ArgumentException($"{moduleType.Name} has no [Verb] attribute.", nameof(moduleType));
        var inflection = attribute.Inflection ?? ExpressionBinder.FriendlyName(moduleType);

        var constructors = moduleType.GetConstructors();
        var withParameters = constructors.FirstOrDefault(c => c.GetParameters() is [{ ParameterType.IsClass: true }]);
        if (withParameters is not null)
        {
            var parametersType = withParameters.GetParameters()[0].ParameterType;
            return Register(new ModuleRegistration(attribute.Name, inflection, moduleType, parametersType, p => (AModule)withParameters.Invoke([p])));
        }
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? throw new ArgumentException($"{moduleType.Name} needs a public constructor that takes its parameters, or none.", nameof(moduleType));
        return Register(new ModuleRegistration(attribute.Name, inflection, moduleType, null, _ => (AModule)parameterless.Invoke(null)));
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

    /// <summary>Finds a message type by name.</summary>
    public bool TryGetMessageType(string name, [NotNullWhen(true)] out Type? type) =>
        _messageTypes.TryGetValue(name, out type);

    private static string InflectionOf(Type moduleType) =>
        moduleType.GetCustomAttribute<VerbAttribute>()?.Inflection ?? ExpressionBinder.FriendlyName(moduleType);

    private ModuleRegistry Register(ModuleRegistration registration)
    {
        if (!_verbs.TryGetValue(registration.Verb, out var inflections))
            _verbs[registration.Verb] = inflections = [];

        var index = inflections.FindIndex(r => r.Inflection == registration.Inflection);
        if (index >= 0)
        {
            if (inflections[index].ModuleType != registration.ModuleType)
            {
                throw new ArgumentException(
                    $"{registration.Verb}.{registration.Inflection} is already provided by {inflections[index].ModuleType.Name}. Give one of them another inflection name.");
            }
            inflections[index] = registration;
        }
        else
        {
            if (inflections.Count > 0)
            {
                var first = inflections[0];
                if (first.IsSource != registration.IsSource)
                    throw new ArgumentException($"{registration.Verb}'s inflections have to be all sources or none, but {first.Inflection} and {registration.Inflection} differ.");
                if (first.IsPassthrough != registration.IsPassthrough)
                    throw new ArgumentException($"{registration.Verb}'s inflections have to be all passthrough modules or none, but {first.Inflection} and {registration.Inflection} differ.");
            }
            inflections.Add(registration);
        }

        foreach (var emitted in registration.Emits)
            AddMessageType(emitted);
        return this;
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
        Emits = [.. moduleType.GetCustomAttributes<EmitsAttribute>().Select(a => a.MessageType)];
        EmitsInput = moduleType.IsDefined(typeof(EmitsInputAttribute), inherit: false);
        WhyNot = FindWhyNot(moduleType, parametersType);
    }

    /// <summary>The verb's name.</summary>
    public string Verb { get; }

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

    /// <summary>Whether its parameters can rule it out (<see cref="IChosenByParameters{TParameters}"/>).</summary>
    public bool IsChosenByParameters => WhyNot is not null;

    internal Func<object?, AModule> Create { get; }

    /// <summary>The inflection's <see cref="IChosenByParameters{TParameters}.WhyNot"/>, taking its parameters record.</summary>
    internal Func<object, Message, string?>? WhyNot { get; }

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
