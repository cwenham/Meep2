namespace Meep2.Meeplang.Syntax;

/// <summary>
/// What loading made, or null if it couldn't, with what it found to say either way: errors when there's no value,
/// and warnings, which can come with one.
/// </summary>
/// <typeparam name="T">What was made, such as a pipeline.</typeparam>
/// <param name="Value">What was made, or null if there are errors.</param>
/// <param name="Diagnostics">The errors and warnings, in the order of the text.</param>
public sealed record WithDiagnostics<T>(T? Value, IReadOnlyList<Diagnostic> Diagnostics)
    where T : class
{
    /// <summary>Whether something was made.</summary>
    public bool HasValue => Value is not null;

    /// <summary>The errors alone.</summary>
    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.IsError);

    /// <summary>What was made, or the exception <paramref name="failure"/> makes from the errors, joined with "; ".</summary>
    public T ValueOrThrow(Func<string, Exception> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return Value ?? throw failure(string.Join("; ", Errors));
    }
}
