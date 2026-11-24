using System.Buffers.Binary;
using System.Text;

namespace Meep2.SDK.Security;

/// <summary>
/// Reads SSH's wire format (RFC 4251): big-endian numbers, and strings with their length before them, as SSH keys,
/// certificates, signatures and key revocation lists are written. Keeps its place, so a caller can take the bytes a
/// run of fields came from. Throws <see cref="FormatException"/>, with words to finish a sentence such as "it ends too
/// soon", when the data runs out or doesn't fit.
/// </summary>
/// <remarks>
/// Public so Meep's own key handling (<c>Meep2.Security</c>) and plugins can read the same formats as the SDK's
/// <see cref="Credential"/> and <see cref="Grant"/>, with one reader. Reading isn't where security is checked.
/// </remarks>
/// <param name="data">The bytes to read.</param>
public ref struct SshWireReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>How many bytes have been read.</summary>
    public int Position { get; private set; }

    /// <summary>Whether everything has been read.</summary>
    public readonly bool AtEnd => Position == _data.Length;

    /// <summary>Reads one byte.</summary>
    public byte ReadByte() => Take(1)[0];

    /// <summary>Reads a big-endian 32-bit number.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    /// <summary>Reads a big-endian 64-bit number.</summary>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    /// <summary>Reads this many bytes, with no length before them.</summary>
    public ReadOnlySpan<byte> ReadRaw(int length) => Take(length);

    /// <summary>Reads a string: its length, then its bytes.</summary>
    public ReadOnlySpan<byte> ReadString()
    {
        var length = ReadUInt32();
        if (length > (uint)(_data.Length - Position))
            throw new FormatException("it ends too soon");
        return Take((int)length);
    }

    /// <summary>Reads a string as UTF-8 text.</summary>
    public string ReadText()
    {
        var bytes = ReadString();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("it has text that isn't UTF-8");
        }
    }

    /// <summary>
    /// Reads an mpint that must not be negative, as RSA keys and ECDSA signatures have them, without the leading
    /// zero that keeps its top bit clear: the number's big-endian bytes.
    /// </summary>
    public byte[] ReadMpint()
    {
        var bytes = ReadString();
        if (bytes.Length > 0 && (bytes[0] & 0x80) != 0)
            throw new FormatException("it has a negative number");
        var start = 0;
        while (start < bytes.Length && bytes[start] == 0)
            start++;
        return bytes[start..].ToArray();
    }

    /// <summary>The bytes from <paramref name="start"/> up to where the reader is now.</summary>
    public readonly ReadOnlySpan<byte> Since(int start) => _data[start..Position];

    /// <summary>Throws <see cref="FormatException"/> if anything is left to read.</summary>
    public readonly void End()
    {
        if (!AtEnd)
            throw new FormatException("it has bytes left over");
    }

    private ReadOnlySpan<byte> Take(int length)
    {
        if (_data.Length - Position < length)
            throw new FormatException("it ends too soon");
        var value = _data.Slice(Position, length);
        Position += length;
        return value;
    }
}

/// <summary>
/// Writes SSH's wire format, for keys and signatures and the records Meep signs itself, such as a
/// <see cref="Grant"/>. Each method adds a field and returns the writer, so fields can be chained.
/// </summary>
public sealed class SshWireWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>Adds a big-endian 32-bit number.</summary>
    public SshWireWriter UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _stream.Write(bytes);
        return this;
    }

    /// <summary>Adds a big-endian 64-bit number.</summary>
    public SshWireWriter UInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        _stream.Write(bytes);
        return this;
    }

    /// <summary>Adds a string: its length, then its bytes.</summary>
    public SshWireWriter String(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        _stream.Write(value);
        return this;
    }

    /// <summary>Adds text as a UTF-8 string.</summary>
    public SshWireWriter Text(string value) => String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Adds an unsigned number, given as big-endian bytes, as an mpint: without leading zeros, but with one if needed
    /// to keep the top bit clear.
    /// </summary>
    public SshWireWriter Mpint(ReadOnlySpan<byte> unsigned)
    {
        var start = 0;
        while (start < unsigned.Length && unsigned[start] == 0)
            start++;
        var digits = unsigned[start..];
        return !digits.IsEmpty && (digits[0] & 0x80) != 0 ? String([0, .. digits]) : String(digits);
    }

    /// <summary>Adds bytes as they are, with no length before them.</summary>
    public SshWireWriter Raw(ReadOnlySpan<byte> value)
    {
        _stream.Write(value);
        return this;
    }

    /// <summary>Everything written so far.</summary>
    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>Times as SSH certificates and Meep's grants hold them: seconds since 1970, with the ends meaning "no limit".</summary>
internal static class SshTime
{
    /// <summary>A valid-after time, or null for 0, which means "from always".</summary>
    public static DateTime? After(ulong seconds) => seconds == 0 ? null : ToDateTime(seconds);

    /// <summary>A valid-before time, or null for the largest value, which means "forever".</summary>
    public static DateTime? Before(ulong seconds) => seconds == ulong.MaxValue ? null : ToDateTime(seconds);

    public static ulong FromAfter(DateTime? time) => time is null ? 0 : ToSeconds(time.Value);

    public static ulong FromBefore(DateTime? time) => time is null ? ulong.MaxValue : ToSeconds(time.Value);

    // Times past what DateTime can hold are as good as forever.
    private static DateTime ToDateTime(ulong seconds) =>
        seconds >= (ulong)(DateTime.MaxValue - DateTime.UnixEpoch).TotalSeconds
            ? DateTime.MaxValue
            : DateTime.UnixEpoch.AddSeconds(seconds);

    private static ulong ToSeconds(DateTime time) =>
        (ulong)Math.Max(0, (time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond);
}
