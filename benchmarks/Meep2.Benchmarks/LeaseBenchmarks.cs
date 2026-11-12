using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Meep2.Benchmarks;

/// <summary>
/// The cost of leases: source → 4 modules → sink, with messages that carry no lease, all the same lease (as lines
/// from one connection do), or a lease each (as HTTP requests do). Times and allocations are per message.
/// </summary>
[MemoryDiagnoser]
public class LeaseBenchmarks
{
    [Params("None", "Shared", "Each")]
    public string Leases { get; set; } = "None";

    [Benchmark(OperationsPerInvoke = ChainBenchmarks.MessagesPerRun)]
    public async Task Chain()
    {
        var builder = new PipelineBuilder("Leases", new PipelineOptions { BufferSize = ChainBenchmarks.MessagesPerRun });
        var previous = builder.AddSource(() => new LeasingCounter(ChainBenchmarks.MessagesPerRun, Leases));
        for (var i = 0; i < 4; i++)
        {
            var step = builder.Add(() => new ChainBenchmarks.PassOn());
            builder.Connect(previous, step);
            previous = step;
        }
        var sink = new ChainBenchmarks.Sink();
        builder.Connect(previous, builder.Add(() => sink));

        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(ChainBenchmarks.MessagesPerRun);
    }

    private sealed class NothingLease : Lease
    {
        protected override ValueTask OnClosedAsync() => ValueTask.CompletedTask;
    }

    private sealed class LeasingCounter(int count, string leases) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            var shared = new NothingLease();
            for (var i = 0; i < count; i++)
            {
                switch (leases)
                {
                    case "Shared":
                        context.Emit(new Count(i), shared);
                        break;
                    case "Each":
                        context.Emit(new Count(i), new NothingLease());
                        break;
                    default:
                        context.Emit(new Count(i));
                        break;
                }
            }
            if (leases != "Shared")
                _ = shared.CloseAsync();
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// One HTTP request at a time from a client on this machine: Meep's Listen → Send, against a bare Kestrel server
/// set up the same way and answering directly, to show what the pipeline adds. Times are per request, end to end.
/// </summary>
[MemoryDiagnoser]
public class HttpBenchmarks
{
    private static readonly byte[] s_ok = Encoding.UTF8.GetBytes("ok");
    private readonly HttpClient _client = new();
    private Pipeline _pipeline = null!;
    private Task _running = null!;
    private WebApplication _bare = null!;
    private string _meepUrl = "";
    private string _bareUrl = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _meepUrl = $"http://127.0.0.1:{FreePort()}/";
        var builder = new PipelineBuilder("Http");
        builder.Connect(
            builder.AddSource(() => new HttpListen(new HttpListenParameters { Url = _meepUrl })),
            builder.Add(() => new SendReply(new SendReplyParameters { Text = Computed<string>.Constant("ok") })));
        _pipeline = builder.Build();
        _running = _pipeline.RunAsync();

        var port = FreePort();
        _bareUrl = $"http://127.0.0.1:{port}/";
        var bare = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        bare.WebHost.UseKestrelCore();
        bare.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
        });
        _bare = bare.Build();
        _bare.Run(async context =>
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength = s_ok.Length;
            await context.Response.Body.WriteAsync(s_ok);
        });
        await _bare.StartAsync();

        // Wait until Meep's listener is up.
        for (var tries = 0; ; tries++)
        {
            try
            {
                await Meep();
                break;
            }
            catch (HttpRequestException) when (tries < 200)
            {
                await Task.Delay(10);
            }
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _pipeline.Stop();
        await _running;
        await _pipeline.DisposeAsync();
        await _bare.StopAsync();
        await _bare.DisposeAsync();
        _client.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<string> Bare() => _client.GetStringAsync(_bareUrl);

    [Benchmark]
    public Task<string> Meep() => _client.GetStringAsync(_meepUrl);

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
