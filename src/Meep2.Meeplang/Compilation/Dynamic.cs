using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Numbers;

namespace Meep2.Meeplang.Compilation;

/// <summary>
/// What compiled expressions call when a value's type is only known at runtime, such as a member of a JSON message.
/// Public only so compiled code can reach it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Dynamic
{
    // Weak on the type, so a plugin's types don't stay loaded because a getter was cached for them.
    private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, Func<object, object?>?>> s_members = new();

    internal static readonly MethodInfo GetMemberMethod = Method(nameof(GetMember));
    internal static readonly MethodInfo IndexMethod = Method(nameof(Index));
    internal static readonly MethodInfo InvokeMethod = Method(nameof(Invoke));
    internal static readonly MethodInfo CompareMethod = Method(nameof(Compare));
    internal static readonly MethodInfo ArithmeticMethod = Method(nameof(Arithmetic));
    internal static readonly MethodInfo NegateMethod = Method(nameof(Negate));
    internal static readonly MethodInfo IsTrueMethod = Method(nameof(IsTrue));
    internal static readonly MethodInfo FormatMethod = Method(nameof(Format));
    internal static readonly MethodInfo ConvertToMethod = Method(nameof(ConvertTo));
    internal static readonly MethodInfo FamMethod = Method(nameof(Fam));
    internal static readonly MethodInfo ConcatMethod = typeof(string).GetMethod(nameof(string.Concat), [typeof(string[])])!;

    /// <summary>Reads a member by name.</summary>
    public static object? GetMember(object? target, string name)
    {
        switch (Normalize(target))
        {
            case null:
                throw new EvaluationException($"Can't read '{name}' from nothing.");
            case IHasDynamicMembers members:
                return members.TryGetMember(name, out var value)
                    ? Normalize(value)
                    : throw new EvaluationException($"There's no '{name}' in this {Describe(target)}.");
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                return json.TryGetProperty(name, out var property)
                    ? Normalize(property)
                    : throw new EvaluationException($"There's no '{name}' in this JSON object.");
            case JsonElement json:
                throw new EvaluationException($"Can't read '{name}' from a JSON {Describe(json)}.");
            case var other:
                var type = other.GetType();
                var getter = s_members.GetValue(type, static _ => new(StringComparer.Ordinal)).GetOrAdd(name, static (member, owner) => FindGetter(owner, member), type);
                return getter is not null
                    ? Normalize(getter(other))
                    : throw new EvaluationException($"{other.GetType().Name} has no '{name}'.");
        }
    }

    /// <summary>Reads an element by position or a member by name: <c>target[index]</c>.</summary>
    public static object? Index(object? target, object? index)
    {
        target = Normalize(target);
        index = Normalize(index);
        switch (target)
        {
            case null:
                throw new EvaluationException("Can't index into nothing.");
            case JsonElement { ValueKind: JsonValueKind.Array } array when Numbers.IsNumber(index):
            {
                var length = array.GetArrayLength();
                var position = Position(index);
                var from = position < 0 ? position + length : position;
                return from >= 0 && from < length
                    ? Normalize(array[from])
                    : throw new EvaluationException($"There's no element {position} in a JSON array of {length}.");
            }
            case JsonElement { ValueKind: JsonValueKind.Object } or IHasDynamicMembers when index is string name:
                return GetMember(target, name);
            case string text when Numbers.IsNumber(index):
            {
                var position = Position(index);
                return position >= 0 && position < text.Length
                    ? text[position].ToString()
                    : throw new EvaluationException($"There's no character {position} in text of length {text.Length}.");
            }
            case IList list when Numbers.IsNumber(index):
            {
                var position = Position(index);
                return position >= 0 && position < list.Count
                    ? Normalize(list[position])
                    : throw new EvaluationException($"There's no element {position} in a list of {list.Count}.");
            }
            case IDictionary dictionary when index is not null:
                return dictionary.Contains(index)
                    ? Normalize(dictionary[index])
                    : throw new EvaluationException($"There's no '{index}' here.");
            default:
                throw new EvaluationException($"Can't index into {Describe(target)} with {Describe(index)}.");
        }
    }

    /// <summary>Calls a method by name: <c>target.Method(arguments)</c>.</summary>
    public static object? Invoke(object? target, string method, object?[] arguments)
    {
        target = Normalize(target) ?? throw new EvaluationException($"Can't call {method} on nothing.");
        for (var i = 0; i < arguments.Length; i++)
            arguments[i] = Normalize(arguments[i]);

        foreach (var candidate in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (candidate.Name != method)
                continue;
            var parameters = candidate.GetParameters();
            if (parameters.Length != arguments.Length)
                continue;
            var converted = new object?[arguments.Length];
            var fits = true;
            for (var i = 0; i < arguments.Length && fits; i++)
                fits = TryConvert(arguments[i], parameters[i].ParameterType, out converted[i]);
            if (fits)
                return Normalize(candidate.Invoke(target, converted));
        }
        throw new EvaluationException($"{Describe(target)} has no method {method} that takes {arguments.Length} value(s).");
    }

    /// <summary>Compares two values with <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or <c>&gt;=</c>.</summary>
    public static bool Compare(string op, object? left, object? right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (op is "==" or "!=")
        {
            var equal = AreEqual(left, right);
            return op == "==" ? equal : !equal;
        }

        int order;
        if (Numbers.IsNumber(left) && Numbers.IsNumber(right))
        {
            // NaN can't be ordered, so every comparison with it is false.
            if (Numbers.Compare(left!, right!) is not { } numeric)
                return false;
            order = numeric;
        }
        else if (left is string a && right is string b)
            order = string.CompareOrdinal(a, b);
        else if (left is not null && right is not null && left.GetType() == right.GetType() && left is IComparable comparable)
            order = comparable.CompareTo(right);
        else
            throw new EvaluationException($"Can't compare {Describe(left)} with {Describe(right)} using {op}.");

        return op switch
        {
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new EvaluationException($"'{op}' isn't a comparison."),
        };
    }

    /// <summary>Applies <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> or <c>%</c>.</summary>
    public static object? Arithmetic(string op, object? left, object? right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (op == "+" && (left is string || right is string))
            return Format(left, null) + Format(right, null);

        if (Numbers.IsNumber(left) && Numbers.IsNumber(right))
            return Numbers.Arithmetic(op, left, right);

        return (op, left, right) switch
        {
            ("+", TimeSpan a, TimeSpan b) => a + b,
            ("-", TimeSpan a, TimeSpan b) => a - b,
            ("+", DateTime a, TimeSpan b) => a + b,
            ("-", DateTime a, TimeSpan b) => a - b,
            ("-", DateTime a, DateTime b) => a - b,
            _ => throw new EvaluationException($"Can't use {op} with {Describe(left)} and {Describe(right)}."),
        };
    }

    /// <summary>Applies unary <c>-</c>.</summary>
    public static object? Negate(object? value) => Normalize(value) switch
    {
        TimeSpan t => -t,
        var number when Numbers.IsNumber(number) => Numbers.Negate(number),
        var other => throw new EvaluationException($"Can't make {Describe(other)} negative."),
    };

    /// <summary>Reads a value as a condition. Nothing counts as false.</summary>
    public static bool IsTrue(object? value) => Normalize(value) switch
    {
        bool b => b,
        null => false,
        var other => throw new EvaluationException($"{Describe(other)} isn't true or false."),
    };

    /// <summary>Turns a value into text for a template, with an optional .NET format such as <c>yyyy-MM-dd</c>.</summary>
    public static string Format(object? value, string? format) => value switch
    {
        null => "",
        string text => text,
        Message message => message.ToText(),
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? "",
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        JsonElement json => FormatJson(json, format),
        BigDecimal big => FormatBig(big, format),
        IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Converts a value for a parameter or a method that needs a particular type.</summary>
    public static object? ConvertTo(object? value, Type type)
    {
        value = Normalize(value);
        if (TryConvert(value, type, out var converted))
            return converted;
        throw new EvaluationException(Numbers.IsNumber(value) && Numbers.IsNumericType(type)
            ? Numbers.WhyNot(value!, type)
            : $"{Describe(value)} can't be used as {ExpressionBinder.FriendlyName(type)}.");
    }

    /// <summary>Finds the nearest ancestor emitted by the named module: <c>fam.Name</c>.</summary>
    public static Message Fam(Message message, string name) =>
        message.Fam[name] ?? throw new EvaluationException($"No message from {name} comes before this one.");

    /// <summary>Turns JSON values into ordinary values: text, numbers, true or false, or null. Objects and arrays stay JSON.</summary>
    internal static object? Normalize(object? value)
    {
        if (value is not JsonElement json)
            return value;
        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number => Numbers.FromJson(json),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => json,
        };
    }

    internal static bool TryConvert(object? value, Type type, out object? converted)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        converted = null;
        if (value is null)
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        if (target.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }
        if (target == typeof(string))
        {
            converted = Format(value, null);
            return true;
        }
        if (Numbers.IsNumber(value) && Numbers.IsNumericType(target))
            return Numbers.TryConvert(value, target, out converted);
        if (target == typeof(TimeSpan) && value is string duration && TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var span))
        {
            converted = span;
            return true;
        }
        return false;
    }

    internal static string Describe(object? value) => Normalize(value) switch
    {
        null => "nothing",
        string text => $"the text \"{text}\"",
        bool b => b ? "true" : "false",
        JsonElement { ValueKind: JsonValueKind.Object } => "JSON object",
        JsonElement { ValueKind: JsonValueKind.Array } => "JSON array",
        var number when Numbers.IsNumber(number) => $"the number {Format(number, null)}",
        var other => other.GetType().Name,
    };

    // A position in a list or text: a whole number that fits in an int.
    private static int Position(object? index) =>
        Numbers.TryConvert(index!, typeof(int), out var position)
            ? (int)position!
            : throw new EvaluationException($"A position has to be a whole number, but this is {Describe(index)}.");

    private static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (Numbers.IsNumber(left) && Numbers.IsNumber(right))
            return Numbers.AreEqual(left, right);
        return left.Equals(right);
    }

    // Kept apart from the other formats, which are on the fast path for templates.
    private static string FormatBig(BigDecimal value, string? format)
    {
        try
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException exception)
        {
            throw new EvaluationException(exception.Message);
        }
    }

    private static string FormatJson(JsonElement json, string? format)
    {
        var value = Normalize(json);
        return value is JsonElement element ? element.GetRawText() : Format(value, format);
    }

    private static Func<object, object?>? FindGetter(Type type, string name)
    {
        if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { GetMethod: not null } property && property.GetIndexParameters().Length == 0)
            return target => property.GetValue(target);
        if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } field)
            return target => field.GetValue(target);
        return null;
    }

    private static MethodInfo Method(string name) => typeof(Dynamic).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
}
