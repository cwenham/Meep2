using System.Globalization;
using Meep2.Meeplang;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Shared;

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
        var reader = new DaemonConfigReader(full, folder);
        var config = reader.Defaults(settings.Defaults);
        var start = reader.Starts(settings.Lines);
        reader.ThrowIfProblems();
        return config with { Start = start };
    }

    internal static double? Share(object? value) =>
        value is not null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) && share is > 0 and <= 1
            ? share
            : null;

    // ~ is the home folder, and a relative path is from the settings file's folder.
    internal static string Resolve(string file, string folder)
    {
        return System.IO.Path.GetFullPath(HomeFolder.Expand(file), folder);
    }
}
