using System.Security.Cryptography;
using System.Text;
using Meep2.Git;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Parsing;
using Meep2.Plugins;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>Where a pipeline comes from (goal 7).</summary>
public enum PipelineOrigin
{
    /// <summary>A file on this machine.</summary>
    File,

    /// <summary>Standard input: <c>meep run -</c>.</summary>
    Stdin,

    /// <summary>An https:// address, or http:// for one on this machine.</summary>
    Url,

    /// <summary>A file in a Git repository: <c>git+https://host/owner/repo.git#path/file.meep@ref</c>.</summary>
    Git,

    /// <summary>Text sent to the daemon over its socket.</summary>
    Text,
}

/// <summary>A pipeline's address, as <c>meep run</c> and <c>meep start</c> take it.</summary>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Location">The file's full path, the URL, or the repository's address.</param>
/// <param name="Path">For Git, the file's path in the repository.</param>
/// <param name="Reference">For Git, the branch, tag or commit; null for main or master.</param>
/// <param name="Sha256">For a URL, the SHA-256 its text must have, from <c>#sha256=…</c>: a pin, trusted without a signature.</param>
public sealed record PipelineAddress(PipelineOrigin Origin, string Location, string? Path = null, string? Reference = null, string? Sha256 = null)
{
    /// <summary>Reads an address: a file, <c>-</c>, a URL, or <c>git+</c> and a repository, <c>#</c> and a path, then <c>@</c> and a ref.</summary>
    /// <exception cref="FormatException">A git+ address without a file.</exception>
    public static PipelineAddress Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address == "-")
            return new PipelineAddress(PipelineOrigin.Stdin, "stdin");
        if (address.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            var rest = address[4..];
            var hash = rest.LastIndexOf('#');
            if (hash < 0 || hash == rest.Length - 1)
                throw new FormatException($"A pipeline in Git is written as git+https://host/owner/repo.git#path/file.meep@ref, with the file after #. {address} has none.");
            var file = rest[(hash + 1)..];
            string? reference = null;
            var at = file.LastIndexOf('@');
            if (at > 0)
            {
                reference = file[(at + 1)..];
                file = file[..at];
            }
            var repository = rest[..hash];
            if (Uri.TryCreate(repository, UriKind.Absolute, out var uri) && uri.IsFile)
                repository = uri.LocalPath;
            return new PipelineAddress(PipelineOrigin.Git, repository, file.TrimStart('/'), reference);
        }
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var hash = address.IndexOf("#sha256=", StringComparison.OrdinalIgnoreCase);
            return hash < 0
                ? new PipelineAddress(PipelineOrigin.Url, address)
                : new PipelineAddress(PipelineOrigin.Url, address[..hash], Sha256: address[(hash + "#sha256=".Length)..].ToLowerInvariant());
        }
        return new PipelineAddress(PipelineOrigin.File, System.IO.Path.GetFullPath(address));
    }

    /// <summary>The pipeline's name: its file's name without <c>.meep</c>.</summary>
    public string Name => Origin switch
    {
        PipelineOrigin.Stdin => "stdin",
        PipelineOrigin.Git => System.IO.Path.GetFileNameWithoutExtension(Path!),
        PipelineOrigin.Url => System.IO.Path.GetFileNameWithoutExtension(new Uri(Location).AbsolutePath) is { Length: > 0 } name ? name : "pipeline",
        _ => System.IO.Path.GetFileNameWithoutExtension(Location),
    };

    /// <summary>Whether it's a file on this machine, which edits can be saved to and which can be watched.</summary>
    public bool IsFile => Origin == PipelineOrigin.File;

    /// <summary>The address as it's written.</summary>
    public override string ToString() => Origin switch
    {
        PipelineOrigin.Stdin => "-",
        PipelineOrigin.Git => $"git+{Location}#{Path}{(Reference is null ? "" : "@" + Reference)}",
        PipelineOrigin.Url => Sha256 is null ? Location : $"{Location}#sha256={Sha256}",
        _ => Location,
    };
}

