using BenchmarkDotNet.Attributes;
using Meep2.Plugins;
using NuGet.Versioning;

namespace Meep2.Benchmarks;

/// <summary>
/// Finding and downloading a NuGet plugin and what it needs from a feed in a folder (E4): the test plugin, which
/// needs a second package. Build the tests first, which make the feed.
/// </summary>
[MemoryDiagnoser]
public class NuGetBenchmarks
{
    private PluginInstaller _installer = null!;
    private string _home = "";

    [GlobalSetup]
    public void Setup()
    {
        var feed = Path.GetFullPath(Path.Combine(FindRepository(), "tests", "Meep2.Tests", "bin", "Debug", "net11.0", "plugins", "feed"));
        if (!Directory.Exists(feed))
            throw new InvalidOperationException($"There's no feed at {feed}; build the tests first.");
        _home = Directory.CreateTempSubdirectory("meep-nuget-").FullName;
        _installer = new PluginInstaller(new PluginOptions { Home = _home, NuGetSources = [feed] });
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_home, recursive: true);

    [Benchmark]
    public async Task<int> Resolve()
    {
        using var resolver = await NuGetResolver.ConnectAsync(_installer, CancellationToken.None);
        return (await resolver.ResolveAsync("Meep2.TestPlugin", NuGetVersion.Parse("1.0.0"), CancellationToken.None)).Count;
    }

    private static string FindRepository()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "Meep2.slnx")))
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidOperationException("Couldn't find the repository.");
        return folder;
    }
}
