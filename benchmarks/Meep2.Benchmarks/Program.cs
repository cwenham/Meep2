using BenchmarkDotNet.Running;

// What a subscription costs against the stats streams, measured over a running daemon rather than by BenchmarkDotNet:
// dotnet run -c Release -- subscriptions [--pipelines 50] [--modules 20] [--seconds 10] [--tick-ms 10]
if (args is ["subscriptions", ..])
    return await Meep2.Benchmarks.SubscriptionCost.RunAsync(args);

// Pick benchmarks from the command line, for example: dotnet run -c Release -- --filter '*Pipeline*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
