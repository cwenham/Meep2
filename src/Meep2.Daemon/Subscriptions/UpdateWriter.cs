using System.Buffers;
using System.Formats.Cbor;
using System.Text.Json;

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

/// <summary>Events as JSON text.</summary>
internal sealed class JsonUpdateWriter(Utf8JsonWriter writer) : UpdateWriter
{
    public override void StartMap(int? count = null) => writer.WriteStartObject();

    public override void EndMap() => writer.WriteEndObject();

    public override void StartArray(int? count = null) => writer.WriteStartArray();

    public override void EndArray() => writer.WriteEndArray();

    public override void Name(string name) => writer.WritePropertyName(name);

    public override void Number(long value) => writer.WriteNumberValue(value);

    public override void Number(double value)
    {
        if (double.IsFinite(value))
            writer.WriteNumberValue(value);
        else
            writer.WriteNullValue();
    }

    public override void String(string? value)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }

    public override void Bool(bool value) => writer.WriteBooleanValue(value);

    public override void Null() => writer.WriteNullValue();
}

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

/// <summary>How a subscription's events go on the wire.</summary>
internal static class Envelope
{
    // Link keys have > in them, which the default escapes as \u003E: six bytes for one, a thousand times an update.
    // Nothing reads these frames as HTML, so only what JSON itself needs is escaped.
    private static readonly JsonWriterOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// One event as a JSON frame, as the socket's other streams send them: <c>{"id": …, "event": …, "data": …}</c>.
    /// </summary>
    public static byte[] Json(string id, string name, Action<UpdateWriter> data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Relaxed))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteRawValue(id, skipInputValidation: true);
            writer.WriteString("event", name);
            writer.WritePropertyName("data");
            data(new JsonUpdateWriter(writer));
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The same event as a CBOR map with the same three keys, for a binary frame.</summary>
    public static byte[] Cbor(string id, string name, Action<UpdateWriter> data)
    {
        var writer = new CborWriter(CborConformanceMode.Lax, convertIndefiniteLengthEncodings: false);
        writer.WriteStartMap(3);
        writer.WriteTextString("id");
        WriteId(writer, id);
        writer.WriteTextString("event");
        writer.WriteTextString(name);
        writer.WriteTextString("data");
        data(new CborUpdateWriter(writer));
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>A CBOR frame as the JSON it stands for, as a client in .NET can read it.</summary>
    public static JsonElement ToJson(ReadOnlyMemory<byte> cbor)
    {
        var reader = new CborReader(cbor, CborConformanceMode.Lax);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Relaxed))
            Copy(reader, writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Copy(CborReader reader, Utf8JsonWriter writer)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                writer.WriteStartObject();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    writer.WritePropertyName(reader.ReadTextString());
                    Copy(reader, writer);
                }
                reader.ReadEndMap();
                writer.WriteEndObject();
                break;
            case CborReaderState.StartArray:
                reader.ReadStartArray();
                writer.WriteStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                    Copy(reader, writer);
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;
            case CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger:
                writer.WriteNumberValue(reader.ReadInt64());
                break;
            case CborReaderState.HalfPrecisionFloat or CborReaderState.SinglePrecisionFloat or CborReaderState.DoublePrecisionFloat:
                writer.WriteNumberValue(reader.ReadDouble());
                break;
            case CborReaderState.TextString:
                writer.WriteStringValue(reader.ReadTextString());
                break;
            case CborReaderState.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;
            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;
            default:
                throw new FormatException($"A subscription doesn't send CBOR's {reader.PeekState()}.");
        }
    }

    // The request's id as it was given: a string or a number.
    private static void WriteId(CborWriter writer, string id)
    {
        using var document = JsonDocument.Parse(id);
        var value = document.RootElement;
        if (value.ValueKind == JsonValueKind.String)
            writer.WriteTextString(value.GetString()!);
        else if (value.TryGetInt64(out var whole))
            writer.WriteInt64(whole);
        else
            writer.WriteDouble(value.GetDouble());
    }
}
