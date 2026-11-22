using System.Text;
using System.Text.Json;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>What <see cref="Interval"/> emits each time it fires.</summary>
/// <param name="Count">The tick's number: how many times Interval has fired, counting from 1, or from its <c>count-start</c>.</param>
public sealed record Tick(long Count) : Message
{
    /// <summary>The text given with Interval's <c>payload</c> parameter, if any.</summary>
    public string? Payload { get; init; }
}

/// <summary>Plain text, such as a message sent into a running pipeline as a test (Q72).</summary>
/// <param name="Text">The text.</param>
public sealed record TextMessage(string Text) : Message, IHasText;

/// <summary>The contents of a text file, as <see cref="Read"/> emits it.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Text">What the file contains.</param>
public sealed record FileText(string Path, string Text) : Message, IHasText, IHasPath;

/// <summary>
/// What Classify emits: the class a message most likely belongs to, and how likely. Its parent is the message that
/// was classified, so <c>fam</c> reaches it. When the tree hasn't learned enough to classify it, <see cref="Class"/>
/// is null and <see cref="Score"/> is 0 (Q144).
/// </summary>
/// <param name="Tree">The classification tree that was used, as named in the pipeline.</param>
/// <param name="Class">The class with the highest score, such as "Science", or null if the message is unclassified.</param>
/// <param name="Score">
/// The probability, from 0 to 1, that the text belongs to <paramref name="Class"/> rather than another class the tree
/// knows, or the classes given with <c>against</c>. Naive Bayes is overconfident, so scores near 1 are common on long
/// texts; compare them with care.
/// </param>
/// <param name="Text">The text that was classified.</param>
public sealed record ClassifiedMessage(string Tree, string? Class, double Score, string Text) : Message, IHasText
{
    /// <summary>Whether a class was found.</summary>
    public bool IsClassified => Class is not null;

    /// <summary>
    /// Every class that was scored, with its probability. Without <c>against</c> they add up to 1; with it, each is
    /// that class's probability against the <c>against</c> classes alone.
    /// </summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();

    /// <summary>Prints the tree, the class and the score, leaving out the text.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Tree = ").Append(Tree)
            .Append(", Class = ").Append(Class)
            .Append(", Score = ").Append(Score.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>
/// A piece of JSON, as <see cref="Extract"/> emits it. In Meeplang its members are read by name for each message, as
/// in <c>msg.minutesAway</c>.
/// </summary>
/// <param name="Value">The JSON value.</param>
public sealed record JsonMessage(JsonElement Value) : Message, IHasText, IHasDynamicMembers
{
    /// <summary>A string's own text, or the JSON for anything else.</summary>
    public string Text => Value.ValueKind == JsonValueKind.String ? Value.GetString()! : Value.GetRawText();

    /// <summary>Reads a member of a JSON object by name. Names are case-sensitive, as in JSON.</summary>
    public bool TryGetMember(string name, out object? value)
    {
        if (Value.ValueKind == JsonValueKind.Object && Value.TryGetProperty(name, out var member))
        {
            value = member;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Prints the JSON.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Value = ").Append(Value.GetRawText());
        return true;
    }
}

/// <summary>An item from a book in the library, as <see cref="Enumerate"/> emits it.</summary>
/// <param name="Book">The book's name, as the library spells it.</param>
/// <param name="Index">The item's place in the book, counting from 0.</param>
/// <param name="Text">The item.</param>
public sealed record BookItem(string Book, long Index, string Text) : Message, IHasText;
