using System.Globalization;
using System.Text.Json;

namespace Meep2.Daemon;

/// <summary>
/// The daemon's settings file, <c>~/.meep/daemon/config.json</c> unless <c>meep daemon --config</c> names another
/// (Q112). It's JSON, which may have comments:
/// <code>
/// {
///   // Pipelines to start when the daemon starts. Relative paths are from this file's folder.
///   "start": ["~/pipelines/ticker.meep"],
///   "otlp": "http://localhost:4318",   // where to send spans and metrics
///   "sample": 0.1                      // the share of traces to record
/// }
/// </code>
/// Settings on the command line come first.
/// </summary>
public sealed record DaemonConfig
{
    /// <summary>Pipeline files to start, as full paths.</summary>
    public IReadOnlyList<string> Start { get; init; } = [];

    /// <summary>An OpenTelemetry collector's OTLP/HTTP address.</summary>
    public string? Otlp { get; init; }

    /// <summary>The share of traces to record, more than 0 and at most 1.</summary>
    public double? Sample { get; init; }

    /// <summary>The usual settings file for a Meep folder.</summary>
    public static string DefaultPath(string home) => Path.Combine(home, "daemon", "config.json");

    /// <summary>Reads a settings file. A missing file is empty settings.</summary>
    /// <exception cref="FormatException">The file isn't settings the daemon understands. The message says where.</exception>
    public static DaemonConfig Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return new DaemonConfig();
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{path} isn't JSON: {exception.Message}", exception);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{path} should hold a JSON object, such as {{ \"start\": [\"ticker.meep\"] }}.");
            var config = new DaemonConfig();
            foreach (var setting in document.RootElement.EnumerateObject())
            {
                config = setting.Name switch
                {
                    "start" when setting.Value.ValueKind == JsonValueKind.Array && setting.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) =>
                        config with { Start = [.. setting.Value.EnumerateArray().Select(e => Resolve(e.GetString()!, folder))] },
                    "start" => throw new FormatException($"In {path}, start should be a list of pipeline files, such as [\"ticker.meep\"]."),
                    "otlp" when setting.Value.ValueKind == JsonValueKind.String && Uri.TryCreate(setting.Value.GetString(), UriKind.Absolute, out _) =>
                        config with { Otlp = setting.Value.GetString() },
                    "otlp" => throw new FormatException($"In {path}, otlp should be a collector's address, such as \"http://localhost:4318\"."),
                    "sample" when setting.Value.ValueKind == JsonValueKind.Number && setting.Value.GetDouble() is > 0 and <= 1 =>
                        config with { Sample = setting.Value.GetDouble() },
                    "sample" => throw new FormatException($"In {path}, sample should be the share of traces to record, more than 0 and at most 1, such as 0.1."),
                    _ => throw new FormatException($"In {path}, there's no setting called {setting.Name}. The daemon's settings are start, otlp and sample."),
                };
            }
            return config;
        }
    }

    // ~ is the home folder, and a relative path is from the settings file's folder.
    private static string Resolve(string file, string folder)
    {
        if (file == "~" || file.StartsWith("~/", StringComparison.Ordinal))
            file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file.Length > 2 ? file[2..] : "");
        return System.IO.Path.GetFullPath(file, folder);
    }

    /// <summary>A share such as 0.1 written as people say it: 10%.</summary>
    public static string Percent(double share) => (share * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
