using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Meep2.Meeplang.Syntax;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Meep2.Plugins;

/// <summary>A plugin installed in the cache, or found on this machine, and checked: ready to load.</summary>
/// <param name="Directive">The <c>#plugin</c> line it was installed for.</param>
/// <param name="Folder">The folder its files are in.</param>
/// <param name="MainAssembly">The path of the plugin's own assembly.</param>
/// <param name="SignedBy">
/// The trusted key that signed it, or null for a GitHub plugin trusted by the SHA its line pins (Q102), or an unsigned
/// plugin that developer mode let through.
/// </param>
public sealed record InstalledPlugin(PluginDirective Directive, string Folder, string MainAssembly, PublicKey? SignedBy)
{
    /// <summary>Something the person running the pipeline should know, such as that the plugin isn't signed.</summary>
    public string? Warning { get; init; }

    /// <summary>
    /// For a NuGet plugin, the packages installed: it and its dependencies, each as <c>Id version sha256:hex</c>, for
    /// the pipeline's lock file (Q103).
    /// </summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// What tells loaded plugins apart: for one on this machine, its path and version, so a rebuilt plugin with a new
    /// version loads afresh when its pipeline is reloaded (Q107, Q127); otherwise its name and version.
    /// </summary>
    public string Key { get; init; } = Directive.Origin == PluginOrigin.Local ? MainAssembly : Directive.ToString();

    /// <summary>The plugin, as its <c>#plugin</c> line names it.</summary>
    public override string ToString() => Directive.ToString();
}