/// <summary>
/// Reads pipelines from wherever their address says (goal 7): a file, standard input, a URL or Git. One from a URL or
/// Git must be signed by a key you trust (<c>file.meep.sig</c> beside it, from <c>meep sign</c>), or pinned: a URL by
/// <c>#sha256=</c>, a Git file by its commit's full SHA. Its includes come from the same place, and need the same,
/// unless the <c>#include</c> line pins them with <c>sha256:</c> (Q136).
/// </summary>
/// <remarks>
/// A URL must be https://, or http:// on this machine (Q135), where developer mode runs an unsigned pipeline too
/// (Q133). A Git commit signed with SSH by a key you trust covers every file in it, as a pinned one does (Q134).
/// </remarks>
public static class PipelineSources
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Reads a pipeline, checking it can be trusted.</summary>
    /// <param name="address">Where it is.</param>
    /// <param name="plugins">Meep's folder, for its cache and the keys you trust.</param>
    /// <param name="stdin">Standard input, for <c>-</c>.</param>
    /// <exception cref="PipelineSourceException">It couldn't be read, or isn't signed or pinned as it must be.</exception>
    public static async Task<PipelineText> ReadAsync(PipelineAddress address, PluginOptions plugins, TextReader? stdin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(plugins);
        switch (address.Origin)
        {
            case PipelineOrigin.File:
                return File.Exists(address.Location)
                    ? new PipelineText(address.Location, await File.ReadAllTextAsync(address.Location, cancellationToken).ConfigureAwait(false))
                    : throw new PipelineSourceException($"There's no file {address.Location}.");
            case PipelineOrigin.Stdin:
                return new PipelineText("stdin", stdin is null ? "" : await stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            case PipelineOrigin.Url:
            {
                Fetchable(address.Location);
                var bytes = await GetAsync(address.Location, cancellationToken).ConfigureAwait(false)
                    ?? throw new PipelineSourceException($"There's no pipeline at {address.Location}.");
                string because;
                if (address.Sha256 is { } pin)
                {
                    var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    because = sha == pin ? $"its SHA-256 matches the pin {pin[..12]}…"
                        : throw new PipelineSourceException($"{address.Location} has the SHA-256 {sha}, not the {pin} its address pins, so it has changed.");
                }
                else
                {
                    because = UrlTrust(bytes, address.Location, await GetTextAsync(PipelineSignature.FileFor(address.Location), cancellationToken).ConfigureAwait(false), plugins);
                }
                return new PipelineText(address.Location, Encoding.UTF8.GetString(bytes)) { Includes = new UrlIncludes(plugins), TrustedBecause = because };
            }
            case PipelineOrigin.Git:
            {
                GitSnapshot snapshot;
                try
                {
                    snapshot = await Task.Run(() => GitSnapshot.Fetch(address.Location, address.Reference, plugins.Cache), cancellationToken).ConfigureAwait(false);
                }
                catch (GitSourceException exception)
                {
                    throw new PipelineSourceException(exception.Message, exception);
                }
                var includes = new GitIncludes(snapshot, plugins, CommitTrust(snapshot, plugins, out var whyNot), whyNot);
                var path = includes.Address(address.Path!);
                byte[] bytes;
                try
                {
                    bytes = includes.Read(path, pinned: false) ?? throw new PipelineSourceException($"{address.Location} has no {address.Path} at {snapshot.Commit[..7]}.");
                }
                catch (IncludeException exception)
                {
                    throw new PipelineSourceException(exception.Message, exception);
                }
                return new PipelineText(path, Encoding.UTF8.GetString(bytes)) { Includes = includes, TrustedBecause = includes.LastTrust };
            }
            default:
                throw new PipelineSourceException($"A pipeline can't be read from {address.Origin}.");
        }
    }

    // Who signed it, or why it can't be trusted.
    private static string Verified(byte[] text, string? signature, PluginOptions plugins, string what, string? more = null)
    {
        try
        {
            var signer = PipelineSignature.Verify(text, signature, plugins.TrustedKeys(), what);
            return $"it's signed by {signer.Name}";
        }
        catch (PluginException exception)
        {
            throw new PipelineSourceException(
                $"{exception.Message}{(more is null ? "" : " " + more)} A pipeline from a URL or Git needs a signature from a key you trust, made with meep sign, or an address that pins it: #sha256= for a URL, a commit's full SHA for Git.",
                exception);
        }
    }

    // Why a file from a URL can be trusted: its signature, or for one on this machine in developer mode, nothing (Q133).
    private static string UrlTrust(byte[] text, string url, string? signature, PluginOptions plugins)
    {
        var local = new Uri(url).IsLoopback;
        if (signature is null && local && plugins.DeveloperMode)
            return "it's on this machine, and developer mode is on";
        return Verified(text, signature, plugins, url, local && !plugins.DeveloperMode ? "As it's on this machine, --dev would run it unsigned." : null);
    }

    // The key you trust that signed the commit, or null with why not (Q134).
    private static PublicKey? CommitTrust(GitSnapshot snapshot, PluginOptions plugins, out string? whyNot)
    {
        whyNot = null;
        if (snapshot.Pinned)
            return null;
        try
        {
            if (snapshot.Signature() is not { } signed)
                return null;
            return PipelineSignature.VerifyCommit(signed.SignedData, signed.Signature, plugins.TrustedKeys(), out whyNot);
        }
        catch (GitSourceException exception)
        {
            whyNot = exception.Message;
            return null;
        }
    }

    // Only https://, or http:// on this machine (Q135).
    private static void Fetchable(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new PipelineSourceException($"Meep fetches pipelines over HTTPS, so it won't fetch {url}: http:// is only for this machine (localhost). Use https://.");
    }

    private static async Task<byte[]?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s_http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new PipelineSourceException($"Couldn't fetch {url}: {exception.Message}", exception);
        }
    }

    private static async Task<string?> GetTextAsync(string url, CancellationToken cancellationToken) =>
        await GetAsync(url, cancellationToken).ConfigureAwait(false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    // Includes beside a pipeline from a URL, each signed by a key you trust, unless its #include line pins it.
    private sealed class UrlIncludes(PluginOptions plugins) : IIncludeReader
    {
        public string Resolve(string included, string from) => included == from ? from : new Uri(new Uri(from), included).ToString();

        public byte[]? Read(string address, bool pinned)
        {
            try
            {
                Fetchable(address);
                var bytes = GetAsync(address, CancellationToken.None).GetAwaiter().GetResult();
                if (bytes is not null && !pinned)
                    UrlTrust(bytes, address, GetTextAsync(PipelineSignature.FileFor(address), CancellationToken.None).GetAwaiter().GetResult(), plugins);
                return bytes;
            }
            catch (PipelineSourceException exception)
            {
                throw new IncludeException(exception.Message, exception);
            }
        }
    }

    // Files from the same commit as a pipeline in Git: trusted with it if the commit is pinned or signed by a key you
    // trust, or the #include line pins the file; otherwise each signed.
    private sealed class GitIncludes(GitSnapshot snapshot, PluginOptions plugins, PublicKey? commitSigner, string? commitWhyNot) : IIncludeReader
    {
        private readonly string _prefix = $"git+{snapshot.Url}#";

        public string? LastTrust { get; private set; }

        public string Address(string path) => $"{_prefix}{path}@{snapshot.Commit[..12]}";

        public string Resolve(string included, string from)
        {
            if (included == from)
                return from;
            var folder = System.IO.Path.GetDirectoryName(PathOf(from))?.Replace('\\', '/') ?? "";
            var combined = new Uri(new Uri("meep://repository/" + (folder.Length > 0 ? folder + "/" : "")), included).AbsolutePath.TrimStart('/');
            return Address(combined);
        }

        public byte[]? Read(string address, bool pinned)
        {
            var path = PathOf(address);
            var bytes = snapshot.ReadBytes(path);
            if (bytes is null)
                return null;
            if (snapshot.Pinned)
                LastTrust = $"its commit {snapshot.Commit[..12]} is pinned";
            else if (commitSigner is not null)
                LastTrust = $"its commit {snapshot.Commit[..12]} is signed by {commitSigner.Name}";
            else if (pinned)
                LastTrust = "the file that includes it pins it";
            else
            {
                try
                {
                    LastTrust = Verified(bytes, snapshot.Read(PipelineSignature.FileFor(path)), plugins, address, commitWhyNot);
                }
                catch (PipelineSourceException exception)
                {
                    throw new IncludeException(exception.Message, exception);
                }
            }
            return bytes;
        }

        private string PathOf(string address) =>
            address.StartsWith(_prefix, StringComparison.Ordinal) ? address[_prefix.Length..address.LastIndexOf('@')] : address;
    }
}

/// <summary>Why a pipeline couldn't be read, in words.</summary>
public sealed class PipelineSourceException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The bootstrap's first stage for a pipeline from anywhere (goal 7): reads it from its address, checks it can be
/// trusted, and emits it as <see cref="PipelineText"/>, whose includes come from the same place. One that can't be read
/// or trusted becomes an error saying why.
/// </summary>
public sealed class ReadPipeline(PipelineAddress address, PluginOptions plugins, TextReader? stdin = null, string? text = null) : ASource
{
    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (text is not null)
        {
            context.Emit(new PipelineText(address.Location, text)); // sent over the socket: it's yours, as a file is
            return;
        }
        try
        {
            context.Emit(await PipelineSources.ReadAsync(address, plugins, stdin, cancellationToken).ConfigureAwait(false));
        }
        catch (PipelineSourceException exception)
        {
            context.EmitError(exception.Message, exception);
        }
    }
}
