namespace Meep2.Tests.Support;

/// <summary>A folder of its own for a test's files, deleted with them when disposed.</summary>
public sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"meep-test-{Guid.NewGuid():N}");

    /// <summary>Writes a file in the folder and returns its full path.</summary>
    public string Write(string name, string text)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes a file in the folder and returns its full path.</summary>
    public string Write(string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Something still has a file open; the system cleans its temporary folder in time.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
