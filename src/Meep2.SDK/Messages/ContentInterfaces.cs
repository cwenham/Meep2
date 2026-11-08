namespace Meep2.SDK.Messages;

/// <summary>A message whose content can be read as text, such as an HTTP response or a file.</summary>
public interface IHasText
{
    /// <summary>The content as text.</summary>
    string Text { get; }
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
