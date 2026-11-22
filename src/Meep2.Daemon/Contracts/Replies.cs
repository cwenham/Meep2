namespace Meep2.Daemon;

/// <summary>Why a request failed.</summary>
/// <param name="Error">What went wrong.</param>
/// <param name="Details">More, such as a pipeline file's errors, line by line.</param>
public sealed record Problem(string Error, IReadOnlyList<string>? Details = null);

/// <summary>A message the daemon sent into a pipeline.</summary>
public sealed record Injected(Guid Id, string Type, string Module);
