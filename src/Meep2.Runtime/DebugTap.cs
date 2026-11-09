using System.Threading.Channels;
using Meep2.Runtime.Internal;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>
/// A debug tap on a running pipeline: each message a module emits or passes on, for watching what flows past that
/// point (Q64). Open one with <see cref="Pipeline.Tap"/> and dispose it when you're done.
/// </summary>
/// <remarks>
/// A tap must never slow the pipeline or change what it does. It has its own small buffer that drops its oldest
/// message when the reader falls behind, holds no leases, and doesn't count towards halting, acknowledgements or
/// error handling. It ends when its pipeline finishes.
/// </remarks>
public sealed class DebugTap : IDisposable
{
    private readonly Node _node;
    private readonly Channel<Message> _messages;
    private readonly Func<Message, bool>? _where;
    private readonly double _sample;
    private long _dropped;

    internal DebugTap(Node node, DebugTapOptions options)
    {
        _node = node;
        _where = options.Where;
        _sample = options.Sample;
        _messages = Channel.CreateBounded<Message>(
            new BoundedChannelOptions(options.BufferSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>The module tapped.</summary>
    public ModuleInfo Module => _node.Info;

    /// <summary>The messages, as they pass. The reader completes when the tap is disposed or its pipeline finishes.</summary>
    public ChannelReader<Message> Messages => _messages.Reader;

    /// <summary>Messages that passed while the reader was behind and the tap's buffer was full, so they were dropped.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Stops tapping.</summary>
    public void Dispose()
    {
        _node.RemoveTap(this);
        Complete();
    }

    internal void Offer(Message message)
    {
        if (_sample < 1 && Random.Shared.NextDouble() >= _sample)
            return;
        if (_where is not null)
        {
            try
            {
                if (!_where(message))
                    return;
            }
            catch (Exception)
            {
                // A filter that fails on a message doesn't match it. The pipeline mustn't notice.
                return;
            }
        }
        _messages.Writer.TryWrite(message);
    }

    internal void Complete() => _messages.Writer.TryComplete();
}

/// <summary>What a <see cref="Tap"/> shows.</summary>
public sealed record DebugTapOptions
{
    /// <summary>Shows only the messages this matches, or every message if it's null.</summary>
    public Func<Message, bool>? Where { get; init; }

    /// <summary>The share of messages to show, from just above 0 to 1 (all of them), picked at random.</summary>
    public double Sample
    {
        get;
        init => field = value is > 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(Sample), value, "The sample has to be more than 0 and at most 1.");
    } = 1;

    /// <summary>How many messages wait for the reader before the oldest is dropped.</summary>
    public int BufferSize
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(BufferSize), value, "A tap needs room for at least one message.");
    } = 100;
}
