namespace Meep2.Meeplang.Parsing;

/// <summary>
/// Finds and fetches the files <c>#include</c> lines name from somewhere that takes time to answer, such as a URL, so
/// <see cref="FetchedIncludes"/> can fetch them all before the pipeline is parsed, without blocking a thread.
/// </summary>
public interface IIncludeFetcher
{
    /// <summary>The included file's address, from the line as written and the address of the file that includes it.</summary>
    string Resolve(string included, string from);

    /// <summary>The file's bytes, or null if there's no such file.</summary>
    /// <param name="address">The file, as <see cref="Resolve"/> found it.</param>
    /// <param name="pinned">Whether the <c>#include</c> line pins the file with its SHA-256, as for <see cref="IIncludeReader.Read"/>.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <exception cref="IncludeException">The file can't be used, such as one that isn't signed by a key you trust.</exception>
    Task<byte[]?> FetchAsync(string address, bool pinned, CancellationToken cancellationToken);
}
