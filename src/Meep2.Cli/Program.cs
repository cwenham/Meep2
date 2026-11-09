using System.Runtime.InteropServices;
using Meep2.Cli;

// meep run --detach's background process sends its output to a log, before anything is written.
Detaching.Begin(args);

// The first Ctrl+C, or a kill, asks the pipeline to stop, letting messages already in it finish. A second Ctrl+C
// quits at once.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (stop.IsCancellationRequested)
        return;
    e.Cancel = true;
    Console.Error.WriteLine("Stopping. Press Ctrl+C again to quit at once.");
    stop.Cancel();
};
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    if (stop.IsCancellationRequested)
        return;
    context.Cancel = true; // stop gracefully rather than at once
    Console.Error.WriteLine("Stopping, as asked.");
    stop.Cancel();
});

return await MeepCommand.RunAsync(args, Console.Out, Console.Error, stop.Token);
