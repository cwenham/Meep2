namespace Meep2.Runtime.Internal;

/// <summary>
/// The debug taps on one module (<see cref="DebugTap"/>). Taps are added and removed under a lock and kept as an
/// array that's replaced whole, so offering a message reads one field and takes no lock.
/// </summary>
internal sealed class TapList
{
    private readonly Lock _tapping = new();
    private volatile DebugTap[]? _taps;

    /// <summary>
    /// Shows a message the module emitted or passed on to any taps. With none, this is one check of a field, so a
    /// pipeline nobody is watching doesn't slow down.
    /// </summary>
    public void Offer(SDK.Messages.Message message)
    {
        if (_taps is { } taps)
        {
            foreach (var tap in taps)
                tap.Offer(message);
        }
    }

    public void Add(DebugTap tap)
    {
        lock (_tapping)
            _taps = [.. _taps ?? [], tap];
    }

    public void Remove(DebugTap tap)
    {
        lock (_tapping)
        {
            var rest = (_taps ?? []).Where(t => t != tap).ToArray();
            _taps = rest.Length == 0 ? null : rest;
        }
    }

    /// <summary>Ends every tap, as when the pipeline finishes.</summary>
    public void CloseAll()
    {
        DebugTap[]? taps;
        lock (_tapping)
        {
            taps = _taps;
            _taps = null;
        }
        foreach (var tap in taps ?? [])
            tap.Complete();
    }
}
