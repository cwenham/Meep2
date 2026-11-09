using BenchmarkDotNet.Attributes;
using Meep2.Meeplang.Syntax;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>A signed copy of the test plugin in a folder of its own, and settings that trust its key.</summary>
internal sealed class SignedPlugin : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"meep-bench-{Guid.NewGuid():N}");

    public SignedPlugin()
    {
        var plugin = Path.Combine(_folder, "shout");
        Directory.CreateDirectory(plugin);
        // The plugin and its library, as they were copied beside the benchmarks.
        foreach (var file in new[] { "Meep2.TestPlugin.dll", "Meep2.TestPlugin.Words.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(plugin, file));
        using var key = SshKeyPair.CreateEd25519("bench");
        PluginSignature.Sign(plugin, key);
        Options = new PluginOptions { Home = Path.Combine(_folder, "home"), Trusted = new TrustedKeys([key.Public]) };
        Directive = new PluginDirective(PluginOrigin.Local, Path.Combine(plugin, "Meep2.TestPlugin.dll"), null, null, default);
    }

    public PluginOptions Options { get; }

    public PluginDirective Directive { get; }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

/// <summary>
/// A module running from a plugin's load context, which can unload, against the same module loaded as usual:
/// source → Shout → sink. Times and allocations are per message.
/// </summary>
[MemoryDiagnoser]
public class PluginBenchmarks
{
    private SignedPlugin _plugin = null!;
    private PluginHold _hold = null!;
    private Func<AInflection> _shout = null!;

    [Params("Plugin", "Referenced")]
    public string Loaded { get; set; } = "Plugin";

    [GlobalSetup]
    public async Task Setup()
    {
        _plugin = new SignedPlugin();
        var installed = await new PluginInstaller(_plugin.Options).InstallAsync(_plugin.Directive);
        _hold = new PluginCatalog().Acquire(installed);
        var assembly = Loaded == "Plugin" ? _hold.Plugin.Assembly : typeof(TestPlugin.Shout).Assembly;

        var parametersType = assembly.GetType("Meep2.TestPlugin.ShoutParameters")!;
        var parameters = Activator.CreateInstance(parametersType)!;
        parametersType.GetProperty("Text")!.SetValue(parameters, Computed<string>.Constant("hi"));
        var shoutType = assembly.GetType("Meep2.TestPlugin.Shout")!;
        _shout = () => (AInflection)Activator.CreateInstance(shoutType, parameters)!;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hold.Dispose();
        _plugin.Dispose();
    }

    [Benchmark(OperationsPerInvoke = ChainBenchmarks.MessagesPerRun)]
    public async Task Shout()
    {
        var builder = new PipelineBuilder("Plugin", new PipelineOptions { BufferSize = ChainBenchmarks.MessagesPerRun });
        var sink = new Counting();
        builder.Chain(
            builder.AddSource(() => new ChainBenchmarks.Counter(ChainBenchmarks.MessagesPerRun)),
            builder.Add("Shout", _shout),
            builder.Add(() => sink));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        if (sink.Received != ChainBenchmarks.MessagesPerRun)
            throw new InvalidOperationException($"Expected {ChainBenchmarks.MessagesPerRun} messages but {sink.Received} arrived.");
    }

    private sealed class Counting : AInflection<Message>
    {
        public int Received { get; private set; }

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
        {
            Received++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Starting a pipeline's plugin when it's installed already: checking its signature (two assemblies), loading it into
/// a load context of its own, and letting it go again.
/// </summary>
[MemoryDiagnoser]
public class PluginLoadBenchmarks
{
    private SignedPlugin _plugin = null!;
    private PluginInstaller _installer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _plugin = new SignedPlugin();
        _installer = new PluginInstaller(_plugin.Options);
    }

    [GlobalCleanup]
    public void Cleanup() => _plugin.Dispose();

    [Benchmark]
    public async Task<int> CheckAndLoad()
    {
        var installed = await _installer.InstallAsync(_plugin.Directive);
        using var hold = new PluginCatalog().Acquire(installed);
        return hold.Plugin.Assembly.GetExportedTypes().Length;
    }

    [Benchmark]
    public Task<InstalledPlugin> CheckOnly() => _installer.InstallAsync(_plugin.Directive);
}
