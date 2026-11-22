namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// Writes a subscription's events, as JSON or as CBOR (Q202), from the same code, so both carry the same values in the
/// same shape. Maps and arrays are given their length when it's known, which CBOR writes in their first byte.
/// </summary>
internal abstract class UpdateWriter
{
    public abstract void StartMap(int? count = null);

    public abstract void EndMap();

    public abstract void StartArray(int? count = null);

    public abstract void EndArray();

    public abstract void Name(string name);

    public abstract void Number(long value);

    public abstract void Number(double value);

    public abstract void String(string? value);

    public abstract void Bool(bool value);

    public abstract void Null();

    public void Property(string name, long value)
    {
        Name(name);
        Number(value);
    }

    public void Property(string name, double value)
    {
        Name(name);
        Number(value);
    }

    public void Property(string name, string? value)
    {
        Name(name);
        String(value);
    }

    public void Property(string name, bool value)
    {
        Name(name);
        Bool(value);
    }

    /// <summary>Values that changed, by position, with null for those that didn't, and none after the last that did.</summary>
    public void Changed(ReadOnlySpan<long> now, ReadOnlySpan<long> before, bool all)
    {
        var last = now.Length - 1;
        if (!all)
        {
            while (last >= 0 && now[last] == before[last])
                last--;
        }
        StartArray(last + 1);
        for (var i = 0; i <= last; i++)
        {
            if (all || now[i] != before[i])
                Number(now[i]);
            else
                Null();
        }
        EndArray();
    }
}
