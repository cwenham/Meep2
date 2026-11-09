using System.Text;

namespace Meep2.Plugins;

/// <summary>
/// A pipeline's lock file, beside it as <c>name.meep.lock</c>: the NuGet packages each of its <c>#plugin</c> lines
/// installed, with their versions and SHA-256s (Q103). The first time a plugin is installed, its packages are written
/// down. After that, any that differ are refused, so a dependency can't change under a pipeline unnoticed.
/// </summary>
/// <remarks>
/// It reads:
/// <code>
/// nuget:Meep.Weather 2.0.1
///   Meep.Weather 2.0.1 sha256:…
///   Newtonsoft.Json 13.0.1 sha256:…
/// </code>
/// To accept a change, delete the plugin's lines and run the pipeline again.
/// </remarks>
public sealed class PluginLock
{
    private const string Header = """
        # The NuGet packages each #plugin line installed, written by meep, which refuses any that differ.
        # To accept a change, delete the plugin's lines and run the pipeline again.
        """;

    private readonly Dictionary<string, List<string>> _plugins;
    private bool _changed;

    private PluginLock(string path, Dictionary<string, List<string>> plugins)
    {
        Path = path;
        _plugins = plugins;
    }

    /// <summary>The lock file's path.</summary>
    public string Path { get; }

    /// <summary>The lock file for a pipeline file: its path with <c>.lock</c> added.</summary>
    public static string For(string pipelinePath) => pipelinePath + ".lock";

    /// <summary>Reads a lock file, or starts an empty one if there isn't one yet.</summary>
    /// <exception cref="PluginException">It has a line Meep can't read.</exception>
    public static PluginLock Load(string path)
    {
        var plugins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            List<string>? current = null;
            var number = 0;
            foreach (var line in File.ReadLines(path))
            {
                number++;
                if (line.Trim() is { Length: 0 } or ['#', ..])
                    continue;
                if (!char.IsWhiteSpace(line[0]))
                {
                    plugins[line.Trim()] = current = [];
                    continue;
                }
                var package = line.Trim();
                if (current is null || package.Split(' ') is not [_, _, ['s', 'h', 'a', '2', '5', '6', ':', ..]])
                    throw new PluginException($"Line {number} of {path} isn't one meep wrote: {line.Trim()}. Fix it, or delete the file to write it again.");
                current.Add(package);
            }
        }
        return new PluginLock(path, plugins);
    }

    /// <summary>
    /// Checks a NuGet plugin's packages against the lock, or writes them down if the lock doesn't have them yet.
    /// </summary>
    /// <exception cref="PluginException">They differ from the lock.</exception>
    public void Check(InstalledPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var key = plugin.Directive.ToString();
        if (!_plugins.TryGetValue(key, out var locked))
        {
            _plugins[key] = [.. plugin.Packages.Order(StringComparer.OrdinalIgnoreCase)];
            _changed = true;
            return;
        }

        var before = locked.ToDictionary(Id, StringComparer.OrdinalIgnoreCase);
        var now = plugin.Packages.ToDictionary(Id, StringComparer.OrdinalIgnoreCase);
        var differences = new List<string>();
        foreach (var (id, was) in before)
        {
            if (!now.TryGetValue(id, out var installed))
                differences.Add($"{id} is no longer installed");
            else if (!string.Equals(was, installed, StringComparison.OrdinalIgnoreCase))
                differences.Add($"{Short(was)} is now {Short(installed)}");
        }
        differences.AddRange(now.Keys.Where(id => !before.ContainsKey(id)).Select(id => $"{Short(now[id])} is new"));
        if (differences.Count > 0)
        {
            throw new PluginException(
                $"{key} installed packages that differ from {System.IO.Path.GetFileName(Path)}: {string.Join("; ", differences)}. If you expect that, delete {key}'s lines from the lock file and run the pipeline again.");
        }
    }

    /// <summary>Writes the lock file if anything was added. Returns whether it wrote.</summary>
    public bool Save()
    {
        if (!_changed)
            return false;
        var text = new StringBuilder(Header).Append('\n');
        foreach (var (plugin, packages) in _plugins.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append(plugin).Append('\n');
            foreach (var package in packages)
                text.Append("  ").Append(package).Append('\n');
        }
        File.WriteAllText(Path, text.ToString());
        _changed = false;
        return true;
    }

    private static string Id(string package) => package.Split(' ')[0];

    // "Id 1.0.0 sha256:3f9a…" with the hash cut short.
    private static string Short(string package)
    {
        var at = package.IndexOf("sha256:", StringComparison.Ordinal);
        return at < 0 || package.Length < at + 7 + 12 ? package : package[..(at + 7 + 12)] + "…";
    }
}
