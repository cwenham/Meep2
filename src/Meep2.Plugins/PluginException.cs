namespace Meep2.Plugins;

/// <summary>A plugin couldn't be installed or loaded. The message says why, for the person running the pipeline.</summary>
public class PluginException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PluginException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the exception that caused it.</summary>
    public PluginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A plugin has no signature at all, which developer mode allows for plugins on this machine.</summary>
public sealed class UnsignedPluginException(string message) : PluginException(message);

/// <summary>A plugin is signed, but by a key you don't trust.</summary>
public sealed class UntrustedSignatureException(string message) : PluginException(message);
