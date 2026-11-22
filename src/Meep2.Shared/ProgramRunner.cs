using System.Diagnostics;

namespace Meep2.Shared;

/// <summary>
/// Runs programs Meep relies on, such as ssh-keygen, launchctl and systemctl, with arguments as a list (never through
/// a shell), reading what they say, and killing them if the wait is cancelled so they aren't left running (M6).
/// </summary>
public static class ProgramRunner
{
    /// <summary>Runs a program to the end and returns what it said.</summary>
    /// <param name="program">The program, by name on the PATH or by path.</param>
    /// <param name="arguments">Its arguments, each passed as it is.</param>
    /// <param name="input">Text to write to its standard input, which is then closed; null leaves its input alone.</param>
    /// <param name="environment">Variables to set for it, besides those it inherits.</param>
    /// <param name="cancellationToken">Kills it, and anything it started, when cancelled.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">The program isn't there to run.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled; the program has been killed.</exception>
    public static async Task<ProgramResult> RunAsync(
        string program,
        IEnumerable<string> arguments,
        string? input = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't run {program}.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        await process.WaitOrKillAsync(cancellationToken).ConfigureAwait(false);
        return new ProgramResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>
    /// Waits for a program to exit, and kills it, with anything it started, if the wait is cancelled, so it isn't left
    /// running, perhaps waiting for a passphrase nobody will type (M6).
    /// </summary>
    public static async Task WaitOrKillAsync(this Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited meanwhile.
            }
            throw;
        }
    }
}
