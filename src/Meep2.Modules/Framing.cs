using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace Meep2.Modules;

/// <summary>A connection sent something Listen can't take as a message.</summary>
internal sealed class FramingException(string message) : Exception(message);

/// <summary>How Listen.TCP tells where one message ends and the next begins: lines, an end marker, or a fixed length.</summary>
internal abstract class Framing
{
    public static Framing For(TcpListenParameters parameters) => For(parameters.End, parameters.Length);

    public static Framing For(string? end, int? length) =>
        length is { } fixedLength ? new Fixed(fixedLength)
        : end is not null ? new Marked(Encoding.UTF8.GetBytes(end), end)
        : new Lines();

    /// <summary>What ends a message, written after each reply by default.</summary>
    public abstract string Ending { get; }

    /// <summary>Reads messages until the connection ends. A message without its end at the very end still counts.</summary>
    public async IAsyncEnumerable<byte[]> ReadAsync(Stream stream, long limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (TryRead(ref buffer, out var message))
                {
                    if (message.Length > Math.Max(limit, MinimumLimit))
                        throw new FramingException($"sent a message longer than {limit} bytes, so its connection was closed");
                    yield return message;
                }
                if (buffer.Length > Math.Max(limit, MinimumLimit))
                    throw new FramingException($"sent a message longer than {limit} bytes, so its connection was closed");
                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                        yield return Last(buffer);
                    yield break;
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A message's size can always reach this, whatever max-size says.</summary>
    protected virtual long MinimumLimit => 0;

    /// <summary>Takes one whole message off the front of the buffer, if there is one.</summary>
    protected abstract bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message);

    /// <summary>What's left when the connection ends: the last message, without its end.</summary>
    protected virtual byte[] Last(ReadOnlySequence<byte> buffer) => buffer.ToArray();

    // Each line is a message. A \r before the \n is dropped.
    private sealed class Lines : Framing
    {
        public override string Ending => "\n";

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            if (buffer.PositionOf((byte)'\n') is not { } end)
            {
                message = [];
                return false;
            }
            message = WithoutReturn(buffer.Slice(0, end));
            buffer = buffer.Slice(buffer.GetPosition(1, end));
            return true;
        }

        protected override byte[] Last(ReadOnlySequence<byte> buffer) => WithoutReturn(buffer);

        private static byte[] WithoutReturn(ReadOnlySequence<byte> line) =>
            line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r' ? line.Slice(0, line.Length - 1).ToArray() : line.ToArray();
    }

    // Messages end with a marker, such as "\r\n.\r\n", which isn't part of them.
    private sealed class Marked(byte[] marker, string text) : Framing
    {
        public override string Ending => text;

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> found, marker, advancePastDelimiter: true))
            {
                message = [];
                return false;
            }
            message = found.ToArray();
            buffer = buffer.Slice(reader.Position);
            return true;
        }
    }

    // Every message is the same number of bytes.
    private sealed class Fixed(int length) : Framing
    {
        public override string Ending => "";

        protected override long MinimumLimit => length;

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            if (buffer.Length < length)
            {
                message = [];
                return false;
            }
            message = buffer.Slice(0, length).ToArray();
            buffer = buffer.Slice(length);
            return true;
        }

        protected override byte[] Last(ReadOnlySequence<byte> buffer) =>
            throw new FramingException($"closed its connection {buffer.Length} bytes into a {length}-byte message, so that message was dropped");
    }
}
