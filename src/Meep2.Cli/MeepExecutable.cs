using System.Reflection;

namespace Meep2.Cli;

/// <summary>How to run meep again, as a background process or a service.</summary>
internal static class MeepExecutable
{
    /// <summary>The program and the words before meep's own: meep itself, or dotnet and meep.dll.</summary>
    public static IReadOnlyList<string> Command()
    {
        var process = System.Environment.ProcessPath ?? throw new InvalidOperationException("Meep can't tell where its own program is.");
        var entry = Assembly.GetEntryAssembly()?.Location;
        return Path.GetFileNameWithoutExtension(process) == "dotnet" && entry is { Length: > 0 } ? [process, entry] : [process];
    }

    /// <summary>
    /// Variables meep needs when something else starts it: where .NET is, if it isn't in the usual place, and
    /// Meep's folder, if it isn't the usual one.
    /// </summary>
    public static Dictionary<string, string> Environment(string home)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            variables["DOTNET_ROOT"] = root;
        else if (System.Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process) == "dotnet")
            variables["DOTNET_ROOT"] = Path.GetDirectoryName(process)!;
        if (System.Environment.GetEnvironmentVariable("MEEP_HOME") is { Length: > 0 })
            variables["MEEP_HOME"] = home;
        return variables;
    }
}
