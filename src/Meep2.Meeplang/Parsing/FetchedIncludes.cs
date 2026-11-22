using System.Runtime.ExceptionServices;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Parsing;

/// <summary>
/// A pipeline's includes, fetched before it's parsed (P1), so the parser, which reads them as it goes, never waits on
/// the network. Each file is fetched as the parser would read it, in the same order, and the parser is given what
/// came back, or why it couldn't be used.
/// </summary>
public sealed class FetchedIncludes : IIncludeReader
{
    private readonly IIncludeFetcher _fetcher;
    private readonly Dictionary<(string Address, bool Pinned), Fetched> _fetched = [];

    private FetchedIncludes(IIncludeFetcher fetcher) => _fetcher = fetcher;

    /// <summary>Fetches every file a pipeline includes, and what they include, as the parser will want them.</summary>
    /// <param name="source">The pipeline's own file.</param>
    /// <param name="fetcher">Finds and fetches its includes.</param>
    /// <param name="cancellationToken">Stops fetching.</param>
    public static async Task<FetchedIncludes> FetchAsync(SourceFile source, IIncludeFetcher fetcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fetcher);
        var includes = new FetchedIncludes(fetcher);
        await includes.FetchFromAsync(new LineParser(source).Parse(), cancellationToken).ConfigureAwait(false);
        return includes;
    }

    /// <inheritdoc/>
    public string Resolve(string included, string from) => _fetcher.Resolve(included, from);

    /// <inheritdoc/>
    public byte[]? Read(string address, bool pinned)
    {
        if (!_fetched.TryGetValue((address, pinned), out var fetched))
            throw new IncludeException($"{address} wasn't fetched with the pipeline.");
        if (fetched.Failed is { } failed)
            ExceptionDispatchInfo.Throw(failed);
        return fetched.Bytes;
    }

    // Fetches what a file includes, then what each of those includes, skipping any it has already fetched. The parser
    // refuses loops and files that can't be used, so a file that failed or doesn't match its pin isn't looked into.
    private async Task FetchFromAsync(PipelineSyntax from, CancellationToken cancellationToken)
    {
        foreach (var include in from.Includes)
        {
            string address;
            try
            {
                address = _fetcher.Resolve(include.Path, from.Source.Path);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue; // the parser meets the same error when it resolves it
            }
            var key = (Address: address, Pinned: include.Sha256 is not null);
            if (_fetched.ContainsKey(key))
                continue;
            byte[]? bytes;
            try
            {
                bytes = await _fetcher.FetchAsync(address, key.Pinned, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IncludeException)
            {
                _fetched[key] = new Fetched(null, exception);
                continue;
            }
            _fetched[key] = new Fetched(bytes, null);
            if (bytes is not null && MeeplangParser.PinMismatch(bytes, include) is null)
                await FetchFromAsync(new LineParser(new SourceFile(address, MeeplangParser.Text(bytes)), isInclude: true).Parse(), cancellationToken).ConfigureAwait(false);
        }
    }

    // What fetching a file gave: its bytes (null if there's no such file), or why it can't be used.
    private sealed record Fetched(byte[]? Bytes, Exception? Failed);
}
