using Meep2.Runtime;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meep2.Cli;

/// <summary>
/// Sends Meep's spans and metrics to an OpenTelemetry collector (Q43), when <c>--otlp</c> gives one or the standard
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable is set. Without either, nothing listens, so it costs nothing.
/// </summary>
/// <remarks>
/// It records one trace in ten, decided where each message enters the pipeline, so a trace is whole or not there
/// at all (Q114). <c>--sample</c> sets the share, and <c>--sample 1</c> records them all. Without <c>--sample</c>,
/// the standard <c>OTEL_TRACES_SAMPLER</c> variable decides, if it's set.
/// </remarks>
internal static class Telemetry
{
    /// <summary>The share of traces recorded unless something says otherwise.</summary>
    public const double DefaultSample = 0.1;

    /// <summary>Starts exporting, or returns null if there's nowhere to export to. Dispose it to send what's left.</summary>
    /// <param name="otlp">A collector's OTLP/HTTP address, such as http://localhost:4318, or null to use the standard variables.</param>
    /// <param name="sample">The share of traces to record, or null for the default.</param>
    public static IDisposable? Start(string? otlp, double? sample = null)
    {
        if (otlp is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            return null;

        var resource = ResourceBuilder.CreateDefault();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")))
            resource.AddService("meep");
        var traces = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(MeepTelemetry.Name)
            .AddOtlpExporter(exporter => Configure(exporter, otlp, "v1/traces"));
        if (sample is not null || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER")))
            traces.SetSampler(Sampler(sample ?? DefaultSample));
        var tracing = traces.Build();
        var metrics = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(MeepTelemetry.Name)
            .AddOtlpExporter(exporter => Configure(exporter, otlp, "v1/metrics"))
            .Build();
        return new Providers(tracing, metrics);
    }

    /// <summary>
    /// Records that share of traces, decided by each trace's id where it starts; everything after follows its
    /// parent, so a trace is never cut short.
    /// </summary>
    public static Sampler Sampler(double share) =>
        new ParentBasedSampler(share >= 1 ? new AlwaysOnSampler() : new TraceIdRatioBasedSampler(share));

    // With --otlp, OTLP over HTTP to that address. Otherwise the exporter reads the standard variables itself.
    private static void Configure(OtlpExporterOptions exporter, string? otlp, string signal)
    {
        if (otlp is null)
            return;
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = new Uri(new Uri(otlp.TrimEnd('/') + "/"), signal);
    }

    private sealed class Providers(TracerProvider tracing, MeterProvider metrics) : IDisposable
    {
        public void Dispose()
        {
            tracing.Dispose();
            metrics.Dispose();
        }
    }
}
