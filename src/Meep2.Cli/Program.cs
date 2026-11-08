// The meep command. Commands such as `meep run` arrive in later steps; for now it reports its version.
var version = typeof(Program).Assembly.GetName().Version;
Console.WriteLine($"meep {version} on .NET {Environment.Version}");
return 0;
