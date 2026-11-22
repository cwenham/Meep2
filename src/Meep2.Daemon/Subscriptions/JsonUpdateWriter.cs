using System.Text.Json;

namespace Meep2.Daemon.Subscriptions;

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
