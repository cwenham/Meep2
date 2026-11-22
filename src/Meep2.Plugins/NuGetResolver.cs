using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Meep2.Plugins;

/// <summary>A package NuGet resolved: the version chosen, and the package as downloaded.</summary>
internal sealed record ResolvedPackage(NuGetVersion Version, byte[] Package);

/// <summary>
/// Finds a NuGet plugin and its dependencies on the feeds the installer's settings name, and downloads them: the lowest
/// version each range allows, and the highest of those when several packages ask for the same one. Packages .NET or
/// Meep provide are left out.
/// </summary>
internal sealed class NuGetResolver : IDisposable
{
    private readonly PluginInstaller _installer;
    private readonly IReadOnlyList<FindPackageByIdResource> _feeds;
    private readonly SourceCacheContext _cache = new() { NoCache = true, DirectDownload = true };

    private NuGetResolver(PluginInstaller installer, IReadOnlyList<FindPackageByIdResource> feeds)
    {
        _installer = installer;
        _feeds = feeds;
    }

    /// <summary>Connects to each feed the installer's settings name.</summary>
    /// <exception cref="PluginException">A source isn't a NuGet feed.</exception>
    public static async Task<NuGetResolver> ConnectAsync(PluginInstaller installer, CancellationToken cancellationToken)
    {
        var feeds = new List<FindPackageByIdResource>();
        foreach (var source in installer.Options.NuGetSources)
        {
            feeds.Add(await Repository.Factory.GetCoreV3(source).GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false)
                ?? throw new PluginException($"{source} isn't a NuGet feed Meep can read."));
        }
        return new NuGetResolver(installer, feeds);
    }

    /// <summary>The package <paramref name="id"/> at exactly <paramref name="version"/>, and every package it needs, by ID.</summary>
    /// <exception cref="PluginException">One can't be found or downloaded.</exception>
    public async Task<Dictionary<string, ResolvedPackage>> ResolveAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageDependency>();
        queue.Enqueue(new PackageDependency(id, new VersionRange(version, true, version, true)));
        while (queue.TryDequeue(out var wanted))
        {
            if (wanted.Id != id && PluginInstaller.ProvidedByHost(wanted.Id))
                continue;
            if (resolved.TryGetValue(wanted.Id, out var have) && wanted.VersionRange.Satisfies(have.Version))
                continue;
            var match = await FindAsync(wanted, cancellationToken).ConfigureAwait(false)
                ?? throw new PluginException(wanted.Id == id
                    ? $"Couldn't find {id} {version} on {Sources}."
                    : $"{id} needs {wanted.Id} {wanted.VersionRange.PrettyPrint()}, but no version of it fits on {Sources}.");
            if (have is not null && have.Version >= match.Version)
                continue;
            var package = await DownloadAsync(match, wanted.Id, cancellationToken).ConfigureAwait(false);
            resolved[wanted.Id] = new ResolvedPackage(match.Version, package);
            foreach (var dependency in DependenciesOf(package))
                queue.Enqueue(dependency);
        }
        return resolved;
    }

    /// <inheritdoc/>
    public void Dispose() => _cache.Dispose();

    private string Sources => string.Join(" or ", _installer.Options.NuGetSources);

    // The first feed with a version that fits, and that version.
    private async Task<FeedMatch?> FindAsync(PackageDependency wanted, CancellationToken cancellationToken)
    {
        foreach (var feed in _feeds)
        {
            var versions = await feed.GetAllVersionsAsync(wanted.Id, _cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            if (wanted.VersionRange.FindBestMatch(versions) is { } best)
                return new FeedMatch(feed, best);
        }
        return null;
    }

    private async Task<byte[]> DownloadAsync(FeedMatch match, string id, CancellationToken cancellationToken)
    {
        using var download = new PluginInstaller.LimitedStream(_installer.Options.MaxDownload, () => _installer.TooLarge($"{id} {match.Version}"));
        if (!await match.Feed.CopyNupkgToStreamAsync(id, match.Version, download, _cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false))
            throw new PluginException($"Couldn't download {id} {match.Version}.");
        return download.ToArray();
    }

    // The packages a package needs for .NET 11.
    private static IEnumerable<PackageDependency> DependenciesOf(byte[] package)
    {
        using var reader = new PackageArchiveReader(new MemoryStream(package));
        var dependencies = NuGetFrameworkUtility.GetNearest(reader.NuspecReader.GetDependencyGroups(), PluginInstaller.s_target, group => group.TargetFramework);
        return [.. dependencies?.Packages ?? []];
    }

    private sealed record FeedMatch(FindPackageByIdResource Feed, NuGetVersion Version);
}
