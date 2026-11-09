using System.Diagnostics;
using System.Security;
using System.Text;

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

/// <summary>
/// Sets the daemon up as a service that starts when you log in and is restarted if it fails (Q116): a launchd agent on
/// macOS, a systemd user service on Linux. <c>meep daemon install</c> and <c>meep daemon uninstall</c>.
/// </summary>
public static class DaemonService
{
    /// <summary>The launchd agent's label.</summary>
    public const string Label = "io.github.cwenham.meep";

    /// <summary>The systemd user service's name.</summary>
    public const string Unit = "meep.service";

    /// <summary>Whether this system has a service manager Meep knows.</summary>
    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Plans the service for this system.</summary>
    /// <param name="command">The command that runs the daemon: meep's path, then <c>daemon</c> and its options.</param>
    /// <param name="home">Meep's folder, where the daemon keeps its socket and, under launchd, its logs.</param>
    /// <param name="environment">Variables the daemon needs, such as <c>DOTNET_ROOT</c> and <c>MEEP_HOME</c>.</param>
    /// <exception cref="PlatformNotSupportedException">This isn't macOS or Linux.</exception>
    public static ServicePlan Plan(IReadOnlyList<string> command, string home, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Launchd(command, home, environment, Path.Combine(user, "Library", "LaunchAgents", Label + ".plist"), PeerCredentials.CurrentUser ?? 0);
        if (OperatingSystem.IsLinux())
            return Systemd(command, environment, Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(user, ".config"), "systemd", "user", Unit));
        throw new PlatformNotSupportedException("meep daemon install knows launchd, on macOS, and systemd, on Linux. On Windows, run meep daemon in a terminal of its own for now.");
    }

    /// <summary>A launchd agent, for macOS.</summary>
    public static ServicePlan Launchd(IReadOnlyList<string> command, string home, IReadOnlyDictionary<string, string> environment, string file, uint user)
    {
        var logs = Path.Combine(home, "daemon");
        var plist = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        plist.Append($"    <key>Label</key>\n    <string>{Label}</string>\n");
        plist.Append("    <key>ProgramArguments</key>\n    <array>\n");
        foreach (var word in command)
            plist.Append($"        <string>{SecurityElement.Escape(word)}</string>\n");
        plist.Append("    </array>\n");
        if (environment.Count > 0)
        {
            plist.Append("    <key>EnvironmentVariables</key>\n    <dict>\n");
            foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
                plist.Append($"        <key>{SecurityElement.Escape(name)}</key>\n        <string>{SecurityElement.Escape(value)}</string>\n");
            plist.Append("    </dict>\n");
        }
        plist.Append("""
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ProcessType</key>
                <string>Background</string>

            """);
        plist.Append($"    <key>StandardOutPath</key>\n    <string>{SecurityElement.Escape(Path.Combine(logs, "output.log"))}</string>\n");
        plist.Append($"    <key>StandardErrorPath</key>\n    <string>{SecurityElement.Escape(Path.Combine(logs, "daemon.log"))}</string>\n");
        plist.Append("</dict>\n</plist>\n");

        var domain = $"gui/{user}";
        return new ServicePlan(
            "launchd",
            file,
            plist.ToString(),
            [new(["launchctl", "bootout", $"{domain}/{Label}"], MayFail: true), new(["launchctl", "bootstrap", domain, file])],
            [new(["launchctl", "bootout", $"{domain}/{Label}"], MayFail: true)],
            $"{Path.Combine(logs, "daemon.log")}, and its pipelines' output in {Path.Combine(logs, "output.log")}");
    }

    /// <summary>A systemd user service, for Linux.</summary>
    public static ServicePlan Systemd(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment, string file)
    {
        var unit = new StringBuilder("""
            [Unit]
            Description=Meep daemon, which hosts Meeplang pipelines

            [Service]

            """);
        unit.Append("ExecStart=").AppendJoin(' ', command.Select(Quote)).Append('\n');
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
            unit.Append("Environment=").Append(Quote($"{name}={value}")).Append('\n');
        unit.Append("""
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target

            """);
        return new ServicePlan(
            "systemd",
            file,
            unit.ToString(),
            [new(["systemctl", "--user", "daemon-reload"]), new(["systemctl", "--user", "enable", Unit]), new(["systemctl", "--user", "restart", Unit])],
            [new(["systemctl", "--user", "disable", "--now", Unit], MayFail: true)],
            $"journalctl --user -u {Unit}");

        // systemd's own quoting: double quotes, with backslashes and quotes escaped, and % doubled.
        static string Quote(string word) =>
            "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Writes the service's file and runs the commands that start it.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="home">Meep's folder, made first so the service's logs have somewhere to go.</param>
    /// <exception cref="InvalidOperationException">A command failed. The message has what it said.</exception>
    public static async Task InstallAsync(ServicePlan plan, string home, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var daemon = Path.Combine(home, "daemon");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(daemon);
        else
            Directory.CreateDirectory(daemon, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.File)!);
        await File.WriteAllTextAsync(plan.File, plan.Content, cancellationToken).ConfigureAwait(false);
        foreach (var command in plan.Install)
            await RunAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the service and deletes its file. Returns false if it wasn't installed.</summary>
    public static async Task<bool> UninstallAsync(ServicePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!File.Exists(plan.File))
            return false;
        foreach (var command in plan.Uninstall)
            await RunAsync(command, cancellationToken).ConfigureAwait(false);
        File.Delete(plan.File);
        if (plan.System == "systemd")
            await RunAsync(new ServiceCommand(["systemctl", "--user", "daemon-reload"], MayFail: true), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task RunAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.Words[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var word in command.Words.Skip(1))
            start.ArgumentList.Add(word);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't run {command}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 && !command.MayFail)
            throw new InvalidOperationException($"{command} failed with {process.ExitCode}: {(await error.ConfigureAwait(false) + await output.ConfigureAwait(false)).Trim()}");
    }
}
