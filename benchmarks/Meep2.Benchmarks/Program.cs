using BenchmarkDotNet.Running;

// Pick benchmarks from the command line, e.g. --filter '*Buffer*'. They arrive with step 2.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
