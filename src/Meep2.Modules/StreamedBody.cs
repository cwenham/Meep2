namespace Meep2.Modules;

/// <summary>A body left unread, which one module can read (Q51).</summary>
internal sealed class StreamedBody(Stream stream, string what)
{
    private int _taken;

    public Stream Open() =>
        Interlocked.Exchange(ref _taken, 1) == 0
            ? stream
            : throw new InvalidOperationException(
                $"The {what} was streamed, and another module has already read it. Only one module can read a streamed body; leave out stream=true to read it into memory instead.");
}
