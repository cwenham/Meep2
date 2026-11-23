using System.IO.Pipes;
using System.Text;
using BenchmarkDotNet.Attributes;
using Meep2.Cli;

namespace Meep2.Benchmarks;

/// <summary>
/// Reading <c>meep lsp</c>'s messages from a pipe, as an editor sends them on standard input (E6): a header, a blank
/// line, then about 2 KB of JSON, as a change to a file is. Times and allocations are per message.
/// </summary>
[MemoryDiagnoser]
public class LspBenchmarks
{
    private const int Messages = 1000;
    private byte[] _frames = [];

    [GlobalSetup]
    public void Setup()
    {
        var text = new string('x', 2000);
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"text\":\"" + text + "\"}}");
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
        _frames = [.. Enumerable.Repeat(frame, Messages).SelectMany(f => f)];
    }

    [Benchmark(OperationsPerInvoke = Messages)]
    public async Task<int> Read()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var writing = Task.Run(() =>
        {
            server.Write(_frames);
            server.Dispose();
        });
        var channel = new JsonRpcChannel(client, Stream.Null);
        var read = 0;
        while (await channel.ReadAsync(CancellationToken.None) is not null)
            read++;
        await writing;
        return read == Messages ? read : throw new InvalidOperationException($"Read {read} of {Messages} messages.");
    }
}
