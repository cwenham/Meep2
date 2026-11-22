namespace Meep2.Runtime.Internal;

/// <summary>
/// How many messages a link has carried since the pipeline started, and how many of them were errors. A change to the
/// running pipeline that keeps the link hands the same counter on, so its counts carry on.
/// </summary>
/// <remarks>
/// Both counts share a cache line, which each counter has to itself, so links counted on different threads don't slow each other down. (The
/// runtime ignores <c>StructLayout.Size</c> on a class, so a field at the end of the line makes the room.)
/// </remarks>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
internal sealed class LinkCount
{
    /// <summary>For messages that come in along no link, as an injected one does.</summary>
    public static readonly LinkCount Nowhere = new();

    [System.Runtime.InteropServices.FieldOffset(0)]
    private long _carried;

    [System.Runtime.InteropServices.FieldOffset(8)]
    private long _errors;

#pragma warning disable CS0169 // never used: it only takes up the rest of the cache line
    [System.Runtime.InteropServices.FieldOffset(56)]
    private readonly long _padding;
#pragma warning restore CS0169

    public long Carried => Interlocked.Read(ref _carried);

    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Counts a message that went along the link: a second increment only for an error, which is rare.</summary>
    public void Count(bool isError)
    {
        Interlocked.Increment(ref _carried);
        if (isError)
            Interlocked.Increment(ref _errors);
    }
}
