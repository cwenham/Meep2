using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Writes a line for each message to standard output. A passthrough module: messages carry on downstream straight
/// away. In Meeplang: <c>WriteLine "{msg.route} in {msg.minutesAway} min"</c>, or just <c>WriteLine</c> to write each
/// message as text.
/// </summary>
[Verb("WriteLine")]
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

/// <summary>
/// Saves each message's text to a file, adding a line to the end by default. A passthrough module: messages carry on
/// downstream straight away. In Meeplang: <c>Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl"</c>.
/// </summary>
/// <remarks>Folders are created as needed. A leading <c>~</c> means your home folder.</remarks>
[Verb("Save")]
public sealed class Save(SaveParameters parameters) : APassthrough<Message>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = Paths.Expand(parameters.Path.Evaluate(message));
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            var text = message.ToText();
            if (parameters.Mode == SaveMode.Append)
                await File.AppendAllTextAsync(path, text + "\n", cancellationToken).ConfigureAwait(false);
            else
                await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.EmitError($"Couldn't save to {path}: {exception.Message}", exception);
        }
    }
}

/// <summary>Parameters for <see cref="Save"/>.</summary>
public sealed record SaveParameters
{
    /// <summary>The file to save to, worked out for each message.</summary>
    [Positional]
    public required Computed<string> Path { get; init; }

    /// <summary>Whether to add a line to the end of the file or replace it. <see cref="SaveMode.Append"/> unless set.</summary>
    public SaveMode Mode { get; init; } = SaveMode.Append;
}

/// <summary>How <see cref="Save"/> writes to its file.</summary>
public enum SaveMode
{
    /// <summary>Add each message to the end of the file, followed by a new line.</summary>
    Append,

    /// <summary>Replace the file with each message.</summary>
    Overwrite,
}

/// <summary>Marks the assembly of core modules, for registering them.</summary>
public static class CoreModules
{
    /// <summary>The assembly that holds the core modules.</summary>
    public static System.Reflection.Assembly Assembly => typeof(CoreModules).Assembly;
}
