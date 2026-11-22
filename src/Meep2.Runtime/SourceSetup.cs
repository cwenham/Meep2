using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>How to make a source, with a record of its parameters so it can be offered new ones while it runs.</summary>
/// <param name="Create">Makes the source.</param>
public sealed record SourceSetup(Func<ASource> Create)
{
    /// <summary>The parameters it's made with, if it has a record of them.</summary>
    public object? Parameters { get; init; }

    /// <summary>Offers a running instance new parameters, returning whether it took them. Null if it can't take any.</summary>
    public Func<AModule, object?, object?, bool>? Reconfigure { get; init; }
}
