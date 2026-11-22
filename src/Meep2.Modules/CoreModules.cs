namespace Meep2.Modules;

/// <summary>Marks the assembly of core modules, for registering them.</summary>
public static class CoreModules
{
    /// <summary>The assembly that holds the core modules.</summary>
    public static System.Reflection.Assembly Assembly => typeof(CoreModules).Assembly;
}
