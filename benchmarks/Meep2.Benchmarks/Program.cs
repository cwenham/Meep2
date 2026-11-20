using BenchmarkDotNet.Running;

// What a subscription costs against the stats streams, measured over a running daemon rather than by BenchmarkDotNet:
// dotnet run -c Release -- subscriptions [--pipelines 50] [--modules 20] [--seconds 10] [--tick-ms 10]
if (args is ["subscriptions", ..])
    return await Meep2.Benchmarks.SubscriptionCost.RunAsync(args);

// What origins, traces and the counts by place cost a busy Fetch to Listen pair:
// dotnet run -c Release -- flows [--seconds 10] [--subscribe] [--places]
if (args is ["flows-micro", ..])
    return Meep2.Benchmarks.FlowMicro.Run();
if (args is ["flows", ..])
    return await Meep2.Benchmarks.FlowCost.RunAsync(args);

// Pick benchmarks from the command line, for example: dotnet run -c Release -- --filter '*Pipeline*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
