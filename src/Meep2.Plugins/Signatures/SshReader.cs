using System.Buffers.Binary;
using System.Text;

namespace Meep2.Plugins;

/// <summary>Reads SSH's wire format: big-endian lengths before strings and numbers.</summary>
internal ref struct SshReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _rest = data;

    public uint ReadUInt32()
    {
        if (_rest.Length < 4)
            throw new FormatException("it ends too soon");
        var value = BinaryPrimitives.ReadUInt32BigEndian(_rest);
        _rest = _rest[4..];
        return value;
    }

    public ReadOnlySpan<byte> ReadRaw(int length)
    {
        if (_rest.Length < length)
            throw new FormatException("it ends too soon");
        var value = _rest[..length];
        _rest = _rest[length..];
        return value;
    }

    public byte[] ReadBytes()
    {
        var length = ReadUInt32();
        if (length > (uint)_rest.Length)
            throw new FormatException("it ends too soon");
        return ReadRaw((int)length).ToArray();
    }

    public string ReadText() => Encoding.UTF8.GetString(ReadBytes());

    // An unsigned mpint, without the leading zero that keeps its top bit clear.
    public byte[] ReadMpint()
    {
        var bytes = ReadBytes();
        if (bytes.Length > 0 && (bytes[0] & 0x80) != 0)
            throw new FormatException("it has a negative number");
        var start = 0;
        while (start < bytes.Length && bytes[start] == 0)
            start++;
        return bytes[start..];
    }

    public readonly void End()
    {
        if (!_rest.IsEmpty)
            throw new FormatException("it has bytes left over");
    }
}
