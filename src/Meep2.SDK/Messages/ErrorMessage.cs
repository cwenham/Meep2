using System.Text;

namespace Meep2.SDK.Messages;

/// <summary>
/// Reports that something went wrong. The runtime emits one when a module throws, with the message it was handling
/// as the parent; modules can also emit their own.
/// </summary>
/// <remarks>
/// <para>
/// Errors travel downstream like other messages, but skip any module that doesn't take them, so they reach the
/// modules that do. Any module that accepts <see cref="Message"/> takes errors, unless the pipeline says otherwise.
/// </para>
/// <para>
/// An error is handled once a module takes it in and doesn't pass that same error on to a module after it. So the
/// last module in a branch handles any error it takes. Errors that reach the end of the pipeline unhandled go to the
/// error policy.
/// </para>
/// </remarks>
public record ErrorMessage : Message
{
    private const int HandledFlag = 1;

    /// <summary>What went wrong, in words.</summary>
    public required string Error { get; init; }

    /// <summary>The exception behind the error, if there was one.</summary>
    public Exception? Exception { get; init; }

    internal bool IsHandled => (Volatile.Read(ref RuntimeFlags) & HandledFlag) != 0;

    internal void MarkHandled() => Interlocked.Or(ref RuntimeFlags, HandledFlag);

    /// <summary>Prints the error's identity and text, leaving out the exception's stack trace.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Error = ").Append(Error);
        return true;
    }
}
