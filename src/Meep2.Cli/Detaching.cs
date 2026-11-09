using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Meep2.Cli;

/// <summary>
/// <c>meep run --detach</c> (Q116): runs a pipeline in the background, as a process of its own that outlives the
/// terminal, with its output going to a log file. The command starts meep again with <c>--detached-to</c>, waits
/// until the pipeline is running, and reports its process id; a pipeline that doesn't load is reported as it would
/// be in the foreground.
/// </summary>
/// <remarks>
/// The background process sends its output to the log, leaves the terminal's session so closing the terminal
/// doesn't stop it, and tells the command it's running on what was its standard output. <c>kill</c> stops it as
/// Ctrl+C would, letting messages finish.
/// </remarks>
internal static class Detaching
{
    private static FileStream? s_ready;

    /// <summary>Starts the background process and waits for its pipeline to run. Returns the exit code.</summary>
    public static async Task<int> StartAsync(HostSettings settings, string home, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync("--detach isn't supported on Windows yet. Run the pipeline in the daemon instead: meep daemon, then meep start <file.meep>").ConfigureAwait(false);
            return 2;
        }
        var file = Path.GetFullPath(settings.Files[0]);
        if (!File.Exists(file))
        {
            await error.WriteLineAsync($"There's no file {file}.").ConfigureAwait(false);
            return 1;
        }
        var name = Path.GetFileNameWithoutExtension(file);
        var logs = Path.Combine(home, "logs");
        Directory.CreateDirectory(logs);
        var log = Path.Combine(logs, name + ".log");
        var before = File.Exists(log) ? new FileInfo(log).Length : 0;

        // All three of its standard handles are pipes of its own. The .NET runtime keeps copies of the handles a
        // process starts with, so if it were given the terminal's, it would hold them open for as long as it runs.
        var command = MeepExecutable.Command();
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var word in command.Skip(1).Concat(["run", .. settings.Options(), "--detached-to", log, file]))
            start.ArgumentList.Add(word);
        start.Environment["MEEP_HOME"] = home;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start meep in the background.");
        child.StandardInput.Close();
        _ = child.StandardError.ReadToEndAsync(cancellationToken); // it goes to the log, but in case anything comes first

        string? said;
        try
        {
            said = await child.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await output.WriteLineAsync($"{name} is still loading after five minutes, as process {child.Id}. Its output goes to {log}").ConfigureAwait(false);
            return 0;
        }
        if (said == "running")
        {
            await output.WriteLineAsync($"""
                Running {name} in the background, as process {child.Id}.
                Its output and errors go to {log}
                Stop it with: kill {child.Id}
                """).ConfigureAwait(false);
            return 0;
        }

        // It ended without running: show what it said, as the foreground would have.
        await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await using (var reading = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            reading.Seek(before, SeekOrigin.Begin);
            using var reader = new StreamReader(reading);
            await error.WriteAsync(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        return child.ExitCode == 0 ? 1 : child.ExitCode;
    }

    /// <summary>
    /// In the background process, before anything writes to the console: sends standard output and error to the log,
    /// reads nothing, leaves the terminal's session, and keeps what was standard output to say when it's running.
    /// </summary>
    public static void Begin(string[] args)
    {
        var at = Array.IndexOf(args, "--detached-to");
        if (at < 0 || at + 1 >= args.Length || OperatingSystem.IsWindows())
            return;
        var ready = Dup(1);
        _ = Fcntl(ready, 2 /* F_SETFD */, 1 /* FD_CLOEXEC */); // programs it starts mustn't hold the pipe open
        using (var log = File.OpenHandle(args[at + 1], FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var fd = (int)log.DangerousGetHandle();
            Dup2(fd, 1);
            Dup2(fd, 2);
        }
        using (var nothing = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read))
            Dup2((int)nothing.DangerousGetHandle(), 0);
        _ = SetSid();
        s_ready = new FileStream(new SafeFileHandle(ready, ownsHandle: true), FileAccess.Write, bufferSize: 0);
    }

    /// <summary>Tells the command that started this process that the pipeline is running. Only the first call does anything.</summary>
    public static void Running()
    {
        if (Interlocked.Exchange(ref s_ready, null) is not { } ready)
            return;
        try
        {
            ready.Write("running\n"u8);
        }
        catch (IOException)
        {
            // The command has gone, as after Ctrl+C. The pipeline runs on regardless.
        }
        ready.Dispose();
    }

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Dup2(int from, int to);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSid();
}
