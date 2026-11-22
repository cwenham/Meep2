using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Writes a line for each message to standard output. A passthrough module: messages carry on downstream straight
/// away. In Meeplang: <c>WriteLine "{msg.route} in {msg.minutesAway} min"</c>, or just <c>WriteLine</c> to write each
/// message as text.
/// </summary>
[Verb("WriteLine")]
[Role(VerbRole.Outlet)]
public sealed class WriteLine : APassthrough<Message>
{
    private readonly WriteLineParameters _parameters;
    private readonly TextWriter _output;

    /// <summary>Writes to standard output.</summary>
    public WriteLine(WriteLineParameters parameters)
        : this(parameters, Console.Out)
    {
    }

    /// <summary>Writes to the given writer.</summary>
    public WriteLine(WriteLineParameters parameters, TextWriter output)
    {
        _parameters = parameters;
        _output = output;
    }

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = _parameters.Text?.Evaluate(message) ?? message.ToText();
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        await _output.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Parameters for <see cref="WriteLine"/>.</summary>
public sealed record WriteLineParameters
{
    /// <summary>What to write, worked out for each message. Without it, each message is written as text.</summary>
    [Positional]
    public Computed<string>? Text { get; init; }
}
