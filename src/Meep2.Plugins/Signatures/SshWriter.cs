using System.Buffers.Binary;
using System.Text;

namespace Meep2.Plugins;

/// <summary>Writes SSH's wire format.</summary>
internal sealed class SshWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteUInt32((uint)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteText(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));

    // An unsigned number as an mpint: no leading zeros, except one to keep the top bit clear.
    public void WriteMpint(ReadOnlySpan<byte> unsigned)
    {
        var start = 0;
        while (start < unsigned.Length && unsigned[start] == 0)
            start++;
        var digits = unsigned[start..];
        if (!digits.IsEmpty && (digits[0] & 0x80) != 0)
            WriteBytes([0, .. digits]);
        else
            WriteBytes(digits);
    }

    public byte[] ToArray() => _stream.ToArray();
}
