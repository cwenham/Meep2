namespace Meep2.Runtime;

/// <summary>
/// The name Meep's OpenTelemetry data goes out under, for an exporter to listen to: its spans come from an
/// <see cref="System.Diagnostics.ActivitySource"/> and its metrics from a <see cref="System.Diagnostics.Metrics.Meter"/>,
/// both called this (Q43).
/// </summary>
/// <remarks>
/// <para>
/// Spans: one each time a module handles a message, named by its verb and inflection, such as <c>Fetch.HTTP</c>, with
/// the tags <c>meep.pipeline</c>, <c>meep.module</c>, <c>meep.message.type</c> and <c>meep.message.id</c>. A span's
/// parent is the span its message came from, so a trace follows fam.
/// </para>
/// <para>
/// Metrics, tagged with <c>meep.pipeline</c> and <c>meep.module</c>: <c>meep.module.messages</c> (by
/// <c>meep.event</c>: received, handled, emitted, dropped, filtered, expired, refused), <c>meep.module.failures</c>
/// (failed, restarted), <c>meep.module.queued</c>, and the histogram <c>meep.module.duration</c>.
/// </para>
/// </remarks>
public static class MeepTelemetry
{
    /// <summary>The name of Meep's activity source and meter.</summary>
    public const string Name = "Meep2";
}
