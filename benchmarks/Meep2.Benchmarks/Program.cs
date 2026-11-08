using BenchmarkDotNet.Running;

// Pick benchmarks from the command line, for example: dotnet run -c Release -- --filter '*Pipeline*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
