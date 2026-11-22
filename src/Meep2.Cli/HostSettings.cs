using System.Globalization;

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

    /// <summary>
    /// --meep-host-public: Listens tell clients anywhere this host's ID, not only those on this machine or a private
    /// address (Q230), as <c>meep-host-public = true</c> does in the daemon's settings.
    /// </summary>
    public bool MeepHostPublic { get; set; }

    /// <summary>
    /// --meep-hosts: hosts known to be Meep, given origins from the first request, as <c>meep-hosts</c> names them in
    /// the daemon's settings; for <c>meep run</c>, which has no settings file, such as machines on a Tailscale network.
    /// </summary>
    public List<string> MeepHosts { get; } = [];

    /// <summary>--detach: run in the background (meep run only).</summary>
    public bool Detach { get; set; }

    /// <summary>--watch: change the running pipeline whenever its file changes (goal 4).</summary>
    public bool Watch { get; set; }

    /// <summary>--config: the daemon's settings file (meep daemon only).</summary>
    public string? Config { get; set; }

    /// <summary>--detached-to: this is the background process of meep run --detach, and its output goes to this file.</summary>
    public string? DetachedTo { get; set; }

    /// <summary>The options as words again, for running meep with them in the background or as a service.</summary>
    public IEnumerable<string> Options()
    {
        if (DeveloperMode)
            yield return "--dev";
        if (Watch)
            yield return "--watch";
        if (MeepHostPublic)
            yield return "--meep-host-public";
        if (MeepHosts.Count > 0)
        {
            yield return "--meep-hosts";
            yield return string.Join(',', MeepHosts);
        }
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
                case "--watch":
                    settings.Watch = true;
                    break;
                case "--meep-host-public":
                    settings.MeepHostPublic = true;
                    break;
                case "--meep-hosts" when Hosts(next) is { } hosts:
                    settings.MeepHosts.AddRange(hosts);
                    i++;
                    break;
                case "--meep-hosts":
                    error.WriteLine("--meep-hosts needs hosts known to be Meep, separated by commas, each a host or a host and port, such as studio.local,10.0.0.5:8080.");
                    return null;
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

    // studio.local,10.0.0.5:8080, as the daemon's meep-hosts setting takes them.
    private static string[]? Hosts(string? text) =>
        text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } hosts
        && hosts.All(h => Meep2.Modules.MeepHosts.Named(h) is not null)
            ? hosts
            : null;

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
