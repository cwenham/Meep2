using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// The daemon's process and machine, as .NET's own runtime metrics give them: the <c>System.Runtime</c> meter's CPU
/// time, memory, garbage collection, thread pool and so on, by their OpenTelemetry names, with the machine's load and
/// memory beside them. Read when a subscription asks, at most once every 100 ms however many ask.
/// </summary>
/// <remarks>
/// Each metric is a number, or for one with attributes, such as <c>process.cpu.time</c> by <c>cpu.mode</c>, a map from
/// the attributes' values (joined with <c>/</c> when there are several) to numbers: <c>{"user": 12.5, "system": 3.1}</c>.
/// Counters count up from when the daemon started, as OpenTelemetry's cumulative counters do.
/// </remarks>
internal sealed class RuntimeMetrics : IDisposable
{
    /// <summary>The meter .NET's runtime metrics come from.</summary>
    public const string Meter = "System.Runtime";

    private readonly MeterListener _listener = new();
    private readonly Lock _reading = new();
    private readonly SortedDictionary<string, SortedDictionary<string, double>> _values = new(StringComparer.Ordinal);
    private Dictionary<string, Metric> _last = [];
    private long? _readAt;

    public RuntimeMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>One metric: a single number, or numbers by the values of its attributes.</summary>
    public readonly record struct Metric(double? Value, IReadOnlyList<KeyValuePair<string, double>>? Values)
    {
        public bool SameAs(Metric other) =>
            Value == other.Value
            && (Values is null ? other.Values is null : other.Values is not null && Values.SequenceEqual(other.Values));
    }

    /// <summary>The metrics now, by name.</summary>
    public IReadOnlyDictionary<string, Metric> Read()
    {
        lock (_reading)
        {
            var now = Environment.TickCount64;
            if (now - _readAt < 100) // null the first time, so false
                return _last;
            _readAt = now;
            _listener.RecordObservableInstruments(); // observed values replace the last ones; counters add up as they go
            Machine();
            var read = new Dictionary<string, Metric>(StringComparer.Ordinal);
            lock (_values)
            {
                foreach (var (name, values) in _values)
                {
                    read[name] = values.Count == 1 && values.ContainsKey("")
                        ? new Metric(values[""], null)
                        : new Metric(null, [.. values]);
                }
            }
            return _last = read;
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        var key = tags.Length switch
        {
            0 => "",
            1 => tags[0].Value?.ToString() ?? "",
            _ => string.Join('/', tags.ToArray().Select(t => t.Value?.ToString() ?? "")),
        };
        var number = value.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
        lock (_values)
        {
            if (!_values.TryGetValue(instrument.Name, out var values))
                _values[instrument.Name] = values = new SortedDictionary<string, double>(StringComparer.Ordinal);
            if (instrument.IsObservable)
                values[key] = number;
            else
                values[key] = values.GetValueOrDefault(key) + number; // a counter's increments, as they happen
        }
    }

    // What .NET's metrics don't say about the machine: how much memory it has (or the container allows), and its
    // load averages, where the system has them.
    private void Machine()
    {
        lock (_values)
        {
            _values["host.memory.available"] = new(StringComparer.Ordinal) { [""] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes };
            if (!OperatingSystem.IsWindows())
            {
                var load = new double[3];
                if (GetLoadAverage(load, 3) == 3)
                    _values["host.load"] = new(StringComparer.Ordinal) { ["1m"] = load[0], ["5m"] = load[1], ["15m"] = load[2] };
            }
        }
    }

    [DllImport("libc", EntryPoint = "getloadavg")]
    private static extern int GetLoadAverage([Out] double[] loads, int count);
}
