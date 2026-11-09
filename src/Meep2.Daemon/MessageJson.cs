using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>
/// Writes any message as JSON for people to look at, as a debug tap shows it: its type, identity, leases, nearest
/// ancestors and content. It's for display only; sending messages to another process is the pipeline author's job
/// (Q38), and this can't be read back.
/// </summary>
/// <remarks>
/// <para>
/// Content is the message type's public properties, read by reflection. Long text is cut short, bytes are shown as a
/// count, collections show their first items, and nesting stops after a few levels. A property that throws shows
/// why. Reading a property never reads a streamed body, which only <see cref="IHasBody.OpenBody"/> does.
/// </para>
/// <para>
/// The design (section 4 of round 3) has a source generator write these instead, which would keep it working in an
/// AOT build. Reflection does for now, since the daemon runs on the JIT to load plugins.
/// </para>
/// </remarks>
public static class MessageJson
{
    private const int MaxText = 4096;
    private const int MaxItems = 100;
    private const int MaxDepth = 4;
    private const int MaxAncestors = 10;

    // Weak, so a plugin's message types don't keep the plugin loaded.
    private static readonly ConditionalWeakTable<Type, PropertyInfo[]> s_properties = new();

    /// <summary>The message as JSON text.</summary>
    public static string ToJson(Message message, bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
            Write(writer, message);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the message as a JSON object.</summary>
    public static void Write(Utf8JsonWriter writer, Message message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        writer.WriteStartObject();
        WriteIdentity(writer, message);
        if (message.Expires is { } expires)
            writer.WriteString("expires", expires.ToString());

        var leases = message.Leases.ToList();
        if (leases.Count > 0)
        {
            writer.WriteStartArray("leases");
            foreach (var lease in leases)
                writer.WriteStringValue(lease.ToString());
            writer.WriteEndArray();
        }

        writer.WriteStartObject("content");
        foreach (var property in PropertiesOf(message.GetType()))
        {
            writer.WritePropertyName(property.Name);
            object? value;
            try
            {
                value = property.GetValue(message);
            }
            catch (Exception exception)
            {
                writer.WriteStringValue($"(couldn't read it: {(exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message})");
                continue;
            }
            WriteValue(writer, value, depth: 1);
        }
        writer.WriteEndObject();

        var ancestors = Ancestors(message);
        if (ancestors.Count > 0)
        {
            writer.WriteStartArray("fam");
            foreach (var ancestor in ancestors)
            {
                writer.WriteStartObject();
                WriteIdentity(writer, ancestor);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, Message message)
    {
        writer.WriteString("type", message.GetType().Name);
        writer.WriteString("id", message.Id);
        writer.WriteString("created", message.Created.ToString("O", CultureInfo.InvariantCulture));
        if (message.EmittedBy is { } emitter)
            writer.WriteString("emittedBy", emitter.ToString());
    }

    // The nearest ancestors, breadth first, as fam searches them.
    private static List<Message> Ancestors(Message message)
    {
        var found = new List<Message>();
        var seen = new HashSet<Message>(ReferenceEqualityComparer.Instance) { message };
        var waiting = new Queue<Message>(message.Parents);
        while (found.Count < MaxAncestors && waiting.TryDequeue(out var next))
        {
            if (!seen.Add(next))
                continue;
            found.Add(next);
            foreach (var parent in next.Parents)
                waiting.Enqueue(parent);
        }
        return found;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text.Length > MaxText ? string.Concat(text.AsSpan(0, MaxText), "…") : text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case int or long or short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case double or float:
                var real = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(real))
                    writer.WriteNumberValue(real);
                else
                    writer.WriteStringValue(real.ToString(CultureInfo.InvariantCulture));
                return;
            case DateTime time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                return;
            case Guid or TimeSpan or Enum or ModuleInfo or Lease or Uri or IFormattable:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case ReadOnlyMemory<byte> bytes:
                writer.WriteStringValue($"{bytes.Length} bytes");
                return;
            case byte[] bytes:
                writer.WriteStringValue($"{bytes.Length} bytes");
                return;
            case Message message:
                writer.WriteStartObject();
                WriteIdentity(writer, message);
                writer.WriteEndObject();
                return;
            case Exception exception:
                writer.WriteStartObject();
                writer.WriteString("type", exception.GetType().Name);
                writer.WriteString("message", exception.Message);
                writer.WriteEndObject();
                return;
            case Delegate or Stream or Task:
                writer.WriteStringValue($"({value.GetType().Name})");
                return;
        }

        if (depth >= MaxDepth)
        {
            writer.WriteStringValue(value.ToString());
            return;
        }
        if (value is IEnumerable items)
        {
            if (Pairs(value) is { } pairs)
            {
                writer.WriteStartObject();
                foreach (var (key, item) in pairs.Take(MaxItems))
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item, depth + 1);
                }
                writer.WriteEndObject();
                return;
            }
            writer.WriteStartArray();
            var count = 0;
            foreach (var item in items)
            {
                if (count++ == MaxItems)
                {
                    writer.WriteStringValue("…");
                    break;
                }
                WriteValue(writer, item, depth + 1);
            }
            writer.WriteEndArray();
            return;
        }

        // Anything else: its public properties, or its text if it has none.
        var properties = PropertiesOf(value.GetType());
        if (properties.Length == 0)
        {
            writer.WriteStringValue(value.ToString());
            return;
        }
        writer.WriteStartObject();
        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Name);
            try
            {
                WriteValue(writer, property.GetValue(value), depth + 1);
            }
            catch (Exception exception)
            {
                writer.WriteStringValue($"(couldn't read it: {(exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message})");
            }
        }
        writer.WriteEndObject();
    }

    // A dictionary with string keys, as key-value pairs, or null for any other collection.
    private static IEnumerable<(string Key, object? Value)>? Pairs(object value)
    {
        if (value is IDictionary dictionary)
            return Entries(dictionary);
        var pairType = value.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && i.GetGenericArguments()[0] is { IsGenericType: true } item
                && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && item.GetGenericArguments()[0] == typeof(string))
            ?.GetGenericArguments()[0];
        if (pairType is null)
            return null;
        var key = pairType.GetProperty("Key")!;
        var itemValue = pairType.GetProperty("Value")!;
        return ((IEnumerable)value).Cast<object>().Select(pair => ((string)key.GetValue(pair)!, itemValue.GetValue(pair)));
    }

    private static IEnumerable<(string Key, object? Value)> Entries(IDictionary dictionary)
    {
        var entries = dictionary.GetEnumerator();
        while (entries.MoveNext())
            yield return (Convert.ToString(entries.Key, CultureInfo.InvariantCulture) ?? "", entries.Value);
    }

    // A type's public instance properties, leaving out the ones every message has, which are written separately.
    private static PropertyInfo[] PropertiesOf(Type type) =>
        s_properties.GetValue(type, static type => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0
                && property.GetMethod is { IsPublic: true }
                && property.DeclaringType != typeof(Message)
                && property.PropertyType != typeof(Family))
            .OrderBy(property => property.MetadataToken)]);
}
