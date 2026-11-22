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
