using BenchmarkDotNet.Running;

// What a subscription costs against the stats streams, measured over a running daemon rather than by BenchmarkDotNet:
// dotnet run -c Release -- subscriptions [--pipelines 50] [--modules 20] [--seconds 10] [--tick-ms 10] [--ticks-only]
if (args is ["subscriptions", ..])
    return await Meep2.Benchmarks.SubscriptionCost.RunAsync(args);

// What origins, traces and the counts by place cost a busy Fetch to Listen pair:
// dotnet run -c Release -- flows [--seconds 10] [--subscribe] [--places]
if (args is ["flows-micro", ..])
    return Meep2.Benchmarks.FlowMicro.Run();
if (args is ["flows", ..])
    return await Meep2.Benchmarks.FlowCost.RunAsync(args);

// What an @permission gate costs (permissions phase 4): dotnet run -c Release -- gates [--messages 200000]
if (args is ["gates", ..])
    return await Meep2.Benchmarks.GateCost.RunAsync(args);

// What a pipeline's own credential and Meep's own permissions cost (permissions phase 6):
// dotnet run -c Release -- meep-permissions [--messages 200000]
if (args is ["meep-permissions", ..])
    return await Meep2.Benchmarks.MeepPermissionCost.RunAsync(args);

// The memory the fam chain keeps alive (M8, for Q7): dotnet run -c Release -- fam [--messages 100000]
if (args is ["fam", ..])
    return await Meep2.Benchmarks.FamCost.RunAsync(args);

// API requests a second with a token (P3): dotnet run -c Release -- requests [--clients 8] [--seconds 10]
if (args is ["requests", ..])
    return await Meep2.Benchmarks.RequestRate.RunAsync(args);

// What a flush costs while its module is busy (P2): dotnet run -c Release -- flush-wait [--modules 50] [--seconds 10]
if (args is ["flush-wait", ..])
    return await Meep2.Benchmarks.FlushWait.RunAsync(args);

// Pick benchmarks from the command line, for example: dotnet run -c Release -- --filter '*Pipeline*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
