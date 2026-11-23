using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Meep2.Daemon;
using Meep2.Plugins;

namespace Meep2.Benchmarks;

/// <summary>
/// How many API requests a second the daemon answers on its socket, each with a token, from several clients at once
/// (P3). Run with: dotnet run -c Release -- requests [--clients 8] [--seconds 10] [--rounds 3]
/// </summary>
internal static class RequestRate
{
    public static async Task<int> RunAsync(string[] args)
    {
        var clients = Option(args, "--clients", 8);
        var seconds = Option(args, "--seconds", 10);
        var rounds = Option(args, "--rounds", 3);

        var home = Directory.CreateTempSubdirectory("meep-requests-").FullName;
        var socketFolder = Directory.CreateTempSubdirectory("m").FullName;
        var options = new DaemonOptions
        {
            Plugins = new PluginOptions { Home = home },
            SocketPath = Path.Combine(socketFolder, "meep.sock"),
            Output = TextWriter.Null,
            Log = TextWriter.Null,
        };
        await using var daemon = new MeepDaemon(options);
        await daemon.StartAsync();
        var token = DaemonTokens.Create(options.TokensFile, "bench", Capabilities.Observe);
        Console.WriteLine($"{clients} clients, GET / with a token, {seconds} s a round; {Environment.ProcessorCount} processors, .NET {Environment.Version}.");

        await RoundAsync(options.Socket, token, clients, TimeSpan.FromSeconds(10)); // warming up
        for (var round = 1; round <= rounds; round++)
        {
            var (requests, cpu, allocated) = await RoundAsync(options.Socket, token, clients, TimeSpan.FromSeconds(seconds));
            Console.WriteLine($"Round {round}: {requests / seconds:N0} requests a second, {cpu / requests * 1000:N1} µs CPU and {allocated / requests:N0} B each");
        }
        return 0;
    }

    private static async Task<(long Requests, double CpuMs, long Allocated)> RoundAsync(string socket, string token, int clients, TimeSpan length)
    {
        using var stop = new CancellationTokenSource(length);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var counts = await Task.WhenAll(Enumerable.Range(0, clients).Select(_ => Task.Run(() => ClientAsync(socket, token, stop.Token))));
        var requests = counts.Sum();
        return (requests, (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - allocated);
    }

    private static async Task<long> ClientAsync(string socket, string token, CancellationToken stop)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancellationToken);
                return new NetworkStream(connection, ownsSocket: true);
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://meep/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        long count = 0;
        while (!stop.IsCancellationRequested)
        {
            using var response = await http.GetAsync("/", CancellationToken.None);
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static int Option(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? int.Parse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }
}
