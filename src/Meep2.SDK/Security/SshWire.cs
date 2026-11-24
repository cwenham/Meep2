using System.Buffers.Binary;
using System.Text;

namespace Meep2.SDK.Security;

/// <summary>
/// Reads SSH's wire format (RFC 4251): big-endian numbers, and strings with their length before them. Keeps its
/// place, so a caller can take the bytes a run of fields came from.
/// </summary>
/// <remarks>
/// <c>Meep2.Plugins</c> has its own reader for keys and signatures. The SDK can't reference it, and plugins can't
/// reference this one, so each keeps its own; both are a few lines, and parsing isn't where security is checked.
/// </remarks>
internal ref struct SshWireReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>How many bytes have been read.</summary>
    public int Position { get; private set; }

    public readonly bool AtEnd => Position == _data.Length;

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public ReadOnlySpan<byte> ReadRaw(int length) => Take(length);

    public ReadOnlySpan<byte> ReadString()
    {
        var length = ReadUInt32();
        if (length > (uint)(_data.Length - Position))
            throw new FormatException("it ends too soon");
        return Take((int)length);
    }

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

    /// <summary>The bytes from <paramref name="start"/> up to where the reader is now.</summary>
    public readonly ReadOnlySpan<byte> Since(int start) => _data[start..Position];

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

/// <summary>Writes SSH's wire format, for the records Meep signs itself, such as a <see cref="Grant"/>.</summary>
internal sealed class SshWireWriter
{
    private readonly MemoryStream _stream = new();

    public SshWireWriter UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _stream.Write(bytes);
        return this;
    }

    public SshWireWriter UInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        _stream.Write(bytes);
        return this;
    }

    public SshWireWriter String(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        _stream.Write(value);
        return this;
    }

    public SshWireWriter Text(string value) => String(Encoding.UTF8.GetBytes(value));

    public SshWireWriter Raw(ReadOnlySpan<byte> value)
    {
        _stream.Write(value);
        return this;
    }

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
