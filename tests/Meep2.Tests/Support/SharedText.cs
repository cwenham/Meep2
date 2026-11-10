using System.Text;

namespace Meep2.Tests.Support;

/// <summary>
/// A <see cref="StringWriter"/> that can be read while pipelines write to it from other threads: each write, and
/// reading what's been written, takes one lock. Wrap it in <see cref="TextWriter.Synchronized"/> too, so a whole
/// line is written at once and lines from parallel branches don't mix.
/// </summary>
public sealed class SharedText : StringWriter
{
    private readonly Lock _gate = new();

    public override void Write(char value)
    {
        lock (_gate)
            base.Write(value);
    }

    public override void Write(string? value)
    {
        lock (_gate)
            base.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
            base.Write(buffer, index, count);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
            base.Write(buffer);
    }

    public override void Write(StringBuilder? value)
    {
        lock (_gate)
            base.Write(value);
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
            base.WriteLine(buffer);
    }

    public override void WriteLine(StringBuilder? value)
    {
        lock (_gate)
            base.WriteLine(value);
    }

    public override string ToString()
    {
        lock (_gate)
            return base.ToString();
    }
}
