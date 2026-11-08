using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Meep2.SDK.Modules;
using Meep2.SDK.Numbers;

namespace Meep2.Meeplang.Compilation;

/// <summary>The kinds of number Meeplang works with.</summary>
internal enum NumberKind
{
    /// <summary>Not a number.</summary>
    None,

    /// <summary>decimal and the whole-number types up to 64 bits: worked out as decimals.</summary>
    Exact,

    /// <summary>BigDecimal, BigInteger, Int128 and UInt128: exact, but they may not fit in a decimal.</summary>
    Big,

    /// <summary>float, double and Half from C# types: approximate already, so sums with them are done in double.</summary>
    Approximate,
}

/// <summary>
/// How Meeplang works with numbers. Numbers are decimals, so <c>7 / 2</c> is 3.5 and <c>0.1 + 0.2</c> is exactly 0.3.
/// A number too big or too small for decimal, or with more digits than decimal holds, carries on as a
/// <see cref="BigDecimal"/>, and goes back to being a decimal when it fits again. float and double values from C#
/// types are approximate already, so sums and comparisons with them are done in double. Dividing by zero is an error.
/// Public only so compiled code can reach it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Numbers
{
    internal static readonly MethodInfo AddMethod = Method(nameof(Add), typeof(decimal));
    internal static readonly MethodInfo SubtractMethod = Method(nameof(Subtract), typeof(decimal));
    internal static readonly MethodInfo MultiplyMethod = Method(nameof(Multiply), typeof(decimal));
    internal static readonly MethodInfo DivideMethod = Method(nameof(Divide), typeof(decimal));
    internal static readonly MethodInfo RemainderMethod = Method(nameof(Remainder), typeof(decimal));
    internal static readonly MethodInfo DivideApproximateMethod = Method(nameof(Divide), typeof(double));
    internal static readonly MethodInfo RemainderApproximateMethod = Method(nameof(Remainder), typeof(double));
    internal static readonly MethodInfo ArithmeticMethod = typeof(Numbers).GetMethod(nameof(Arithmetic), [typeof(string), typeof(object), typeof(object)])!;
    internal static readonly MethodInfo NegateMethod = typeof(Numbers).GetMethod(nameof(Negate), [typeof(object)])!;

    // Typed sums on decimals, which compiled expressions call directly. When the answer doesn't fit in a decimal they
    // throw BeyondDecimalException, and the expression is compiled again to use big numbers.

    public static decimal Add(decimal left, decimal right)
    {
        try
        {
            return left + right;
        }
        catch (OverflowException)
        {
            throw new BeyondDecimalException();
        }
    }

    public static decimal Subtract(decimal left, decimal right)
    {
        try
        {
            return left - right;
        }
        catch (OverflowException)
        {
            throw new BeyondDecimalException();
        }
    }

    public static decimal Multiply(decimal left, decimal right)
    {
        decimal result;
        try
        {
            result = left * right;
        }
        catch (OverflowException)
        {
            throw new BeyondDecimalException();
        }
        // decimal rounds anything smaller than 10^-28 to zero, where a big number keeps it.
        return result == 0 && left != 0 && right != 0 ? throw new BeyondDecimalException() : result;
    }

    public static decimal Divide(decimal left, decimal right)
    {
        if (right == 0)
            throw DivideByZero();
        decimal result;
        try
        {
            result = left / right;
        }
        catch (OverflowException)
        {
            throw new BeyondDecimalException();
        }
        return result == 0 && left != 0 ? throw new BeyondDecimalException() : result;
    }

    public static decimal Remainder(decimal left, decimal right) => right == 0 ? throw DivideByZero() : left % right;

    public static double Divide(double left, double right) => right == 0 ? throw DivideByZero() : left / right;

    public static double Remainder(double left, double right) => right == 0 ? throw DivideByZero() : left % right;

    /// <summary>Applies <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> or <c>%</c> to two numbers of any kind.</summary>
    public static object Arithmetic(string op, object? left, object? right)
    {
        var leftKind = KindOf(left);
        var rightKind = KindOf(right);
        if (leftKind == NumberKind.None || rightKind == NumberKind.None)
            throw new EvaluationException($"Can't use {op} with {Dynamic.Describe(left)} and {Dynamic.Describe(right)}.");

        if (leftKind == NumberKind.Approximate || rightKind == NumberKind.Approximate)
        {
            var a = ToDouble(left!);
            var b = ToDouble(right!);
            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => Divide(a, b),
                "%" => Remainder(a, b),
                _ => throw NotArithmetic(op),
            };
        }

        if (leftKind == NumberKind.Exact && rightKind == NumberKind.Exact && TryDecimal(op, ToDecimal(left!), ToDecimal(right!), out var small))
            return small;

        var x = ToBig(left!);
        var y = ToBig(right!);
        try
        {
            return Simplify(op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y.IsZero ? throw DivideByZero() : x / y,
                "%" => y.IsZero ? throw DivideByZero() : x % y,
                _ => throw NotArithmetic(op),
            });
        }
        catch (OverflowException)
        {
            throw TooManyDigits("The result");
        }
    }

    /// <summary>Applies unary <c>-</c> to a number of any kind.</summary>
    public static object Negate(object? value) => KindOf(value) switch
    {
        NumberKind.Exact => -ToDecimal(value!),
        NumberKind.Approximate => -ToDouble(value!),
        NumberKind.Big => Simplify(-ToBig(value!)),
        _ => throw new EvaluationException($"Can't make {Dynamic.Describe(value)} negative."),
    };

    internal static NumberKind KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(decimal) || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong))
            return NumberKind.Exact;
        if (type == typeof(double) || type == typeof(float) || type == typeof(Half))
            return NumberKind.Approximate;
        if (type == typeof(BigDecimal) || type == typeof(BigInteger) || type == typeof(Int128) || type == typeof(UInt128))
            return NumberKind.Big;
        return NumberKind.None;
    }

    internal static NumberKind KindOf(object? value) => value switch
    {
        decimal or int or long or short or byte or sbyte or ushort or uint or ulong => NumberKind.Exact,
        double or float or Half => NumberKind.Approximate,
        BigDecimal or BigInteger or Int128 or UInt128 => NumberKind.Big,
        _ => NumberKind.None,
    };

    internal static bool IsNumber(object? value) => KindOf(value) != NumberKind.None;

    internal static bool IsNumericType(Type type) => KindOf(type) != NumberKind.None;

    /// <summary>Whether a type holds only whole numbers.</summary>
    internal static bool IsWholeNumberType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return IsNumericType(type) && type != typeof(decimal) && type != typeof(BigDecimal) && KindOf(type) != NumberKind.Approximate;
    }

    /// <summary>Orders two numbers, or returns null if they can't be ordered because one is NaN.</summary>
    internal static int? Compare(object left, object right)
    {
        var leftKind = KindOf(left);
        var rightKind = KindOf(right);
        if (leftKind == NumberKind.Approximate || rightKind == NumberKind.Approximate)
        {
            var a = ToDouble(left);
            var b = ToDouble(right);
            return double.IsNaN(a) || double.IsNaN(b) ? null : a.CompareTo(b);
        }
        if (leftKind == NumberKind.Exact && rightKind == NumberKind.Exact)
            return ToDecimal(left).CompareTo(ToDecimal(right));
        return ToBig(left).CompareTo(ToBig(right));
    }

    internal static bool AreEqual(object left, object right) => Compare(left, right) == 0;

    /// <summary>Reads a number from Meeplang: a decimal if one holds it exactly, otherwise a BigDecimal.</summary>
    /// <exception cref="OverflowException">It has more digits than a BigDecimal can.</exception>
    internal static object Parse(string text)
    {
        // Up to 28 digits always fit in a decimal exactly.
        if (text.Length <= 28 && text.AsSpan().IndexOfAny('e', 'E') < 0
            && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var small))
            return small;
        return Simplify(BigDecimal.Parse(text, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads a JSON number exactly: a decimal if one holds it, otherwise a BigDecimal.</summary>
    internal static object FromJson(JsonElement number)
    {
        var raw = JsonMarshal.GetRawUtf8Value(number);
        if (raw.Length <= 28 && raw.IndexOfAny((byte)'e', (byte)'E') < 0 && number.TryGetDecimal(out var small))
            return small;
        try
        {
            return Parse(Encoding.UTF8.GetString(raw));
        }
        catch (OverflowException)
        {
            throw TooManyDigits("A JSON number");
        }
    }

    /// <summary>
    /// Converts a number for a parameter or method that needs another numeric type. Whole-number types only take
    /// whole numbers that fit, and decimal only takes numbers it can hold.
    /// </summary>
    internal static bool TryConvert(object value, Type target, out object? converted)
    {
        converted = null;
        var kind = KindOf(value);
        if (kind == NumberKind.None)
            return false;

        if (target == typeof(double) || target == typeof(float) || target == typeof(Half))
        {
            var approximate = ToDouble(value);
            if (target == typeof(double))
                converted = approximate;
            else if (target == typeof(float))
                converted = (float)approximate;
            else
                converted = (Half)approximate;
            return true;
        }
        if (target == typeof(decimal))
        {
            switch (kind)
            {
                case NumberKind.Exact:
                    converted = ToDecimal(value);
                    return true;
                case NumberKind.Big when ToBig(value).TryGetDecimal(out var exact):
                    converted = exact;
                    return true;
                case NumberKind.Approximate when TryFromDouble(ToDouble(value), out var number) && number.TryGetDecimal(out var nearest):
                    converted = nearest;
                    return true;
                default:
                    return false;
            }
        }
        if (target == typeof(BigDecimal))
        {
            if (kind != NumberKind.Approximate)
            {
                converted = ToBig(value);
                return true;
            }
            if (!TryFromDouble(ToDouble(value), out var number))
                return false;
            converted = number;
            return true;
        }
        if (IsWholeNumberType(target) && TryGetWhole(value, out var whole))
        {
            try
            {
                converted = Whole(whole, target);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
        return false;
    }

    // Each branch is boxed as its own type; a conditional expression would widen them all to one.
    private static object Whole(BigInteger whole, Type target)
    {
        if (target == typeof(int))
            return (int)whole;
        if (target == typeof(long))
            return (long)whole;
        if (target == typeof(short))
            return (short)whole;
        if (target == typeof(byte))
            return (byte)whole;
        if (target == typeof(sbyte))
            return (sbyte)whole;
        if (target == typeof(ushort))
            return (ushort)whole;
        if (target == typeof(uint))
            return (uint)whole;
        if (target == typeof(ulong))
            return (ulong)whole;
        if (target == typeof(Int128))
            return (Int128)whole;
        if (target == typeof(UInt128))
            return (UInt128)whole;
        return whole;
    }

    /// <summary>Why a number can't be converted to a numeric type, for an error message.</summary>
    internal static string WhyNot(object value, Type target)
    {
        target = Nullable.GetUnderlyingType(target) ?? target;
        var described = Dynamic.Describe(value);
        if (IsWholeNumberType(target))
        {
            if (!TryGetWhole(value, out _))
                return $"Expected a whole number, but this is {described}.";
            var (min, max) = Range(target);
            return $"Expected a whole number from {min} to {max}, but this is {described}.";
        }
        if (target == typeof(decimal))
            return $"{Capitalise(described)} doesn't fit in a decimal, which holds up to 28 places after the point and numbers up to about 7.9 × 10^28.";
        return $"{Capitalise(described)} can't be used as {ExpressionBinder.FriendlyName(target)}.";
    }

    // A BigDecimal goes back to being a decimal when it fits, so a number is only ever big when it has to be.
    // (Both branches are boxed as they are: without the cast, the decimal would be converted back to a BigDecimal.)
    private static object Simplify(BigDecimal value) => value.TryGetDecimal(out var small) ? small : (object)value;

    private static bool TryDecimal(string op, decimal left, decimal right, out decimal result)
    {
        result = 0;
        if ((op is "/" or "%") && right == 0)
            throw DivideByZero();
        try
        {
            result = op switch
            {
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                "/" => left / right,
                "%" => left % right,
                _ => throw NotArithmetic(op),
            };
        }
        catch (OverflowException)
        {
            return false;
        }
        // decimal rounds anything smaller than 10^-28 to zero, where a big number keeps it.
        return !(result == 0 && (op is "*" or "/") && left != 0 && right != 0);
    }

    private static bool TryGetWhole(object value, out BigInteger whole)
    {
        whole = default;
        switch (KindOf(value))
        {
            case NumberKind.Exact:
            {
                var exact = ToDecimal(value);
                if (!decimal.IsInteger(exact))
                    return false;
                whole = new BigInteger(exact);
                return true;
            }
            case NumberKind.Big:
            {
                var big = ToBig(value);
                if (!BigDecimal.IsInteger(big))
                    return false;
                whole = (BigInteger)big;
                return true;
            }
            case NumberKind.Approximate:
            {
                var approximate = ToDouble(value);
                if (!double.IsFinite(approximate) || Math.Floor(approximate) != approximate)
                    return false;
                whole = new BigInteger(approximate);
                return true;
            }
            default:
                return false;
        }
    }

    // A double as the decimal number it's written as, so 0.1 is 0.1 rather than 0.1000000000000000055511151231.
    private static bool TryFromDouble(double value, out BigDecimal number)
    {
        number = default;
        return double.IsFinite(value)
            && BigDecimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out number);
    }

    private static decimal ToDecimal(object value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        uint ui => ui,
        ulong ul => ul,
        _ => throw new InvalidOperationException($"{value.GetType().Name} isn't a decimal or a whole number of 64 bits or fewer."),
    };

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        Half h => (double)h,
        decimal m => (double)m,
        BigDecimal b => (double)b,
        BigInteger i => (double)i,
        Int128 i => (double)i,
        UInt128 u => (double)u,
        _ => (double)ToDecimal(value),
    };

    private static BigDecimal ToBig(object value)
    {
        try
        {
            return value switch
            {
                BigDecimal b => b,
                BigInteger i => (BigDecimal)i,
                Int128 i => (BigDecimal)(BigInteger)i,
                UInt128 u => (BigDecimal)(BigInteger)u,
                _ => ToDecimal(value),
            };
        }
        catch (OverflowException)
        {
            throw TooManyDigits("A number");
        }
    }

    private static (string Min, string Max) Range(Type type)
    {
        static string Text(BigInteger value) => value.ToString("N0", CultureInfo.InvariantCulture);
        return type switch
        {
            _ when type == typeof(int) => (Text(int.MinValue), Text(int.MaxValue)),
            _ when type == typeof(long) => (Text(long.MinValue), Text(long.MaxValue)),
            _ when type == typeof(short) => (Text(short.MinValue), Text(short.MaxValue)),
            _ when type == typeof(byte) => (Text(byte.MinValue), Text(byte.MaxValue)),
            _ when type == typeof(sbyte) => (Text(sbyte.MinValue), Text(sbyte.MaxValue)),
            _ when type == typeof(ushort) => (Text(ushort.MinValue), Text(ushort.MaxValue)),
            _ when type == typeof(uint) => (Text(uint.MinValue), Text(uint.MaxValue)),
            _ when type == typeof(ulong) => (Text(ulong.MinValue), Text(ulong.MaxValue)),
            _ when type == typeof(Int128) => (Text(Int128.MinValue), Text(Int128.MaxValue)),
            _ when type == typeof(UInt128) => (Text(UInt128.MinValue), Text(UInt128.MaxValue)),
            _ => ("any size", "any size"),
        };
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static EvaluationException DivideByZero() => new("Can't divide by zero.");

    private static EvaluationException NotArithmetic(string op) => new($"'{op}' isn't arithmetic.");

    private static EvaluationException TooManyDigits(string what) =>
        new($"{what} has more than {BigDecimal.MaxDigits:N0} digits, which is more than Meeplang works with.");

    private static MethodInfo Method(string name, Type operand) =>
        typeof(Numbers).GetMethod(name, BindingFlags.Public | BindingFlags.Static, [operand, operand])!;
}

/// <summary>A typed sum's answer didn't fit in a decimal, so the expression has to be worked out with big numbers.</summary>
internal sealed class BeyondDecimalException() : Exception("A sum's answer doesn't fit in a decimal.");
