using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Primitives;

namespace Meep2.Modules;

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

    // Text read as File.ReadAllText reads it: UTF-8 unless a byte order mark says otherwise.
    public static string Decode(ReadOnlyMemory<byte> bytes)
    {
        using var reader = new StreamReader(Open(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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
