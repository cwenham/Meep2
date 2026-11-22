using System.Formats.Cbor;

namespace Meep2.Daemon.Subscriptions;

/// <summary>Events as CBOR (RFC 8949), with text keys as in the JSON.</summary>
internal sealed class CborUpdateWriter(CborWriter writer) : UpdateWriter
{
    public override void StartMap(int? count = null) => writer.WriteStartMap(count);

    public override void EndMap() => writer.WriteEndMap();

    public override void StartArray(int? count = null) => writer.WriteStartArray(count);

    public override void EndArray() => writer.WriteEndArray();

    public override void Name(string name) => writer.WriteTextString(name);

    public override void Number(long value) => writer.WriteInt64(value);

    public override void Number(double value)
    {
        // Whole numbers go as integers, which take fewer bytes; others as the smallest float that holds them exactly.
        if (double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) < 1e15)
            writer.WriteInt64((long)value);
        else if (!double.IsFinite(value))
            writer.WriteNull();
        else if ((float)value == value)
            writer.WriteSingle((float)value);
        else
            writer.WriteDouble(value);
    }

    public override void String(string? value)
    {
        if (value is null)
            writer.WriteNull();
        else
            writer.WriteTextString(value);
    }

    public override void Bool(bool value) => writer.WriteBoolean(value);

    public override void Null() => writer.WriteNull();
}
