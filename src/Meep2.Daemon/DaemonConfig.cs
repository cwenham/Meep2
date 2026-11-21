using System.Globalization;
using Meep2.Meeplang;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;

namespace Meep2.Daemon;

/// <summary>A pipeline file the daemon starts by itself, and how.</summary>
/// <param name="Path">The file.</param>
/// <param name="DeveloperMode">Loads unsigned plugins from this machine for it.</param>
/// <param name="Watch">Reloads it whenever the file changes.</param>
public sealed record DaemonStart(string Path, bool DeveloperMode = false, bool Watch = false);

/// <summary>
/// The daemon's settings file, written in Meeplang (Q119): <c>~/.meep/daemon/daemon.meep</c> unless
/// <c>meep daemon --config</c> names another.
/// <code>
/// // What the daemon starts, and where it reports.
/// defaults
///     otlp = "http://localhost:4318"     // where to send spans and metrics
///     sample = 0.25                      // the share of traces to record
///     meep-hosts = "studio.local, 10.0.0.5:8080"   // hosts known to be Meep, given origins from the first request
///     meep-host-public = true            // Listens tell clients anywhere this daemon's ID, not only private ones
/// const Pipelines = "~/pipelines"
/// Start "{Pipelines}/ticker.meep" watch=true
/// Start "feed.meep" dev=true
/// </code>
/// Relative paths are from the file's folder. Settings on the command line come first.
/// </summary>
public sealed record DaemonConfig
{
    /// <summary>Pipeline files to start, with full paths.</summary>
    public IReadOnlyList<DaemonStart> Start { get; init; } = [];

    /// <summary>An OpenTelemetry collector's OTLP/HTTP address.</summary>
    public string? Otlp { get; init; }

    /// <summary>The share of traces to record, more than 0 and at most 1.</summary>
    public double? Sample { get; init; }

    /// <summary>Hosts known to be Meep, each a host, or a host and port (Q213).</summary>
    public IReadOnlyList<string> MeepHosts { get; init; } = [];

    /// <summary>Whether Listens answer <c>Meep-Host</c> to clients anywhere, not only on private addresses (Q224).</summary>
    public bool MeepHostPublic { get; init; }

    /// <summary>The usual settings file for a Meep folder.</summary>
    public static string DefaultPath(string home) => System.IO.Path.Combine(home, "daemon", "daemon.meep");

    /// <summary>Reads a settings file. A missing file is empty settings.</summary>
    /// <exception cref="FormatException">The file has mistakes. The message says what and where.</exception>
    public static DaemonConfig Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return new DaemonConfig();
        var full = System.IO.Path.GetFullPath(path);
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var settings = MeeplangSettings.Read(MeeplangParser.Parse(new SourceFile(full, File.ReadAllText(full))));
        var problems = new List<string>();
        void Problem(SourceSpan span, string message) => problems.Add($"{full}({span.Line},{span.Column}): error: {message}");

        var config = new DaemonConfig();
        foreach (var (name, setting) in settings.Defaults)
        {
            switch (name)
            {
                case "otlp" when setting.Value is string otlp && Uri.TryCreate(otlp, UriKind.Absolute, out _):
                    config = config with { Otlp = otlp };
                    break;
                case "otlp":
                    Problem(setting.Span, "otlp is a collector's address, such as \"http://localhost:4318\".");
                    break;
                case "sample" when Share(setting.Value) is { } share:
                    config = config with { Sample = share };
                    break;
                case "sample":
                    Problem(setting.Span, "sample is the share of traces to record, more than 0 and at most 1, such as 0.1.");
                    break;
                case "meep-hosts" when setting.Value is string list
                    && list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } hosts
                    && hosts.All(h => Meep2.Modules.MeepHosts.Named(h) is not null):
                    config = config with { MeepHosts = hosts };
                    break;
                case "meep-hosts":
                    Problem(setting.Span, "meep-hosts is hosts known to be Meep, separated by commas, each a host or a host and port, as in \"studio.local, 10.0.0.5:8080\".");
                    break;
                case "meep-host-public" when setting.Value is bool open:
                    config = config with { MeepHostPublic = open };
                    break;
                case "meep-host-public":
                    Problem(setting.Span, "meep-host-public is true or false: whether Listens tell clients anywhere this daemon's ID, not only those on private addresses.");
                    break;
                default:
                    Problem(setting.Span, $"The daemon has no setting called {name}. Its settings are otlp, sample, meep-hosts and meep-host-public.");
                    break;
            }
        }

        var start = new List<DaemonStart>();
        foreach (var line in settings.Lines)
        {
            if (line.Word != "Start")
            {
                Problem(line.Span, $"The daemon's settings have Start lines, not {line.Word}, as in Start \"ticker.meep\".");
                continue;
            }
            if (line.Value?.Value is not string file)
            {
                Problem(line.Span, "Start needs a pipeline file, as in Start \"ticker.meep\".");
                continue;
            }
            bool Flag(string name)
            {
                if (!line.Named.TryGetValue(name, out var value))
                    return false;
                if (value.Value is bool flag)
                    return flag;
                Problem(value.Span, $"{name} is true or false.");
                return false;
            }
            foreach (var (name, value) in line.Named)
            {
                if (name is not ("watch" or "dev"))
                    Problem(value.Span, $"Start takes watch and dev, not {name}.");
            }
            start.Add(new DaemonStart(Resolve(file, folder), Flag("dev"), Flag("watch")));
        }

        if (problems.Count > 0)
            throw new FormatException(string.Join(Environment.NewLine, problems));
        return config with { Start = start };
    }

    private static double? Share(object? value) =>
        value is not null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) && share is > 0 and <= 1
            ? share
            : null;

    // ~ is the home folder, and a relative path is from the settings file's folder.
    private static string Resolve(string file, string folder)
    {
        if (file == "~" || file.StartsWith("~/", StringComparison.Ordinal))
            file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file.Length > 2 ? file[2..] : "");
        return System.IO.Path.GetFullPath(file, folder);
    }
}
