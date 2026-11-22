using System.Buffers;

namespace Meep2.Daemon;

/// <summary>
/// A buffer to write a frame or an answer into, kept for each thread and used again (E3), rather than a new one that
/// grows, doubling, for every frame. Rent one, write, copy out or send what was written, then return it.
/// </summary>
internal static class ScratchBuffer
{
    // Larger buffers are let go rather than kept, so one big answer doesn't hold its memory for good.
    private const int MostKept = 1024 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_kept;

    /// <summary>An empty buffer: this thread's, unless it's in use, as it is if a write rents another inside it.</summary>
    public static ArrayBufferWriter<byte> Rent()
    {
        var buffer = t_kept ?? new ArrayBufferWriter<byte>(4096);
        t_kept = null;
        buffer.ResetWrittenCount();
        return buffer;
    }

    /// <summary>Gives a buffer back, on whichever thread is done with it, once nothing reads what was written.</summary>
    public static void Return(ArrayBufferWriter<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Capacity <= MostKept)
            t_kept = buffer;
    }
}
