using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;

namespace Meep2.Benchmarks;

/// <summary>
/// What watching a pipeline costs, per message: a source, one step that makes a new message, and a sink, as in
/// <see cref="ChainBenchmarks"/>. Watched by nothing, by a debug tap on the step that a reader keeps up with, by
/// tracing that records every span, by tracing that records one trace in ten as <c>--otlp</c> does by default
/// (Q114), or by the handling-time histogram.
/// </summary>
[MemoryDiagnoser]
public class ObservingBenchmarks
{
    private ActivityListener? _tracing;
    private MeterListener? _metrics;

    [Params("Nothing", "Tap", "Tracing", "Sampled", "Histogram")]
    public string Watching { get; set; } = "Nothing";

    [GlobalSetup]
    public void Setup()
    {
        if (Watching == "Tracing")
        {
            _tracing = new ActivityListener
            {
                ShouldListenTo = source => source.Name == MeepTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_tracing);
        }
        else if (Watching == "Sampled")
        {
            // As OpenTelemetry's parent-based ratio sampler: a trace's root by its id, everything else as its parent.
            _tracing = new ActivityListener
            {
                ShouldListenTo = source => source.Name == MeepTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Parent != default
                        ? (options.Parent.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData
                        : (options.TraceId.GetHashCode() & 0x7fffffff) % 10 == 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
            };
            ActivitySource.AddActivityListener(_tracing);
        }
        else if (Watching == "Histogram")
        {
            _metrics = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Name == "meep.module.duration")
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _metrics.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _metrics.Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tracing?.Dispose();
        _metrics?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = ChainBenchmarks.MessagesPerRun)]
    public async Task Chain()
    {
        var builder = new PipelineBuilder("Watched", new PipelineOptions { BufferSize = ChainBenchmarks.MessagesPerRun });
        var source = builder.AddSource(() => new ChainBenchmarks.Counter(ChainBenchmarks.MessagesPerRun));
        var step = builder.Add(() => new ChainBenchmarks.Increment());
        var sink = new ChainBenchmarks.Sink();
        builder.Chain(source, step, builder.Add(() => sink));
        await using var pipeline = builder.Build();

        Task reading = Task.CompletedTask;
        DebugTap? tap = null;
        if (Watching == "Tap")
        {
            tap = pipeline.Tap(step, new DebugTapOptions { BufferSize = 1024 });
            reading = Task.Run(async () =>
            {
                await foreach (var _ in tap.Messages.ReadAllAsync())
                {
                }
            });
        }
        await pipeline.RunAsync();
        await reading;
        tap?.Dispose();
        sink.Check(ChainBenchmarks.MessagesPerRun);
    }
}

/// <summary>Writing a message as JSON for a debug tap.</summary>
[MemoryDiagnoser]
public class MessageJsonBenchmarks
{
    private readonly Tick _tick = new(42);
    private readonly HttpRequest _request = new()
    {
        Method = "POST",
        Url = "http://127.0.0.1:8080/quote?symbol=MEEP",
        Path = "/quote",
        Query = new Dictionary<string, string> { ["symbol"] = "MEEP" },
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["User-Agent"] = "bench" },
        ContentType = "application/json",
        Body = "{\"x\":7}"u8.ToArray(),
        Text = "{\"x\":7}",
    };

    [Benchmark]
    public string Tick() => MessageJson.ToJson(_tick);

    [Benchmark]
    public string Request() => MessageJson.ToJson(_request);
}
