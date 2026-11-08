namespace Meep2.SDK.Modules;

/// <summary>
/// The base for everything that can sit in a pipeline. Derive from <see cref="ASource"/>,
/// <see cref="AInflection{TIn}"/> or <see cref="APassthrough{TIn}"/>.
/// </summary>
/// <remarks>
/// A module that implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> is disposed when its
/// pipeline finishes, and when the runtime replaces it after it throws.
/// </remarks>
public abstract class AModule
{
    private protected AModule()
    {
    }
}
