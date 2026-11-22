using BenchmarkDotNet.Attributes;
using Meep2.Daemon;

namespace Meep2.Benchmarks;

/// <summary>What checking a token costs a request (P3): finding the token, and seeing whether the tokens file has changed.</summary>
[MemoryDiagnoser]
public class AccessBenchmarks
{
    private string _folder = "";
    private DaemonAccess _access = null!;
    private string _authorization = "";

    [GlobalSetup]
    public void Setup()
    {
        _folder = Directory.CreateTempSubdirectory("meep-access-").FullName;
        var file = Path.Combine(_folder, "tokens");
        _authorization = "Bearer " + DaemonTokens.Create(file, "bench", Capabilities.Observe);
        _access = new DaemonAccess(file);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_folder, recursive: true);

    [Benchmark]
    public Capabilities CheckToken() => _access.CapabilitiesOf(_authorization, null);
}
