namespace Meep2.Daemon;

/// <summary>A command the service installer runs, such as <c>launchctl bootstrap</c>.</summary>
/// <param name="Words">The program and its arguments.</param>
/// <param name="MayFail">Whether failing is expected, as when unloading a service that isn't loaded.</param>
public sealed record ServiceCommand(IReadOnlyList<string> Words, bool MayFail = false)
{
    /// <inheritdoc/>
    public override string ToString() => string.Join(' ', Words.Select(w => w.Contains(' ', StringComparison.Ordinal) ? $"\"{w}\"" : w));
}

/// <summary>What installing the daemon as a service does: the file it writes, and the commands it runs.</summary>
/// <param name="System">launchd or systemd.</param>
/// <param name="File">The service's file.</param>
/// <param name="Content">What goes in it.</param>
/// <param name="Install">Commands that load and start it, once the file is written.</param>
/// <param name="Uninstall">Commands that stop and unload it, before the file is deleted.</param>
/// <param name="Logs">Where to see what the daemon logs.</param>
public sealed record ServicePlan(string System, string File, string Content, IReadOnlyList<ServiceCommand> Install, IReadOnlyList<ServiceCommand> Uninstall, string Logs);
