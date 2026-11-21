using System.Diagnostics;

namespace Meep2.Daemon;

/// <summary>Waiting for programs Meep starts, such as ssh-keygen, launchctl and systemctl.</summary>
internal static class Programs
{
    /// <summary>
    /// Waits for a program to exit, and kills it, with anything it started, if the wait is cancelled, so it isn't left
    /// running, perhaps waiting for a passphrase nobody will type (M6).
    /// </summary>
    public static async Task WaitOrKillAsync(this Process process, CancellationToken cancellationToken)
    {
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
