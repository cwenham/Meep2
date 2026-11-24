using System.Security.Cryptography;
using System.Text;
using Meep2.Git;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Plugins;
using Meep2.Security;
using Meep2.Shared;

namespace Meep2.Daemon;

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
    // How long a pipeline, an include or a signature from a URL has to arrive.
    private static readonly TimeSpan s_downloadLimit = TimeSpan.FromSeconds(30);

    /// <summary>Reads a pipeline, checking it can be trusted.</summary>
    /// <param name="address">Where it is.</param>
    /// <param name="plugins">Meep's folder, for its cache and the keys you trust.</param>
    /// <param name="stdin">Standard input, for <c>-</c>.</param>
    /// <exception cref="PipelineSourceException">It couldn't be read, or isn't signed or pinned as it must be.</exception>
    public static Task<PipelineText> ReadAsync(PipelineAddress address, PluginOptions plugins, TextReader? stdin, CancellationToken cancellationToken) =>
        ReadAsync(address, plugins, stdin, s_downloadLimit, cancellationToken);

    /// <summary>Reads a pipeline, giving a file from a URL <paramref name="downloadLimit"/> to arrive, rather than 30 seconds.</summary>
    internal static async Task<PipelineText> ReadAsync(PipelineAddress address, PluginOptions plugins, TextReader? stdin, TimeSpan downloadLimit, CancellationToken cancellationToken)
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
                var bytes = await GetAsync(address.Location, downloadLimit, cancellationToken).ConfigureAwait(false)
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
                    because = UrlTrust(bytes, address.Location, await GetTextAsync(PipelineSignature.FileFor(address.Location), downloadLimit, cancellationToken).ConfigureAwait(false), plugins);
                }
                var text = Encoding.UTF8.GetString(bytes);
                var includes = await FetchedIncludes.FetchAsync(new SourceFile(address.Location, text), new UrlIncludes(plugins, downloadLimit), cancellationToken).ConfigureAwait(false);
                return new PipelineText(address.Location, text) { Includes = includes, TrustedBecause = because };
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

    /// <summary>The largest pipeline, include or signature fetched from a URL (S5): 4 MiB.</summary>
    internal const int MaxDownload = 4 * 1024 * 1024;

    // Reads a file from a URL, up to the limit, or null if it isn't there. One that takes longer than the time it has
    // fails saying so, rather than with a bare cancellation (Q243).
    private static async Task<byte[]?> GetAsync(string url, TimeSpan downloadLimit, CancellationToken cancelled)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancelled);
        limit.CancelAfter(downloadLimit);
        var cancellationToken = limit.Token;
        try
        {
            using var response = await MeepHttp.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownload)
                throw TooLarge(url);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxDownload)
                    throw TooLarge(url);
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException exception)
        {
            throw new PipelineSourceException($"Couldn't fetch {url}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancelled.IsCancellationRequested)
        {
            throw new PipelineSourceException($"{url} didn't arrive within {DurationText.Write(downloadLimit)}.", exception);
        }
    }

    private static PipelineSourceException TooLarge(string url) =>
        new($"{url} is larger than {MaxDownload / (1024 * 1024)} MiB, the most Meep reads for a pipeline, an include or a signature, so it isn't read.");

    private static async Task<string?> GetTextAsync(string url, TimeSpan downloadLimit, CancellationToken cancellationToken) =>
        await GetAsync(url, downloadLimit, cancellationToken).ConfigureAwait(false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    // Includes beside a pipeline from a URL, each signed by a key you trust, unless its #include line pins it. They're
    // fetched before the pipeline is parsed (P1), so nothing waits on the network while holding a thread.
    private sealed class UrlIncludes(PluginOptions plugins, TimeSpan downloadLimit) : IIncludeFetcher
    {
        public string Resolve(string included, string from) => included == from ? from : new Uri(new Uri(from), included).ToString();

        public async Task<byte[]?> FetchAsync(string address, bool pinned, CancellationToken cancellationToken)
        {
            try
            {
                Fetchable(address);
                var bytes = await GetAsync(address, downloadLimit, cancellationToken).ConfigureAwait(false);
                if (bytes is not null && !pinned)
                    UrlTrust(bytes, address, await GetTextAsync(PipelineSignature.FileFor(address), downloadLimit, cancellationToken).ConfigureAwait(false), plugins);
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
