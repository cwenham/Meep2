namespace Meep2.Meeplang.Parsing;

/// <summary>
/// Finds and reads the files <c>#include</c> lines name, for a pipeline that isn't on this machine's disk, such as one
/// loaded from a URL or Git, so its includes come from there too, and are checked the same way.
/// </summary>
public interface IIncludeReader
{
    /// <summary>The included file's address, from the line as written and the address of the file that includes it.</summary>
    string Resolve(string included, string from);

    /// <summary>The file's bytes, or null if there's no such file.</summary>
    /// <param name="address">The file, as <see cref="Resolve"/> found it.</param>
    /// <param name="pinned">
    /// Whether the <c>#include</c> line pins the file with its SHA-256, which the parser checks. A pinned file is
    /// trusted as the file that includes it is, so it needs no signature of its own (Q136).
    /// </param>
    /// <exception cref="IncludeException">The file can't be used, such as one that isn't signed by a key you trust.</exception>
    byte[]? Read(string address, bool pinned);
}

/// <summary>An included file can't be used. The message says why.</summary>
public sealed class IncludeException(string message, Exception? innerException = null) : Exception(message, innerException);
