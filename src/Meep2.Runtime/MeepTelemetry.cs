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
/// parent is the span its message came from, so a trace follows fam. A message refused at a gate adds a
/// <c>meep.permission.denied</c> event to the span of the module that emitted it, with <c>meep.module</c> (the gate),
/// <c>meep.message.id</c>, <c>meep.permission</c> and <c>meep.permission.reasons</c>, so traces are an audit of refusals. Across hosts, Fetch and Send send
/// <c>traceparent</c> naming their span, and a Listen's span for the request continues it.
/// </para>
/// <para>
/// Metrics, tagged with <c>meep.pipeline</c> and <c>meep.module</c>: <c>meep.module.messages</c> (by
/// <c>meep.event</c>: received, handled, emitted, dropped, filtered, expired, refused, denied), <c>meep.module.failures</c>
/// (failed, restarted), <c>meep.module.queued</c>, and the histogram <c>meep.module.duration</c>.
/// </para>
/// </remarks>
public static class MeepTelemetry
{
    /// <summary>The name of Meep's activity source and meter.</summary>
    public const string Name = "Meep2";
}
