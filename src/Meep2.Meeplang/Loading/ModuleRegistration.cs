using System.Linq.Expressions;
using System.Reflection;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Loading;

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
        ChosenForEachMessage = WhyNot is not null && moduleType.IsDefined(typeof(ChosenForEachMessageAttribute), inherit: false);
        MostAtOnce = FindMostAtOnce(moduleType, parametersType);
        Reconfigure = FindReconfigure(moduleType, parametersType);
        var declared = moduleType.GetCustomAttribute<RoleAttribute>()?.Role;
        Role = declared ?? (IsSource ? VerbRole.Source : IsRouter ? VerbRole.Router : VerbRole.Step);
        RoleDeclared = declared is not null;
        if (RoleProblem() is { } problem)
            throw new ArgumentException(problem, nameof(moduleType));
    }

    /// <summary>
    /// What the verb does with messages: declared with <see cref="RoleAttribute"/>, or else worked out from the class.
    /// A source is a <see cref="VerbRole.Source"/>, a module that implements <see cref="IRouteMessages"/> a
    /// <see cref="VerbRole.Router"/>, and any other a <see cref="VerbRole.Step"/>.
    /// </summary>
    public VerbRole Role { get; }

    /// <summary>Whether <see cref="Role"/> was declared with <see cref="RoleAttribute"/>, rather than worked out.</summary>
    public bool RoleDeclared { get; }

    private bool IsRouter => typeof(IRouteMessages).IsAssignableFrom(ModuleType);

    // Why the declared role doesn't fit the class, or null if it does.
    private string? RoleProblem() => Role switch
    {
        VerbRole.Source or VerbRole.Inlet when !IsSource =>
            $"{ModuleType.Name} declares the role {Role}, which is for sources, but it handles messages. Give it {nameof(VerbRole.Step)}, {nameof(VerbRole.Filter)}, {nameof(VerbRole.Outlet)} or {nameof(VerbRole.Tap)}, or none.",
        not (VerbRole.Source or VerbRole.Inlet) when IsSource =>
            $"{ModuleType.Name} is a source, so it can't declare the role {Role}. A source is a {nameof(VerbRole.Source)}, or an {nameof(VerbRole.Inlet)} if its messages come from outside the pipeline.",
        VerbRole.Filter when !EmitsInput && !IsPassthrough =>
            $"{ModuleType.Name} declares the role {nameof(VerbRole.Filter)}, but a filter passes on what it receives, so it needs [EmitsInput] too.",
        VerbRole.Router when !IsRouter =>
            $"{ModuleType.Name} declares the role {nameof(VerbRole.Router)}, but only a module that implements {nameof(IRouteMessages)} routes messages to copies of a branch.",
        _ => null,
    };

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

    /// <summary>
    /// Whether its <see cref="IChosenByParameters{TParameters}.WhyNot"/> is asked for every message, even when the
    /// parameters are the same for each (<see cref="ChosenForEachMessageAttribute"/>).
    /// </summary>
    public bool ChosenForEachMessage { get; }

    internal Func<object?, AModule> Create { get; }

    /// <summary>The inflection's <see cref="IChosenByParameters{TParameters}.WhyNot"/>, taking its parameters record.</summary>
    internal Func<object, Message, string?>? WhyNot { get; }

    /// <summary>The inflection's <see cref="IEmitsAtOnce{TParameters}.MostAtOnce"/>, taking its parameters record.</summary>
    internal Func<object, long?>? MostAtOnce { get; }

    // A call to the class's MostAtOnce, if it says how many messages it can emit at once.
    private static Func<object, long?>? FindMostAtOnce(Type moduleType, Type? parametersType)
    {
        if (FindContract(moduleType, parametersType, typeof(IEmitsAtOnce<>)) is not { } contract)
            return null;
        var argument = contract.GetGenericArguments()[0];
        var method = moduleType.GetMethod("MostAtOnce", BindingFlags.Public | BindingFlags.Static, [argument]);
        if (method is null || method.ReturnType != typeof(long?))
            return null;
        var parameters = Expression.Parameter(typeof(object), "parameters");
        var call = Expression.Call(method, Expression.Convert(parameters, argument));
        return Expression.Lambda<Func<object, long?>>(call, parameters).Compile();
    }

    /// <summary>
    /// Offers a running instance new parameters (<see cref="IReconfigurable{TParameters}"/>), or null if the class
    /// can't take them.
    /// </summary>
    internal Func<AModule, object?, object?, bool>? Reconfigure { get; }

    /// <summary>
    /// The generic interface a module's class implements for its parameters record, such as
    /// <c>IChosenByParameters&lt;TParameters&gt;</c>, taking that record or one it derives from; null if it has none.
    /// </summary>
    /// <param name="moduleType">The module's class.</param>
    /// <param name="parametersType">Its parameters record, or null if it takes none.</param>
    /// <param name="contract">The interface's generic definition, such as <c>typeof(IReconfigurable&lt;&gt;)</c>.</param>
    internal static Type? FindContract(Type moduleType, Type? parametersType, Type contract) =>
        parametersType is null
            ? null
            : moduleType.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType
                && i.GetGenericTypeDefinition() == contract
                && i.GetGenericArguments()[0].IsAssignableFrom(parametersType));

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
        if (FindContract(moduleType, parametersType, typeof(IReconfigurable<>)) is not { } contract)
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
        if (FindContract(moduleType, parametersType, typeof(IChosenByParameters<>)) is not { } contract)
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
