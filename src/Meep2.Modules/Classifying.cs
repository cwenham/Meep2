using System.Text.Json;
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
    private BayesTree? _tree;

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

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, context) is not { } tree)
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

    /// <summary>Saves what the tree has learned since it was last saved, unless <c>save-every</c> is 0.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_tree is { Unsaved: > 0 } tree && parameters.SaveEvery > 0)
            await tree.SaveAsync().ConfigureAwait(false);
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
public sealed class Classify(ClassifyParameters parameters) : AInflection<Message>
{
    private readonly string[]? _against = parameters.Against?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private BayesTree? _tree;

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

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, context) is not { } tree)
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
public sealed class Forget(ForgetParameters parameters) : AInflection<Message>
{
    private BayesTree? _tree;

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

        if (Trees.Open(ref _tree, parameters.Tree, parameters.File, context) is not { } tree)
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

/// <summary>What Learn, Classify and Forget share.</summary>
internal static class Trees
{
    public static string CheckName(string name, string verb) =>
        !string.IsNullOrWhiteSpace(name) && name != BayesTree.SharedPrefix
            ? name
            : throw new ArgumentException($"{verb} needs the name of a classification tree.", nameof(name));

    /// <summary>The module's tree, opened the first time it's needed, or null after emitting an error.</summary>
    public static BayesTree? Open(ref BayesTree? tree, string name, string? file, ModuleContext context)
    {
        if (tree is not null)
            return tree;
        var path = file is not null ? Paths.Expand(file) : BayesTree.PathFor(context.PipelineName, name);
        try
        {
            return tree = BayesTree.Open(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.EmitError($"Couldn't load tree \"{name}\" from {path}: {exception.Message}", exception);
            return null;
        }
    }
}
