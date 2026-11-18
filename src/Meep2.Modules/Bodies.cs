using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using Meep2.SDK.Messages;
using Microsoft.Extensions.Primitives;

namespace Meep2.Modules;

/// <summary>A response Fetch streamed, kept open until its body has been read and its messages have left.</summary>
internal sealed class OpenResponse(HttpResponseMessage response) : Lease
{
    protected override ValueTask OnClosedAsync()
    {
        response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A body left unread, which one module can read (Q51).</summary>
internal sealed class StreamedBody(Stream stream, string what)
{
    private int _taken;

    public Stream Open() =>
        Interlocked.Exchange(ref _taken, 1) == 0
            ? stream
            : throw new InvalidOperationException(
                $"The {what} was streamed, and another module has already read it. Only one module can read a streamed body; leave out stream=true to read it into memory instead.");
}

/// <summary>
/// A connection's stream, or a body's, that counts the bytes read from it and written to it for the module whose
/// connection it is (<see cref="Meep2.SDK.Modules.ModuleContext.CountBytes"/>).
/// </summary>
internal sealed class CountingStream(Stream inner, Action<long, long> counter) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        counter(0, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        counter(0, buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        counter(0, buffer.Length);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private int Counted(int read)
    {
        if (read > 0)
            counter(read, 0);
        return read;
    }
}

/// <summary>The body of a request was larger than the limit.</summary>
internal sealed class BodyTooLargeException() : Exception("The body is larger than the limit.");

/// <summary>Reading bodies with a size limit, and turning query values and headers into dictionaries.</summary>
internal static class Bodies
{
    public const string PlainText = "text/plain; charset=utf-8";

    public static Stream Open(ReadOnlyMemory<byte> body) =>
        MemoryMarshal.TryGetArray(body, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(body.ToArray(), writable: false);

    // Reads a whole body. A body whose length is declared is read straight into an array of that size; one sent in
    // chunks goes through a pooled buffer, so a request without a body allocates nothing.
    public static async Task<ReadOnlyMemory<byte>> ReadAsync(Stream stream, long declaredLength, long limit, CancellationToken cancellationToken)
    {
        if (declaredLength > limit)
            throw new BodyTooLargeException();
        if (declaredLength == 0)
            return ReadOnlyMemory<byte>.Empty;
        if (declaredLength > 0)
        {
            var body = new byte[declaredLength];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            return body;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new BodyTooLargeException();
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return buffer.ToArray();
    }

    // Query values and headers by name, ignoring case. A name given more than once has its values joined with commas.
    public static IReadOnlyDictionary<string, string> ToDictionary(IEnumerable<KeyValuePair<string, StringValues>> values)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
            dictionary[key] = value.ToString();
        return dictionary;
    }
}

/// <summary>Reads the addresses Listen takes.</summary>
internal static class Endpoints
{
    /// <summary>The host and port of a tcp:// address.</summary>
    public static bool TryTcp(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "tcp" || uri.Port is < 1 or > 65535)
            return false;
        host = uri.Host.Trim('[', ']');
        port = uri.Port;
        return true;
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (host is "0.0.0.0" or "*")
            return IPAddress.Any;
        if (host == "::")
            return IPAddress.IPv6Any;
        if (IPAddress.TryParse(host, out var address))
            return address;
        if (host == "localhost")
            return IPAddress.Loopback;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0 ? addresses[0] : throw new InvalidOperationException($"Couldn't find an address for {host}.");
    }
}
