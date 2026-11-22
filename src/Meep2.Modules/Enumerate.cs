using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Emits an item from a book in the library for each message it receives, as a <see cref="BookItem"/>. In Meeplang:
/// <c>Enumerate "Countries"</c> to step through a book in order, one item for each message, or
/// <c>Enumerate "Lorem Ipsum" index=msg.Count</c> to choose the item, or <c>Enumerate "Common Passwords" all=true</c>
/// for every item.
/// </summary>
/// <remarks>
/// <para>
/// The index counts from 0 and wraps around the end of the book, so 12 in a book of 5 items is item 2, and -1 is the
/// last. Without <c>index</c>, Enumerate keeps its own place in the book: it starts at item 0 and moves on one each
/// time, wrapping at the end (Q162). The message that asked for the item is its parent, so <c>fam</c> reaches it. To
/// pace it, put it under an Interval.
/// </para>
/// <para>
/// With <c>all=true</c>, each message brings out the whole book, in order (Q164). Emitting never waits, so a book larger
/// than the next module's buffer would lose its first items. The loader warns about that (Q169); raise <c>buffer</c>
/// for a book that large.
/// </para>
/// <para>
/// The built-in books are in <see cref="Library"/>. Plugins add more with <see cref="LibraryBookAttribute"/>. Errors
/// are passed on untouched.
/// </para>
/// </remarks>
[Verb("Enumerate")]
[Emits(typeof(BookItem))]
public sealed class Enumerate(EnumerateParameters parameters)
    : AInflection<Message>, IChosenByParameters<EnumerateParameters>, IEmitsAtOnce<EnumerateParameters>
{
    private long _next;

    /// <summary>Takes <c>index</c> or <c>all</c>, but not both.</summary>
    public static string? WhyNot(EnumerateParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.All && parameters.Index is not null ? "takes index or all=true, not both" : null;
    }

    /// <summary>With <c>all=true</c>, the whole book.</summary>
    public static long? MostAtOnce(EnumerateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.All ? parameters.Book.Items.Count : null;
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }

        var items = parameters.Book.Items;
        if (parameters.All)
        {
            for (var i = 0; i < items.Count; i++)
                context.Emit(new BookItem(parameters.Book.Name, i, items[i]));
            return ValueTask.CompletedTask;
        }

        long index;
        try
        {
            index = parameters.Index is { } chosen ? chosen.Evaluate(message) : _next++;
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }

        var place = ((index % items.Count) + items.Count) % items.Count;
        context.Emit(new BookItem(parameters.Book.Name, place, items[(int)place]));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Enumerate"/>.</summary>
public sealed record EnumerateParameters
{
    /// <summary>The book to read from, such as <c>"Countries"</c>, or <c>"Plugin.Book"</c> for a plugin's.</summary>
    [Positional]
    public required Book Book { get; init; }

    /// <summary>
    /// Which item to emit, worked out for each message, counting from 0 and wrapping around the end. Without it, the
    /// items in order, one for each message.
    /// </summary>
    public Computed<long>? Index { get; init; }

    /// <summary>Emits every item in the book, in order, for each message, instead of one.</summary>
    public bool All { get; init; }
}
