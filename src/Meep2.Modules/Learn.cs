using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Teaches a named classification tree that each message belongs to a class, then passes the message on. Classify
/// reads the same tree by its name. In Meeplang: <c>Learn "topics" class="Science"</c>, or
/// <c>Learn "shared:topics" class=msg.section text=msg.body</c>.
/// </summary>
/// <remarks>
/// <para>
/// It learns from each message's text, or from <c>text</c> when it's given. A message whose text has no words is
/// passed on without being learned. Errors are passed on untouched.
/// </para>
/// <para>
/// A tree belongs to its pipeline unless its name starts with <c>shared:</c>, and is saved to
/// <c>~/.meep/trees/&lt;pipeline&gt;_&lt;tree&gt;.json</c> (or <c>shared_&lt;tree&gt;.json</c>) after every
/// <c>save-every</c> documents and when the pipeline finishes. <c>file</c> saves it somewhere else. The tree is a
/// naive Bayes classifier (<see cref="BayesTree"/>).
/// </para>
/// </remarks>
[Verb("Learn")]
[EmitsInput]
public sealed class Learn(LearnParameters parameters) : AInflection<Message>, IAsyncDisposable
{
    private BayesTree.TreeHold? _tree;

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        // Errors aren't text to learn from, so they carry on to whatever handles them.
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return;
        }

        string @class, text;
        try
        {
            @class = parameters.Class.Evaluate(message);
            text = parameters.Text?.Evaluate(message) ?? message.ToText();
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(@class))
        {
            context.EmitError($"Learn needs a class to learn into tree \"{parameters.Tree}\", but class was empty.");
            return;
        }

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, saves: parameters.SaveEvery > 0, context) is not { } tree)
            return;
        tree.Learn(@class.Trim(), text ?? "");

        if (parameters.SaveEvery > 0 && tree.Unsaved >= parameters.SaveEvery)
        {
            try
            {
                await tree.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.EmitError($"Learn learned this, but couldn't save tree \"{parameters.Tree}\" to {tree.Path}: {exception.Message}", exception);
                return;
            }
        }
        context.Emit(message);
    }

    /// <summary>
    /// Saves what the tree has learned since it was last saved, unless <c>save-every</c> is 0, and lets go of it, so
    /// it leaves memory once nothing else holds it (M2).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_tree is not { } hold)
            return;
        if (hold.Tree.Unsaved > 0 && parameters.SaveEvery > 0)
            await hold.Tree.SaveAsync().ConfigureAwait(false);
        await hold.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Parameters for <see cref="Learn"/>.</summary>
public sealed record LearnParameters
{
    /// <summary>
    /// The name of the classification tree to teach, the same as Classify's. Starting it with <c>shared:</c> shares it
    /// with every pipeline.
    /// </summary>
    [Positional]
    public required string Tree
    {
        get;
        init => field = Trees.CheckName(value, "Learn");
    }

    /// <summary>The class the message belongs to, such as "Science", worked out for each message.</summary>
    public required Computed<string> Class { get; init; }

    /// <summary>The text to learn from, worked out for each message. Without it, the message's own text.</summary>
    public Computed<string>? Text { get; init; }

    /// <summary>The file to save the tree in, instead of the one in <c>~/.meep/trees</c>.</summary>
    public string? File { get; init; }

    /// <summary>How many documents to learn between saves. 5 unless set; 0 keeps the tree in memory only.</summary>
    public int SaveEvery
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(SaveEvery), "save-every can't be negative. Use 0 to never save.");
    } = 5;
}
