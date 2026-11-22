using System.Buffers;
using System.Formats.Cbor;
using System.Text.Json;

namespace Meep2.Daemon.Subscriptions;

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
        var buffer = ScratchBuffer.Rent();
        try
        {
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
        finally
        {
            ScratchBuffer.Return(buffer);
        }
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
