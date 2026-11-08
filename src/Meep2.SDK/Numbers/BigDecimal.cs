using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Meep2.SDK.Numbers;

/// <summary>
/// A decimal number with as many digits as it needs, for values <see cref="decimal"/> can't hold: bigger than about
/// 7.9 × 10²⁸, smaller than 10⁻²⁸, or with more than 28 significant digits. Its value is
/// <see cref="Significand"/> × 10^<see cref="Exponent"/>.
/// </summary>
/// <remarks>
/// <para>Adding, subtracting, multiplying and taking a remainder keep every digit. Dividing keeps
/// <see cref="DivisionDigits"/> significant digits, or as many as the longer of the two numbers has, and rounds the
/// last one half to even. A division that comes out exactly has no more digits than it needs.</para>
/// <para>Like decimal, a BigDecimal keeps trailing zeros after the point, so 1.50 × 2 is 3.00. Numbers that differ
/// only in trailing zeros are equal.</para>
/// <para>Written out in full, a BigDecimal has at most <see cref="MaxDigits"/> digits, counting any zeros between the
/// point and its first significant digit. This keeps a hostile input such as 1e999999999 from using up memory and
/// time. Anything longer throws <see cref="OverflowException"/>.</para>
/// <para>Text is always read and written with a <c>.</c> before the fraction, whatever the culture.</para>
/// </remarks>
public readonly struct BigDecimal : IEquatable<BigDecimal>, IComparable<BigDecimal>, IComparable, IFormattable, IParsable<BigDecimal>
{
    /// <summary>The most digits a BigDecimal can have when written out in full.</summary>
    public const int MaxDigits = 1000;

    /// <summary>The fewest significant digits a division keeps, the same as decimal.</summary>
    public const int DivisionDigits = 28;

    private const double Log10Of2 = 0.30102999566398119521;
    private static readonly BigInteger[] s_powersOfTen = Enumerable.Range(0, 128).Select(n => BigInteger.Pow(10, n)).ToArray();
    private static readonly BigInteger s_largestDecimalMantissa = (BigInteger.One << 96) - 1;

    private readonly BigInteger _significand;
    private readonly int _exponent;

    /// <summary>Makes the number <paramref name="significand"/> × 10^<paramref name="exponent"/>.</summary>
    /// <exception cref="OverflowException">Written out in full, it would have more than <see cref="MaxDigits"/> digits.</exception>
    public BigDecimal(BigInteger significand, int exponent)
    {
        if (significand.IsZero && exponent > 0)
            exponent = 0;
        if (WrittenLength(significand, exponent) > MaxDigits)
            throw TooManyDigits();
        _significand = significand;
        _exponent = exponent;
    }

    /// <summary>The digits of the number, with its sign, as a whole number.</summary>
    public BigInteger Significand => _significand;

    /// <summary>The power of ten the significand is multiplied by: -2 for 1.50, which is 150 × 10⁻².</summary>
    public int Exponent => _exponent;

    /// <summary>-1, 0 or 1.</summary>
    public int Sign => _significand.Sign;

    /// <summary>Whether the number is zero.</summary>
    public bool IsZero => _significand.IsZero;

    /// <summary>Whether the number has no fraction.</summary>
    public static bool IsInteger(BigDecimal value) =>
        value._exponent >= 0 || (value._significand % Pow10(-value._exponent)).IsZero;

    /// <summary>Gets the number as a decimal, if a decimal can hold it exactly.</summary>
    public bool TryGetDecimal(out decimal value)
    {
        value = 0;
        var significand = _significand;
        var exponent = _exponent;
        if (exponent > 0)
        {
            if (exponent > 28)
                return false;
            significand *= Pow10(exponent);
            exponent = 0;
        }

        // decimal holds at most 28 places after the point, so trailing zeros beyond that have to go.
        while (exponent < -28)
        {
            var quotient = BigInteger.DivRem(significand, 10, out var remainder);
            if (!remainder.IsZero)
                return false;
            significand = quotient;
            exponent++;
        }

        var magnitude = BigInteger.Abs(significand);
        if (magnitude > s_largestDecimalMantissa)
            return false;
        var mantissa = (UInt128)magnitude;
        value = new decimal((int)(uint)mantissa, (int)(uint)(mantissa >> 32), (int)(uint)(mantissa >> 64), significand.Sign < 0, (byte)-exponent);
        return true;
    }

    /// <summary>Divides, keeping the given number of significant digits and rounding the last one half to even.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    /// <exception cref="OverflowException">The result would have more than <see cref="MaxDigits"/> digits.</exception>
    public static BigDecimal Divide(BigDecimal dividend, BigDecimal divisor, int significantDigits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(significantDigits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(significantDigits, MaxDigits);
        if (divisor.IsZero)
            throw new DivideByZeroException();

        // As for decimal, an exact result keeps the dividend's places less the divisor's: 6.0 / 2 is 3.0.
        var preferred = dividend._exponent - divisor._exponent;
        if (dividend.IsZero)
            return new BigDecimal(BigInteger.Zero, Math.Clamp(preferred, 1 - MaxDigits, 0));

        var numerator = BigInteger.Abs(dividend._significand);
        var denominator = BigInteger.Abs(divisor._significand);

        // Shift the dividend left far enough that the quotient has at least one digit more than it keeps.
        var shift = Math.Max(0, significantDigits + 1 + DigitCount(denominator) - DigitCount(numerator));
        var quotient = BigInteger.DivRem(numerator * Pow10(shift), denominator, out var remainder);
        var exponent = preferred - shift;
        var exact = remainder.IsZero;

        var drop = DigitCount(quotient) - significantDigits;
        if (drop > 0)
        {
            var unit = Pow10(drop);
            var kept = BigInteger.DivRem(quotient, unit, out var dropped);
            var half = (dropped * 2).CompareTo(unit);
            if (half > 0 || (half == 0 && (!exact || !kept.IsEven)))
                kept += BigInteger.One;
            exact = exact && dropped.IsZero;
            quotient = kept;
            exponent += drop;
        }

        if (exact)
        {
            while (exponent < preferred)
            {
                var shorter = BigInteger.DivRem(quotient, 10, out var digit);
                if (!digit.IsZero)
                    break;
                quotient = shorter;
                exponent++;
            }
        }

        return new BigDecimal(dividend.Sign == divisor.Sign ? quotient : -quotient, exponent);
    }

    /// <summary>Reads a number such as <c>-12.50</c> or <c>1.5e30</c>.</summary>
    /// <exception cref="FormatException">The text isn't a number.</exception>
    /// <exception cref="OverflowException">The number would have more than <see cref="MaxDigits"/> digits.</exception>
    public static BigDecimal Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Read(s, out var result) switch
        {
            ReadResult.Read => result,
            ReadResult.TooManyDigits => throw TooManyDigits(),
            _ => throw new FormatException($"'{s}' isn't a number."),
        };
    }

    /// <summary>Reads a number such as <c>-12.50</c> or <c>1.5e30</c>, if the text is one that fits.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out BigDecimal result)
    {
        result = default;
        return s is not null && Read(s, out result) == ReadResult.Read;
    }

    public static implicit operator BigDecimal(long value) => new(value, 0);

    public static implicit operator BigDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var mantissa = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0xFF;
        var significand = (BigInteger)mantissa;
        return new BigDecimal(bits[3] < 0 ? -significand : significand, -scale);
    }

    /// <exception cref="OverflowException">The number has more than <see cref="MaxDigits"/> digits.</exception>
    public static explicit operator BigDecimal(BigInteger value) => new(value, 0);

    /// <summary>The nearest decimal, rounding digits beyond decimal's precision.</summary>
    /// <exception cref="OverflowException">The number is too big for a decimal.</exception>
    public static explicit operator decimal(BigDecimal value) =>
        value.TryGetDecimal(out var exact) ? exact : decimal.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The nearest double, or infinity if the number is beyond double's range.</summary>
    public static explicit operator double(BigDecimal value) =>
        double.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The whole part of the number, dropping any fraction.</summary>
    public static explicit operator BigInteger(BigDecimal value) =>
        value._exponent >= 0
            ? value._significand * Pow10(value._exponent)
            : BigInteger.Divide(value._significand, Pow10(-value._exponent));

    public static BigDecimal operator +(BigDecimal left, BigDecimal right)
    {
        var exponent = Math.Min(left._exponent, right._exponent);
        return new BigDecimal(Align(left, exponent) + Align(right, exponent), exponent);
    }

    public static BigDecimal operator -(BigDecimal left, BigDecimal right)
    {
        var exponent = Math.Min(left._exponent, right._exponent);
        return new BigDecimal(Align(left, exponent) - Align(right, exponent), exponent);
    }

    public static BigDecimal operator -(BigDecimal value) => new(BigInteger.Negate(value._significand), value._exponent);

    public static BigDecimal operator *(BigDecimal left, BigDecimal right) =>
        new(left._significand * right._significand, left._exponent + right._exponent);

    /// <summary>Divides, keeping <see cref="DivisionDigits"/> significant digits, or as many as the longer number has.</summary>
    public static BigDecimal operator /(BigDecimal left, BigDecimal right) =>
        Divide(left, right, Math.Min(MaxDigits, Math.Max(DivisionDigits, Math.Max(DigitCount(left._significand), DigitCount(right._significand)))));

    /// <summary>The remainder after dividing, with the dividend's sign, as for decimal: -7.5 % 2 is -1.5.</summary>
    public static BigDecimal operator %(BigDecimal left, BigDecimal right)
    {
        if (right.IsZero)
            throw new DivideByZeroException();
        var exponent = Math.Min(left._exponent, right._exponent);
        return new BigDecimal(BigInteger.Remainder(Align(left, exponent), Align(right, exponent)), exponent);
    }

    public static bool operator ==(BigDecimal left, BigDecimal right) => left.CompareTo(right) == 0;

    public static bool operator !=(BigDecimal left, BigDecimal right) => left.CompareTo(right) != 0;

    public static bool operator <(BigDecimal left, BigDecimal right) => left.CompareTo(right) < 0;

    public static bool operator <=(BigDecimal left, BigDecimal right) => left.CompareTo(right) <= 0;

    public static bool operator >(BigDecimal left, BigDecimal right) => left.CompareTo(right) > 0;

    public static bool operator >=(BigDecimal left, BigDecimal right) => left.CompareTo(right) >= 0;

    public int CompareTo(BigDecimal other)
    {
        var signs = Sign.CompareTo(other.Sign);
        if (signs != 0 || IsZero)
            return signs;
        var exponent = Math.Min(_exponent, other._exponent);
        return Align(this, exponent).CompareTo(Align(other, exponent));
    }

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        BigDecimal other => CompareTo(other),
        _ => throw new ArgumentException($"A BigDecimal can only be compared with another BigDecimal, not {obj.GetType().Name}.", nameof(obj)),
    };

    public bool Equals(BigDecimal other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is BigDecimal other && Equals(other);

    // Equal numbers can differ in trailing zeros, so hash the number without them.
    public override int GetHashCode()
    {
        if (IsZero)
            return 0;
        var significand = _significand;
        var exponent = _exponent;
        while (true)
        {
            var shorter = BigInteger.DivRem(significand, 10, out var digit);
            if (!digit.IsZero)
                break;
            significand = shorter;
            exponent++;
        }
        return HashCode.Combine(significand, exponent);
    }

    /// <summary>The number written out in full, such as <c>-1234.50</c>, never in scientific notation.</summary>
    public override string ToString() => Write(_significand, _exponent, NumberFormatInfo.InvariantInfo, grouped: false);

    /// <summary>
    /// Writes the number with a standard .NET format: G or R (in full, the default), F and N (fixed places, N with
    /// separators between thousands), E (scientific), P (percent) or D (whole numbers only), each with an optional
    /// number of digits. Custom formats such as <c>0.00</c> aren't supported.
    /// </summary>
    /// <exception cref="FormatException">The format isn't one of these.</exception>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        var info = NumberFormatInfo.GetInstance(formatProvider);
        if (string.IsNullOrEmpty(format) || format is "G" or "g" or "R" or "r")
            return Write(_significand, _exponent, info, grouped: false);

        var digits = -1;
        if (format.Length > 1 && (!int.TryParse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out digits) || digits > MaxDigits))
            throw UnsupportedFormat(format);

        switch (format[0])
        {
            case 'F' or 'f':
            {
                var (significand, exponent) = Round(_significand, _exponent, digits < 0 ? info.NumberDecimalDigits : digits);
                return Write(significand, exponent, info, grouped: false);
            }
            case 'N' or 'n':
            {
                var (significand, exponent) = Round(_significand, _exponent, digits < 0 ? info.NumberDecimalDigits : digits);
                return Write(significand, exponent, info, grouped: true);
            }
            case 'P' or 'p':
                return Percent(digits < 0 ? info.PercentDecimalDigits : digits, info);
            case 'E' or 'e':
                return Scientific(digits < 0 ? 6 : digits, format[0], info);
            case 'D' or 'd' when IsInteger(this):
                return ((BigInteger)this).ToString(format, info);
            default:
                throw UnsupportedFormat(format);
        }
    }

    // The number of decimal digits in a whole number, ignoring its sign. Zero has one.
    private static int DigitCount(BigInteger value)
    {
        value = BigInteger.Abs(value);
        if (value < 10)
            return 1;
        // 2^(bits-1) <= value < 2^bits, so the answer is this estimate or one more.
        var bits = value.GetBitLength();
        var digits = (int)((bits - 1) * Log10Of2) + 1;
        return value >= Pow10(digits) ? digits + 1 : digits;
    }

    // Digits before the point (at least one) plus digits after it, when the number is written out in full.
    private static long WrittenLength(BigInteger significand, long exponent)
    {
        long digits = DigitCount(significand);
        return Math.Max(digits + exponent, 1) + Math.Max(-exponent, 0);
    }

    private static BigInteger Pow10(int exponent) =>
        exponent < s_powersOfTen.Length ? s_powersOfTen[exponent] : BigInteger.Pow(10, exponent);

    // The significand, rescaled to a smaller exponent. Both numbers are at most MaxDigits long, so this stays small.
    private static BigInteger Align(BigDecimal value, int exponent) => value._significand * Pow10(value._exponent - exponent);

    // Rounds to a number of places after the point, half away from zero, as .NET does when formatting.
    private static (BigInteger Significand, int Exponent) Round(BigInteger significand, int exponent, int places)
    {
        if (exponent >= -places)
            return (significand * Pow10(exponent + places), -places);
        var unit = Pow10(-places - exponent);
        var kept = BigInteger.DivRem(BigInteger.Abs(significand), unit, out var dropped);
        if (dropped * 2 >= unit)
            kept += BigInteger.One;
        return (significand.Sign < 0 ? -kept : kept, -places);
    }

    private static string Write(BigInteger significand, int exponent, NumberFormatInfo info, bool grouped)
    {
        var digits = BigInteger.Abs(significand).ToString(CultureInfo.InvariantCulture);
        string whole;
        var fraction = "";
        if (exponent >= 0)
        {
            whole = significand.IsZero ? "0" : digits + new string('0', exponent);
        }
        else
        {
            var places = -exponent;
            if (digits.Length <= places)
                digits = new string('0', places - digits.Length + 1) + digits;
            whole = digits[..^places];
            fraction = digits[^places..];
        }

        var builder = new StringBuilder(whole.Length + fraction.Length + (whole.Length / 3) + 4);
        if (significand.Sign < 0)
            builder.Append(info.NegativeSign);
        if (grouped)
        {
            var first = whole.Length % 3 == 0 ? 3 : whole.Length % 3;
            builder.Append(whole, 0, first);
            for (var i = first; i < whole.Length; i += 3)
                builder.Append(info.NumberGroupSeparator).Append(whole, i, 3);
        }
        else
        {
            builder.Append(whole);
        }
        if (fraction.Length > 0)
            builder.Append(info.NumberDecimalSeparator).Append(fraction);
        return builder.ToString();
    }

    private string Percent(int places, NumberFormatInfo info)
    {
        var (significand, exponent) = Round(_significand, _exponent + 2, places);
        var magnitude = Write(BigInteger.Abs(significand), exponent, info, grouped: true);
        var text = info.PercentPositivePattern switch
        {
            0 => $"{magnitude} {info.PercentSymbol}",
            1 => $"{magnitude}{info.PercentSymbol}",
            2 => $"{info.PercentSymbol}{magnitude}",
            _ => $"{info.PercentSymbol} {magnitude}",
        };
        return significand.Sign < 0 ? info.NegativeSign + text : text;
    }

    private string Scientific(int places, char letter, NumberFormatInfo info)
    {
        var magnitude = BigInteger.Abs(_significand);
        var digits = DigitCount(magnitude);
        var power = IsZero ? 0 : digits - 1 + _exponent;
        var keep = places + 1;
        BigInteger kept;
        if (digits <= keep)
        {
            kept = magnitude * Pow10(keep - digits);
        }
        else
        {
            var unit = Pow10(digits - keep);
            kept = BigInteger.DivRem(magnitude, unit, out var dropped);
            if (dropped * 2 >= unit)
                kept += BigInteger.One;
            if (DigitCount(kept) > keep)
            {
                // Rounding carried into a new digit, as 9.99 does to 10.0.
                kept /= 10;
                power++;
            }
        }

        var text = IsZero ? new string('0', keep) : kept.ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(keep + 8);
        if (Sign < 0)
            builder.Append(info.NegativeSign);
        builder.Append(text[0]);
        if (places > 0)
            builder.Append(info.NumberDecimalSeparator).Append(text, 1, places);
        builder.Append(letter == 'e' ? 'e' : 'E');
        builder.Append(power < 0 ? info.NegativeSign : info.PositiveSign);
        builder.Append(Math.Abs(power).ToString("D3", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private enum ReadResult
    {
        Read,
        NotANumber,
        TooManyDigits,
    }

    // Reads [+-]digits[.digits][(e|E)[+-]digits], with digits on at least one side of the point.
    private static ReadResult Read(ReadOnlySpan<char> text, out BigDecimal result)
    {
        result = default;
        var position = 0;
        var negative = false;
        if (position < text.Length && text[position] is '+' or '-')
        {
            negative = text[position] == '-';
            position++;
        }

        var digits = new StringBuilder();
        var places = 0;
        var sawDigit = false;
        var sawPoint = false;
        for (; position < text.Length; position++)
        {
            var c = text[position];
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
                if (sawPoint)
                    places++;
                if (digits.Length > 0 || c != '0')
                    digits.Append(c);
                // A longer significand is longer than MaxDigits however it's written, so stop early.
                if (digits.Length > MaxDigits)
                    return ReadResult.TooManyDigits;
            }
            else if (c == '.' && !sawPoint)
            {
                sawPoint = true;
            }
            else
            {
                break;
            }
        }
        if (!sawDigit)
            return ReadResult.NotANumber;

        long exponent = -places;
        if (position < text.Length && text[position] is 'e' or 'E')
        {
            position++;
            var exponentNegative = false;
            if (position < text.Length && text[position] is '+' or '-')
            {
                exponentNegative = text[position] == '-';
                position++;
            }
            var start = position;
            long power = 0;
            for (; position < text.Length && char.IsAsciiDigit(text[position]); position++)
                power = Math.Min((power * 10) + (text[position] - '0'), 1_000_000_000L);
            if (position == start)
                return ReadResult.NotANumber;
            exponent += exponentNegative ? -power : power;
        }
        if (position != text.Length)
            return ReadResult.NotANumber;

        if (digits.Length == 0)
        {
            result = new BigDecimal(BigInteger.Zero, (int)Math.Clamp(exponent, 1 - MaxDigits, 0));
            return ReadResult.Read;
        }
        var significand = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (WrittenLength(significand, exponent) > MaxDigits)
            return ReadResult.TooManyDigits;
        result = new BigDecimal(negative ? -significand : significand, (int)exponent);
        return ReadResult.Read;
    }

    private static OverflowException TooManyDigits() =>
        new($"A BigDecimal can have at most {MaxDigits:N0} digits when written out in full.");

    private static FormatException UnsupportedFormat(string format) =>
        new($"A big number can be written with the formats G, F, N, E, P, and D for whole numbers, each with an optional number of digits, but not '{format}'.");
}
