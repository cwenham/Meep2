namespace Meep2.SDK.Messages;

/// <summary>A message whose content can be read as text, such as an HTTP response or a file.</summary>
public interface IHasText
{
    /// <summary>The content as text.</summary>
    string Text { get; }
}

/// <summary>A message with a body of bytes, such as an HTTP request or response.</summary>
/// <remarks>
/// Bodies are read into memory by default, up to a size limit. A streamed body is left unread instead, so a large
/// one can be passed on without holding it all in memory, and only one module can read it (Q51).
/// </remarks>
public interface IHasBody
{
    /// <summary>The body's media type, such as <c>application/json</c>, if it's known.</summary>
    string? ContentType { get; }

    /// <summary>Whether the body was left unread, to be read once.</summary>
    bool IsStreamed { get; }

    /// <summary>
    /// Opens the body to read. A body in memory can be opened any number of times. A streamed body can only be
    /// opened once, by one module; after that this throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Stream OpenBody();
}

/// <summary>A message about a file, such as a file's contents.</summary>
public interface IHasPath
{
    /// <summary>The file's path.</summary>
    string Path { get; }
}

/// <summary>
/// A message whose members are only known when it arrives, such as a generic JSON message. Meeplang reads its
/// members by name for each message, instead of checking them when the pipeline loads.
/// </summary>
public interface IHasDynamicMembers
{
    /// <summary>Reads a member by name. Returns false if the message has no member with that name.</summary>
    bool TryGetMember(string name, out object? value);
}
