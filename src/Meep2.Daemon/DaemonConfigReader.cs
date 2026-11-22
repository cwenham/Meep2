using Meep2.Meeplang;
using Meep2.Meeplang.Syntax;

namespace Meep2.Daemon;

/// <summary>
/// Reads the daemon's settings file (<see cref="DaemonConfig.Load"/>), collecting every mistake with where it is, so
/// one error lists them all.
/// </summary>
/// <param name="file">The settings file, in full.</param>
/// <param name="folder">Its folder, which relative paths are from.</param>
internal sealed class DaemonConfigReader(string file, string folder)
{
    private readonly List<string> _problems = [];

    /// <summary>The settings under <c>defaults</c>.</summary>
    public DaemonConfig Defaults(IReadOnlyDictionary<string, SettingValue> defaults)
    {
        var config = new DaemonConfig();
        foreach (var (name, setting) in defaults)
            config = With(config, name, setting);
        return config;
    }

    /// <summary>The pipeline files its <c>Start</c> lines name, with full paths.</summary>
    public List<DaemonStart> Starts(IReadOnlyList<SettingsLine> lines)
    {
        var start = new List<DaemonStart>();
        foreach (var line in lines)
        {
            if (StartOf(line) is { } one)
                start.Add(one);
        }
        return start;
    }

    /// <summary>Throws if anything was wrong, with a line for each mistake.</summary>
    /// <exception cref="FormatException">The file has mistakes.</exception>
    public void ThrowIfProblems()
    {
        if (_problems.Count > 0)
            throw new FormatException(string.Join(Environment.NewLine, _problems));
    }

    private void Problem(SourceSpan span, string message) => _problems.Add($"{file}({span.Line},{span.Column}): error: {message}");

    private DaemonConfig With(DaemonConfig config, string name, SettingValue setting)
    {
        switch (name)
        {
            case "otlp" when setting.Value is string otlp && Uri.TryCreate(otlp, UriKind.Absolute, out _):
                return config with { Otlp = otlp };
            case "otlp":
                Problem(setting.Span, "otlp is a collector's address, such as \"http://localhost:4318\".");
                return config;
            case "sample" when DaemonConfig.Share(setting.Value) is { } share:
                return config with { Sample = share };
            case "sample":
                Problem(setting.Span, "sample is the share of traces to record, more than 0 and at most 1, such as 0.1.");
                return config;
            case "meep-hosts" when setting.Value is string list
                && list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } hosts
                && hosts.All(h => Meep2.Modules.MeepHosts.Named(h) is not null):
                return config with { MeepHosts = hosts };
            case "meep-hosts":
                Problem(setting.Span, "meep-hosts is hosts known to be Meep, separated by commas, each a host or a host and port, as in \"studio.local, 10.0.0.5:8080\".");
                return config;
            case "meep-host-public" when setting.Value is bool open:
                return config with { MeepHostPublic = open };
            case "meep-host-public":
                Problem(setting.Span, "meep-host-public is true or false: whether Listens tell clients anywhere this daemon's ID, not only those on private addresses.");
                return config;
            default:
                Problem(setting.Span, $"The daemon has no setting called {name}. Its settings are otlp, sample, meep-hosts and meep-host-public.");
                return config;
        }
    }

    // A Start line: the pipeline file it names, and whether to load unsigned plugins for it and watch its file. Null,
    // with what's wrong, if it isn't one.
    private DaemonStart? StartOf(SettingsLine line)
    {
        if (line.Word != "Start")
        {
            Problem(line.Span, $"The daemon's settings have Start lines, not {line.Word}, as in Start \"ticker.meep\".");
            return null;
        }
        if (line.Value?.Value is not string path)
        {
            Problem(line.Span, "Start needs a pipeline file, as in Start \"ticker.meep\".");
            return null;
        }
        foreach (var (name, value) in line.Named)
        {
            if (name is not ("watch" or "dev"))
                Problem(value.Span, $"Start takes watch and dev, not {name}.");
        }
        return new DaemonStart(DaemonConfig.Resolve(path, folder), Flag(line, "dev"), Flag(line, "watch"));
    }

    private bool Flag(SettingsLine line, string name)
    {
        if (!line.Named.TryGetValue(name, out var value))
            return false;
        if (value.Value is bool flag)
            return flag;
        Problem(value.Span, $"{name} is true or false.");
        return false;
    }
}
