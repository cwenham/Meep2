namespace Meep2.SDK.Modules;

/// <summary>What a program <see cref="ProgramRunner"/> ran said, and how it ended.</summary>
/// <param name="ExitCode">The code it exited with: 0 for success.</param>
/// <param name="Output">What it wrote to standard output.</param>
/// <param name="Error">What it wrote to standard error.</param>
public sealed record ProgramResult(int ExitCode, string Output, string Error)
{
    /// <summary>Whether it exited with 0.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Everything it said, errors first, trimmed: for a message saying why it failed.</summary>
    public string Said => (Error + Output).Trim();
}
