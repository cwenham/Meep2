namespace Meep2.Modules;

/// <summary>How <see cref="Save"/> writes to its file.</summary>
public enum SaveMode
{
    /// <summary>Add each message to the end of the file, followed by a new line.</summary>
    Append,

    /// <summary>Replace the file with each message.</summary>
    Overwrite,
}
