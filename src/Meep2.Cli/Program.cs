using Meep2.Cli;

// The first Ctrl+C asks the pipeline to stop, letting messages already in it finish. A second one quits at once.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (stop.IsCancellationRequested)
        return;
    e.Cancel = true;
    Console.Error.WriteLine("Stopping. Press Ctrl+C again to quit at once.");
    stop.Cancel();
};

return await MeepCommand.RunAsync(args, Console.Out, Console.Error, stop.Token);
