using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Saves each message's text to a file, adding a line to the end by default. A passthrough module: messages carry on
/// downstream straight away. In Meeplang: <c>Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl"</c>.
/// </summary>
/// <remarks>
/// Folders are created as needed. A leading <c>~</c> means your home folder. What a <c>{msg…}</c> hole gives can't
/// make the path absolute, start it with <c>~</c> or climb out with <c>..</c>, unless <c>any-path=true</c>; and
/// <c>root=</c> keeps every path inside a folder.
/// </remarks>
[Verb("Save")]
[Role(VerbRole.Outlet)]
public sealed class Save(SaveParameters parameters) : APassthrough<Message>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = Paths.Resolve(parameters.Path.Pieces(message), parameters.Root, parameters.AnyPath);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        catch (PathRefusedException exception)
        {
            context.EmitError($"Save refused a path: {exception.Message}", exception);
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

    /// <summary>
    /// A folder every path has to stay inside: relative paths start from it, and a path that leads anywhere else
    /// becomes an error. Paths start from the current folder, and can be anywhere, unless set.
    /// </summary>
    public string? Root { get; init; }

    /// <summary>
    /// Lets a message choose any path, through a <c>{msg…}</c> hole: an absolute one, one starting with <c>~</c>, or
    /// one climbing out with <c>..</c>. Off unless set, so a message can only name a file where the pipeline put it.
    /// </summary>
    public bool AnyPath { get; init; }
}
