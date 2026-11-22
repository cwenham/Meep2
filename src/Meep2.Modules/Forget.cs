using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Makes a named classification tree forget everything it has learned, or one class, then passes the message on.
/// In Meeplang: <c>Forget "topics"</c>, or <c>Forget "topics" class="Science"</c>.
/// </summary>
/// <remarks>
/// It finds the tree as Learn does, and saves it straight away; a tree that has forgotten everything has its file
/// deleted. Errors are passed on untouched.
/// </remarks>
[Verb("Forget")]
[EmitsInput]
public sealed class Forget(ForgetParameters parameters) : AInflection<Message>, IAsyncDisposable
{
    private BayesTree.TreeHold? _tree;

    /// <summary>Lets go of the tree, so it leaves memory once nothing else holds it (M2).</summary>
    public ValueTask DisposeAsync() => _tree?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return;
        }

        string? @class;
        try
        {
            @class = parameters.Class?.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, saves: true, context) is not { } tree)
            return;

        if (tree.Forget(string.IsNullOrWhiteSpace(@class) ? null : @class.Trim()))
        {
            try
            {
                await tree.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.EmitError($"Forget forgot, but couldn't save tree \"{parameters.Tree}\" to {tree.Path}: {exception.Message}", exception);
                return;
            }
        }
        context.Emit(message);
    }
}

/// <summary>Parameters for <see cref="Forget"/>.</summary>
public sealed record ForgetParameters
{
    /// <summary>The name of the classification tree, the same as Learn's.</summary>
    [Positional]
    public required string Tree
    {
        get;
        init => field = Trees.CheckName(value, "Forget");
    }

    /// <summary>The one class to forget, worked out for each message. Without it, everything.</summary>
    public Computed<string>? Class { get; init; }

    /// <summary>The file the tree is saved in, if Learn was given one.</summary>
    public string? File { get; init; }
}