/// <summary>
/// Installs the plugins <c>#plugin</c> lines name: downloads a GitHub release asset or a NuGet package and its
/// dependencies, checks the SHA-256 if the line gives one, unpacks it into the cache, and checks its signature.
/// Plugins on this machine are used where they are.
/// </summary>
/// <remarks>
/// <para>
/// A GitHub plugin is the asset <c>{repo}-{version}.zip</c> in the release tagged <c>v{version}</c> (or
/// <c>{version}</c>): the output of <c>dotnet publish</c>, zipped, with its signature inside. Its line must give
/// the asset's SHA-256, because a release's assets can be replaced (Q34).
/// </para>
/// <para>
/// A NuGet plugin's signature goes in the package beside its assembly, in <c>lib/net11.0</c>. Its dependencies come
/// from the same feeds, picking the lowest version each one allows, as NuGet does. Packages that .NET or Meep
/// already provide are left out.
/// </para>
/// <para>
/// A GitHub plugin is trusted because its line pins its SHA-256 (Q102), so it needn't be signed; if it is, by a key
/// you trust, that's recorded. A NuGet plugin must be signed by a key you trust, and so must a plugin on this machine,
/// unless developer mode is on (Q36).
/// </para>
/// </remarks>
public sealed partial class PluginInstaller(PluginOptions options, HttpClient? http = null)
{
    private const string ManifestName = ".meep-install.json";
    private static readonly NuGetFramework s_target = NuGetFramework.Parse("net11.0");
    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meep2/1.0");
        return client;
    });

    private readonly HttpClient _http = http ?? s_http.Value;

    /// <summary>The settings it installs with.</summary>
    public PluginOptions Options => options;

    /// <summary>Installs the plugin a <c>#plugin</c> line names, or finds it installed already, and checks it.</summary>
    /// <param name="directive">The line.</param>
    /// <param name="baseFolder">Where a relative path to a plugin on this machine starts: the pipeline file's folder.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <exception cref="PluginException">The plugin couldn't be installed, or didn't pass its checks.</exception>
    public Task<InstalledPlugin> InstallAsync(PluginDirective directive, string? baseFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        return directive.Origin switch
        {
            PluginOrigin.Local => Task.FromResult(FindLocal(directive, baseFolder)),
            PluginOrigin.GitHub => InstallGitHubAsync(directive, cancellationToken),
            _ => InstallNuGetAsync(directive, cancellationToken),
        };
    }

    // A plugin on this machine: a DLL, or a folder holding one.
    /// <summary>
    /// A plugin's version, as its project gives it: its informational version without build metadata, such as
    /// 1.2.0-beta.1, or its assembly version.
    /// </summary>
    public static string VersionOf(string assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            if (System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly).ProductVersion is { Length: > 0 } product)
                return product.Split('+')[0];
            return System.Reflection.AssemblyName.GetAssemblyName(assembly).Version?.ToString() ?? "?";
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            return "?";
        }
    }

    private InstalledPlugin FindLocal(PluginDirective directive, string? baseFolder)
    {
        var path = Path.GetFullPath(directive.Name, baseFolder ?? Directory.GetCurrentDirectory());
        string folder;
        string main;
        if (Directory.Exists(path))
        {
            folder = path;
            main = MainAssembly(folder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), directive);
        }
        else if (File.Exists(path))
        {
            folder = Path.GetDirectoryName(path)!;
            main = path;
        }
        else
        {
            throw new PluginException($"Can't find the plugin {directive.Name} (looked for {path}).");
        }

        var key = $"{main}@{VersionOf(main)}";
        try
        {
            var signer = PluginSignature.Verify(folder, options.TrustedKeys(), Covered, directive.Name);
            return new InstalledPlugin(directive, folder, main, signer) { Key = key };
        }
        catch (PluginException exception) when (options.DeveloperMode)
        {
            var warning = exception is UnsignedPluginException
                ? $"{directive.Name} isn't signed. It's loaded because meep is in developer mode."
                : $"{exception.Message} It's loaded anyway because meep is in developer mode.";
            return new InstalledPlugin(directive, folder, main, null) { Warning = warning, Key = key };
        }
        catch (UnsignedPluginException exception)
        {
            throw new PluginException(
                $"{exception.Message} A plugin on this machine needs signing with meep sign, unless meep runs in developer mode (meep run --dev).",
                exception);
        }
    }

    // Offline, a plugin that isn't in the cache isn't downloaded (Q138).
    private void NotOffline(PluginDirective directive)
    {
        if (options.Offline)
            throw new PluginException($"{directive} isn't in Meep's cache, and plugins aren't downloaded while editing. Run the pipeline once to install it.");
    }

    private async Task<InstalledPlugin> InstallGitHubAsync(PluginDirective directive, CancellationToken cancellationToken)
    {
        var (owner, repo) = directive.Name.Split('/') is [var o, var r] ? (o, r) : throw new PluginException($"github.com/{directive.Name} isn't owner/repo.");
        var version = directive.Version!;
        var folder = Path.Combine(options.Cache, "github", owner.ToLowerInvariant(), repo.ToLowerInvariant(), version);
        var asset = $"{repo}-{version}.zip";

        // A cached copy is used if its files are still the ones that were downloaded; otherwise it's downloaded again.
        if (Manifest.Read(folder) is { } manifest && manifest.Sha256 == directive.Sha256 && Unchanged(folder, manifest.Files))
            return Pinned(directive, folder, MainAssembly(folder, repo, directive));
        NotOffline(directive);

        var staging = Staging();
        try
        {
            var zip = Path.Combine(staging, asset);
            string? sha = null;
            foreach (var tag in new[] { $"v{version}", version })
            {
                var url = new Uri(options.GitHub, $"{owner}/{repo}/releases/download/{tag}/{asset}");
                sha = await DownloadAsync(url, zip, cancellationToken).ConfigureAwait(false);
                if (sha is not null)
                    break;
            }
            if (sha is null)
                throw new PluginException($"Couldn't find {asset} in the v{version} release of github.com/{directive.Name}.");
            if (sha != directive.Sha256)
            {
                throw new PluginException(
                    $"github.com/{directive.Name} {version}'s {asset} has the SHA-256 {sha}, but the #plugin line says {directive.Sha256}. Either the release has changed, or the line has the wrong SHA.");
            }

            var unpacked = Path.Combine(staging, "files");
            Unzip(zip, unpacked);
            var installed = Pinned(directive, unpacked, MainAssembly(unpacked, repo, directive));
            Manifest.Write(unpacked, new Manifest(directive.ToString(), sha, []) { Files = HashFiles(unpacked) });
            Publish(unpacked, folder);
            return installed with { Folder = folder, MainAssembly = Path.Combine(folder, Path.GetFileName(installed.MainAssembly)) };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<InstalledPlugin> InstallNuGetAsync(PluginDirective directive, CancellationToken cancellationToken)
    {
        if (!NuGetVersion.TryParse(directive.Version, out var version))
            throw new PluginException($"{directive.Version} isn't a version NuGet understands.");
        var folder = Path.Combine(options.Cache, "nuget", directive.Name.ToLowerInvariant(), version.ToNormalizedString());

        if (Manifest.Read(folder) is { } existing && (directive.Sha256 is null || existing.Sha256 == directive.Sha256))
        {
            var ownFiles = existing.PluginFiles.ToHashSet(StringComparer.Ordinal);
            return Checked(directive, folder, MainAssembly(folder, directive.Name, directive), ownFiles.Contains) with { Packages = existing.Packages };
        }
        NotOffline(directive);

        var logger = NullLogger.Instance;
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        var feeds = new List<FindPackageByIdResource>();
        foreach (var source in options.NuGetSources)
        {
            feeds.Add(await Repository.Factory.GetCoreV3(source).GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false)
                ?? throw new PluginException($"{source} isn't a NuGet feed Meep can read."));
        }

        // Resolve the plugin and its dependencies: the lowest version each range allows, and the highest of those
        // when several packages ask for the same one.
        var resolved = new Dictionary<string, (NuGetVersion Version, byte[] Package)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, VersionRange Range)>();
        queue.Enqueue((directive.Name, new VersionRange(version, true, version, true)));
        while (queue.TryDequeue(out var wanted))
        {
            if (wanted.Id != directive.Name && ProvidedByHost(wanted.Id))
                continue;
            if (resolved.TryGetValue(wanted.Id, out var have) && wanted.Range.Satisfies(have.Version))
                continue;

            (FindPackageByIdResource Feed, NuGetVersion Version)? found = null;
            foreach (var feed in feeds)
            {
                var versions = await feed.GetAllVersionsAsync(wanted.Id, cache, logger, cancellationToken).ConfigureAwait(false);
                if (wanted.Range.FindBestMatch(versions) is { } best)
                {
                    found = (feed, best);
                    break;
                }
            }
            if (found is not { } match)
            {
                throw new PluginException(wanted.Id == directive.Name
                    ? $"Couldn't find {directive.Name} {version} on {string.Join(" or ", options.NuGetSources)}."
                    : $"{directive.Name} needs {wanted.Id} {wanted.Range.PrettyPrint()}, but no version of it fits on {string.Join(" or ", options.NuGetSources)}.");
            }
            if (have.Package is not null && have.Version >= match.Version)
                continue;

            using var download = new MemoryStream();
            if (!await match.Feed.CopyNupkgToStreamAsync(wanted.Id, match.Version, download, cache, logger, cancellationToken).ConfigureAwait(false))
                throw new PluginException($"Couldn't download {wanted.Id} {match.Version}.");
            var package = download.ToArray();
            resolved[wanted.Id] = (match.Version, package);

            using var reader = new PackageArchiveReader(new MemoryStream(package));
            var dependencies = NuGetFrameworkUtility.GetNearest(reader.NuspecReader.GetDependencyGroups(), s_target, group => group.TargetFramework);
            foreach (var dependency in dependencies?.Packages ?? [])
                queue.Enqueue((dependency.Id, dependency.VersionRange));
        }

        var root = resolved[directive.Name];
        var sha = Convert.ToHexStringLower(SHA256.HashData(root.Package));
        if (directive.Sha256 is not null && sha != directive.Sha256)
        {
            throw new PluginException(
                $"nuget:{directive.Name} {version} has the SHA-256 {sha}, but the #plugin line says {directive.Sha256}. Either the package isn't the one the line was written for, or the line has the wrong SHA.");
        }

        var staging = Staging();
        try
        {
            var unpacked = Path.Combine(staging, "files");
            Directory.CreateDirectory(unpacked);
            var pluginFiles = new List<string>();
            var packages = new List<string>();
            foreach (var (id, (packageVersion, bytes)) in resolved)
            {
                using var reader = new PackageArchiveReader(new MemoryStream(bytes));
                var lib = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), s_target, group => group.TargetFramework);
                if (lib is null && id.Equals(directive.Name, StringComparison.OrdinalIgnoreCase))
                    throw new PluginException($"nuget:{directive.Name} {version} has nothing for .NET 11 in its lib folder.");
                foreach (var item in lib?.Items ?? [])
                {
                    var name = Path.GetFileName(item);
                    await using (var source = reader.GetStream(item))
                    await using (var target = File.Create(Path.Combine(unpacked, name)))
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    if (id.Equals(directive.Name, StringComparison.OrdinalIgnoreCase))
                        pluginFiles.Add(name);
                }
                packages.Add($"{id} {packageVersion.ToNormalizedString()} sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
            }

            var own = pluginFiles.ToHashSet(StringComparer.Ordinal);
            var installed = Checked(directive, unpacked, MainAssembly(unpacked, directive.Name, directive, own), own.Contains);
            Manifest.Write(unpacked, new Manifest(directive.ToString(), sha, pluginFiles) { Packages = packages });
            Publish(unpacked, folder);
            return installed with { Folder = folder, MainAssembly = Path.Combine(folder, Path.GetFileName(installed.MainAssembly)), Packages = packages };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    // Packages .NET or Meep provide, so a plugin uses the host's copy: Meep's SDK and core modules, and .NET's
    // own assemblies, whose package names match their assembly names.
    private static bool ProvidedByHost(string id) =>
        PluginLoadContext.IsShared(id)
        || id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase);

    // A plugin from GitHub or this machine has its signature cover every file, apart from hidden ones such as the
    // install manifest.
    private static bool Covered(string file) => !Path.GetFileName(file).StartsWith('.');

    private InstalledPlugin Checked(PluginDirective directive, string folder, string main, Func<string, bool> mustBeCovered)
    {
        try
        {
            return new InstalledPlugin(directive, folder, main, PluginSignature.Verify(folder, options.TrustedKeys(), mustBeCovered, directive.ToString()));
        }
        catch (UnsignedPluginException exception)
        {
            throw new PluginException($"{exception.Message} Plugins from NuGet must be signed by a key you trust.", exception);
        }
    }

    // A GitHub plugin, trusted because its line pins its SHA-256 (Q102). A signature by a key you trust is recorded,
    // and one that doesn't hold is mentioned, since the plugin's author would want to know.
    private InstalledPlugin Pinned(PluginDirective directive, string folder, string main)
    {
        try
        {
            return new InstalledPlugin(directive, folder, main, PluginSignature.Verify(folder, options.TrustedKeys(), Covered, directive.ToString()));
        }
        catch (UnsignedPluginException)
        {
            return new InstalledPlugin(directive, folder, main, null);
        }
        catch (UntrustedSignatureException)
        {
            return new InstalledPlugin(directive, folder, main, null);
        }
        catch (PluginException exception)
        {
            return new InstalledPlugin(directive, folder, main, null)
            {
                Warning = $"{exception.Message} It's loaded anyway, because its #plugin line pins its SHA-256.",
            };
        }
    }

    // The SHA-256 of each file a GitHub plugin unpacked to, apart from hidden ones, so the cached copy can be checked
    // each time it loads.
    private static Dictionary<string, string> HashFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => PluginSignature.Relative(folder, path))
            .Where(Covered)
            .ToDictionary(path => path, path => PluginSignature.Hash(Path.Combine(folder, path)), StringComparer.Ordinal);

    // Whether a cached plugin's files are the ones it was installed with: none changed, missing or added.
    private static bool Unchanged(string folder, IReadOnlyDictionary<string, string>? files)
    {
        if (files is null)
            return false;
        var now = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path => PluginSignature.Relative(folder, path)).Where(Covered).ToList();
        return now.Count == files.Count
            && now.All(path => files.TryGetValue(path, out var hash) && hash == PluginSignature.Hash(Path.Combine(folder, path)));
    }

    // The plugin's own assembly: the one its .deps.json is named for, as dotnet publish writes it, or the one named
    // like the plugin, or the only one there is.
    private static string MainAssembly(string folder, string name, PluginDirective directive, IReadOnlySet<string>? among = null)
    {
        var assemblies = Directory.EnumerateFiles(folder, "*.dll")
            .Where(path => among is null || among.Contains(Path.GetFileName(path)))
            .Where(path => !PluginLoadContext.IsShared(Path.GetFileNameWithoutExtension(path)))
            .ToList();
        var deps = Directory.EnumerateFiles(folder, "*.deps.json").Select(path => Path.Combine(folder, Path.GetFileName(path)[..^".deps.json".Length] + ".dll")).ToList();
        if (deps is [var published] && File.Exists(published))
            return published;
        if (assemblies.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        if (assemblies is [var only])
            return only;
        throw new PluginException(
            $"Couldn't tell which assembly is {directive}'s own: include the .deps.json file dotnet publish makes, or name the assembly {name}.dll.");
    }

    // Downloads to a file, returning its SHA-256, or null if it isn't there.
    private async Task<string?> DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new PluginException($"Couldn't download {url}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Unpacks a zip, refusing any entry that would land outside the folder.
    private static void Unzip(string zip, string folder)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new PluginException($"{Path.GetFileName(zip)} has an entry that would land outside its folder: {entry.FullName}");
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new PluginException($"{Path.GetFileName(zip)} isn't a zip file Meep can read: {exception.Message}", exception);
        }
    }

    private string Staging()
    {
        var staging = Path.Combine(options.Cache, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    // Moves a checked plugin into its place in the cache, replacing what was there.
    private static void Publish(string unpacked, string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        try
        {
            Directory.Move(unpacked, folder);
        }
        catch (IOException) when (Directory.Exists(folder) && Manifest.Read(folder) is not null)
        {
            // Another pipeline installed it at the same moment.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for next time.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    // What the cache knows about an installed plugin.
    private sealed record Manifest(string Plugin, string Sha256, IReadOnlyList<string> PluginFiles)
    {
        public IReadOnlyList<string> Packages { get; init; } = [];

        public Dictionary<string, string>? Files { get; init; }

        public static Manifest? Read(string folder)
        {
            var path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), ManifestJson.Default.Manifest);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static void Write(string folder, Manifest manifest) =>
            File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(manifest, ManifestJson.Default.Manifest));
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(Manifest))]
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
    private sealed partial class ManifestJson : System.Text.Json.Serialization.JsonSerializerContext;
}
