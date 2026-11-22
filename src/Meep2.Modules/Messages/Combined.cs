using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>What <see cref="Batch"/> emits: messages that went together. Its items are its parents (round 3, section 5).</summary>
public sealed record Batched : Message
{
    /// <summary>The messages in the batch, in the order they came: a view of <see cref="Message.Parents"/>, not a copy.</summary>
    public IReadOnlyList<Message> Items => Parents;

    /// <summary>How many messages are in the batch.</summary>
    public int Count => Parents.Count;
}

/// <summary>What <see cref="Zip"/> emits: a message from each of its two inputs. They're its parents, left first.</summary>
/// <param name="Left">The message from input 1.</param>
/// <param name="Right">The message from input 2.</param>
public sealed record Zipped(Message Left, Message Right) : Message;
