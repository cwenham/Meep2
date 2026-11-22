using Meep2.Daemon;

namespace Meep2.Cli;

/// <summary>What every command that asks the daemon for something uses: its connection, where to write, and Ctrl+C.</summary>
/// <param name="Client">The connection to the daemon.</param>
/// <param name="Output">Where to write what happened.</param>
/// <param name="Error">Where to write what went wrong.</param>
/// <param name="Cancellation">Cancelled by Ctrl+C.</param>
internal sealed record CliContext(DaemonClient Client, TextWriter Output, TextWriter Error, CancellationToken Cancellation);
