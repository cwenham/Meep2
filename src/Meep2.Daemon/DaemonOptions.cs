using Meep2.Plugins;

namespace Meep2.Daemon;

/// <summary>How a <see cref="MeepDaemon"/> runs.</summary>
public sealed record DaemonOptions
{
    /// <summary>Where plugins come from, and Meep's own folder, <c>~/.meep</c> or <c>MEEP_HOME</c>, which holds the daemon's socket and tokens.</summary>
    public required PluginOptions Plugins { get; init; }

    /// <summary>The control socket's path, unless it's the usual <see cref="DefaultSocket"/>.</summary>
    public string? SocketPath { get; init; }

    /// <summary>Where the pipelines' WriteLine output goes.</summary>
    public TextWriter Output { get; init; } = Console.Out;

    /// <summary>Where the daemon logs what happens: pipelines starting and finishing, their warnings and the errors they leave unhandled.</summary>
    public TextWriter Log { get; init; } = Console.Error;

    /// <summary>
    /// Pipeline files to start once the daemon is listening, from its command line and its settings file (Q112). The
    /// daemon doesn't remember what it ran before, so these are the only ones it starts by itself.
    /// </summary>
    public IReadOnlyList<DaemonStart> Start { get; init; } = [];

    /// <summary>The control socket.</summary>
    public string Socket => SocketPath ?? DefaultSocket(Plugins.Home);

    /// <summary>The file of tokens that give clients narrower capabilities.</summary>
    public string TokensFile => Path.Combine(Plugins.Home, "daemon", "tokens");

    /// <summary>The usual control socket for a Meep folder: <c>daemon/meep.sock</c> inside it.</summary>
    public static string DefaultSocket(string home) => Path.Combine(home, "daemon", "meep.sock");
}
