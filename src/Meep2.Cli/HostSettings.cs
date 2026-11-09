using System.Globalization;
using System.Reflection;

namespace Meep2.Cli;

/// <summary>The options <c>meep run</c> and <c>meep daemon</c> share, and the files they're given.</summary>
internal sealed record HostSettings
{
    /// <summary>Pipeline files, in the order given.</summary>
    public List<string> Files { get; } = [];

    /// <summary>--dev: load unsigned plugins from this machine.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>--otlp: an OpenTelemetry collector's OTLP/HTTP address.</summary>
    public string? Otlp { get; set; }

    /// <summary>--sample: the share of traces to record, more than 0 and at most 1.</summary>
    public double? Sample { get; set; }

    /// <summary>--detach: run in the background (meep run only).</summary>
    public bool Detach { get; set; }

    /// <summary>--config: the daemon's settings file (meep daemon only).</summary>
    public string? Config { get; set; }

    /// <summary>--detached-to: this is the background process of meep run --detach, and its output goes to this file.</summary>
    public string? DetachedTo { get; set; }

    /// <summary>The options as words again, for running meep with them in the background or as a service.</summary>
    public IEnumerable<string> Options()
    {
        if (DeveloperMode)
            yield return "--dev";
        if (Otlp is not null)
        {
            yield return "--otlp";
            yield return Otlp;
        }
        if (Sample is { } sample)
        {
            yield return "--sample";
            yield return sample.ToString(CultureInfo.InvariantCulture);
        }
        if (Config is not null)
        {
            yield return "--config";
            yield return Path.GetFullPath(Config);
        }
    }

    /// <summary>Reads the options and files, in any order. Returns null, having said why, if something isn't right.</summary>
    public static HostSettings? Parse(string[] words, TextWriter error)
    {
        var settings = new HostSettings();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var next = i + 1 < words.Length ? words[i + 1] : null;
            switch (word)
            {
                case "--dev":
                    settings.DeveloperMode = true;
                    break;
                case "--detach":
                    settings.Detach = true;
                    break;
                case "--otlp" when next is not null && Uri.TryCreate(next, UriKind.Absolute, out _):
                    settings.Otlp = next;
                    i++;
                    break;
                case "--otlp":
                    error.WriteLine("--otlp needs a collector's address, such as http://localhost:4318.");
                    return null;
                case "--sample" when Share(next) is { } share:
                    settings.Sample = share;
                    i++;
                    break;
                case "--sample":
                    error.WriteLine("--sample needs the share of traces to record, such as 0.1 or 10%, or 1 to record them all.");
                    return null;
                case "--config" when next is not null:
                    settings.Config = next;
                    i++;
                    break;
                case "--detached-to" when next is not null:
                    settings.DetachedTo = next;
                    i++;
                    break;
                case ['-', '-', ..] or "--config" or "--detached-to":
                    error.WriteLine($"meep doesn't know the option {word}.");
                    return null;
                default:
                    settings.Files.Add(word);
                    break;
            }
        }
        return settings;
    }

    // 0.1, or 10%.
    private static double? Share(string? text)
    {
        if (text is null)
            return null;
        var percent = text.EndsWith('%');
        if (!double.TryParse(percent ? text[..^1] : text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        if (percent)
            value /= 100;
        return value is > 0 and <= 1 ? value : null;
    }
}

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
