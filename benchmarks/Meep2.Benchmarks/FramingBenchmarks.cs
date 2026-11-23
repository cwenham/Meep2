using BenchmarkDotNet.Attributes;
using Meep2.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// Listen.TCP finding where a large message ends when it arrives in 4 KB pieces, as it does over a network (E7): one
/// message of 64 KB or 1 MB, ended by a newline or by a marker. Times and allocations are per message.
/// </summary>
[MemoryDiagnoser]
public class FramingBenchmarks
{
    private byte[] _bytes = [];
    private Framing _framing = null!;

    [Params("Lines", "Marked")]
    public string Ending { get; set; } = "Lines";

    [Params(64 * 1024, 1024 * 1024)]
    public int Size { get; set; } = 64 * 1024;

    [GlobalSetup]
    public void Setup()
    {
        var end = Ending == "Lines" ? "\n" : "\r\n.\r\n";
        _bytes = [.. Enumerable.Repeat((byte)'x', Size), .. System.Text.Encoding.ASCII.GetBytes(end)];
        _framing = Framing.For(Ending == "Lines" ? null : end, null);
    }

    [Benchmark]
    public async Task<int> Read()
    {
        var count = 0;
        await foreach (var message in _framing.ReadAsync(new Pieces(_bytes, 4096), long.MaxValue, CancellationToken.None))
            count += message.Length;
        return count == Size ? count : throw new InvalidOperationException($"Read {count} bytes, not {Size}.");
    }

    // A stream that gives at most so many bytes for each read.
    private sealed class Pieces(byte[] bytes, int piece) : Stream
    {
        private int _at;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(piece, buffer.Length), bytes.Length - _at);
            bytes.AsSpan(_at, take).CopyTo(buffer);
            _at += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
