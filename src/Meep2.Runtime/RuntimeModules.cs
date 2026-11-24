namespace Meep2.Runtime;

/// <summary>
/// Marks the assembly of the verbs the runtime provides itself, for registering them beside the core modules: Verify,
/// which alone may make a <see cref="SDK.Security.VerifiedMessage"/> (Q266).
/// </summary>
public static class RuntimeModules
{
    /// <summary>The assembly that holds the runtime's own verbs.</summary>
    public static System.Reflection.Assembly Assembly => typeof(RuntimeModules).Assembly;
}
