using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Works out which class of a named classification tree each message belongs to, from what Learn has taught the
/// tree, and emits a <see cref="ClassifiedMessage"/> with the class and its score. In Meeplang:
/// <c>Classify "topics"</c>, then perhaps <c>Where msg.Score > 0.9</c>; or <c>Classify "mail" against="Ham"</c>.
/// </summary>
/// <remarks>
/// It classifies each message's text, or <c>text</c> when it's given. It finds the tree as Learn does. Until a class
/// has learned <c>min-training</c> documents, messages come out unclassified: a <see cref="ClassifiedMessage"/> whose
/// <c>Class</c> is null and <c>Score</c> 0 (Q144). Errors are passed on untouched.
/// </remarks>
[Verb("Classify")]
[Emits(typeof(ClassifiedMessage))]
public sealed class Classify(ClassifyParameters parameters) : AInflection<Message>, IAsyncDisposable
{
    private readonly string[]? _against = parameters.Against?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private BayesTree.TreeHold? _tree;

    /// <summary>Lets go of the tree, so it leaves memory once nothing else holds it (M2).</summary>
    public ValueTask DisposeAsync() => _tree?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        // Errors aren't text to classify, so they carry on to whatever handles them.
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }

        string text;
        try
        {
            text = parameters.Text?.Evaluate(message) ?? message.ToText() ?? "";
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, saves: false, context) is not { } tree)
            return ValueTask.CompletedTask;

        var scores = tree.Scores(text, parameters.MinTraining, _against);
        context.Emit(scores.Count == 0
            ? new ClassifiedMessage(parameters.Tree, null, 0, text)
            : new ClassifiedMessage(parameters.Tree, scores[0].Key, scores[0].Value, text)
            {
                Scores = scores.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Classify"/>.</summary>
public sealed record ClassifyParameters
{
    /// <summary>The name of the classification tree to classify with, the same as Learn's.</summary>
    [Positional]
    public required string Tree
    {
        get;
        init => field = Trees.CheckName(value, "Classify");
    }

    /// <summary>The text to classify, worked out for each message. Without it, the message's own text.</summary>
    public Computed<string>? Text { get; init; }

    /// <summary>The file the tree is saved in, if Learn was given one.</summary>
    public string? File { get; init; }

    /// <summary>
    /// How many documents a class must have learned before Classify will choose it, so a class taught only a few
    /// times doesn't win by chance. 10 unless set (Q145).
    /// </summary>
    public int MinTraining
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MinTraining), "min-training must be at least 1.");
    } = 10;

    /// <summary>
    /// Classes to score the others against, separated by commas, such as <c>"Ham"</c> for a spam filter (Q147). Each
    /// other class is then scored against these alone, and these are never the class emitted.
    /// </summary>
    public string? Against { get; init; }
}
