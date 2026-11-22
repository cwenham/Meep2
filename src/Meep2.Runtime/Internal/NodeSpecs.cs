using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>One inflection of a module: how to make it, and whether it takes a message, if its parameters decide.</summary>
/// <param name="Info">The inflection's identity, recorded on the messages it emits.</param>
/// <param name="Create">Makes the inflection.</param>
/// <param name="WhyNot">For each message, why the inflection can't take it, or null if it can. Null takes them all.</param>
internal sealed record InflectionSpec(ModuleInfo Info, Func<AModule> Create, Func<Message, string?>? WhyNot)
{
    /// <summary>The parameters it's made with, if it has a record of them, for <see cref="Reconfigure"/>.</summary>
    public object? Parameters { get; init; }

    /// <summary>
    /// Offers a running instance new parameters: the instance, its old parameters and the new ones. True if it took
    /// them (<see cref="IReconfigurable{TParameters}"/>). Null if the module can't take new parameters.
    /// </summary>
    public Func<AModule, object?, object?, bool>? Reconfigure { get; init; }
}

/// <summary>How a module was added to a pipeline.</summary>
/// <param name="Info">The module's identity.</param>
/// <param name="Inflections">Its inflections, in the order they're tried when several take a message equally well. A source has one.</param>
/// <param name="Options">How the runtime treats it.</param>
/// <param name="IsSource">Whether it's a source.</param>
internal sealed record NodeSpec(ModuleInfo Info, InflectionSpec[] Inflections, NodeOptions Options, bool IsSource);
