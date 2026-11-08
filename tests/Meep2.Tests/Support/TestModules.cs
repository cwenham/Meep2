using System.Collections.Concurrent;
using System.Threading.Channels;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Tests.Support;

/// <summary>Emits the given messages, then finishes.</summary>
public sealed class Messages(params Message[] messages) : ASource
{
    protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
            context.Emit(message);
        return Task.CompletedTask;
    }
}

/// <summary>Emits a <see cref="Number"/> for each value, then finishes.</summary>
public sealed class Numbers(params int[] values) : ASource
{
    protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        foreach (var value in values)
            context.Emit(new Number(value));
        return Task.CompletedTask;
    }
}

/// <summary>Emits a new <see cref="Number"/> every few milliseconds until stopped.</summary>
public sealed class Ticker(TimeSpan period) : ASource
{
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        for (var i = 1; ; i++)
        {
            context.Emit(new Number(i));
            await Task.Delay(period, cancellationToken);
        }
    }
}

/// <summary>Emits, with tracking, whatever the test sends, until the test finishes it.</summary>
public sealed class Feed : ASource
{
    private readonly Channel<(Message Message, TaskCompletionSource<Task<MessageOutcome>> Emitted)> _queue =
        Channel.CreateUnbounded<(Message, TaskCompletionSource<Task<MessageOutcome>>)>();

    /// <summary>Queues a message, and returns how it and its descendants fared once they're finished.</summary>
    public Task<MessageOutcome> Send(Message message)
    {
        var emitted = new TaskCompletionSource<Task<MessageOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite((message, emitted));
        return emitted.Task.Unwrap();
    }

    public void Finish() => _queue.Writer.TryComplete();

    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        await foreach (var (message, emitted) in _queue.Reader.ReadAllAsync(cancellationToken))
            emitted.SetResult(context.EmitTracked(message));
    }
}

/// <summary>Runs a delegate for each message. The delegate decides what to emit.</summary>
public sealed class Map<TIn>(Func<TIn, ModuleContext, ValueTask> handle) : AInflection<TIn>
    where TIn : class
{
    public Map(Action<TIn, ModuleContext> handle)
        : this((message, context) =>
        {
            handle(message, context);
            return ValueTask.CompletedTask;
        })
    {
    }

    protected override ValueTask HandleAsync(TIn message, ModuleContext context, CancellationToken cancellationToken) =>
        handle(message, context);
}

/// <summary>Keeps every message it receives, and lets a test wait until it has enough.</summary>
public sealed class Collect<TIn> : AInflection<TIn>
    where TIn : class
{
    private readonly ConcurrentQueue<TIn> _received = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    public IReadOnlyList<TIn> Received => [.. _received];

    /// <summary>Completes once at least <paramref name="count"/> messages have arrived.</summary>
    public Task Reached(int count)
    {
        lock (_waiters)
        {
            if (_received.Count >= count)
                return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task;
        }
    }

    protected override ValueTask HandleAsync(TIn message, ModuleContext context, CancellationToken cancellationToken)
    {
        _received.Enqueue(message);
        lock (_waiters)
        {
            foreach (var (count, reached) in _waiters)
            {
                if (_received.Count >= count)
                    reached.TrySetResult();
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Holds each message until the test opens the gate, then passes it on.</summary>
public sealed class Gate<TIn> : AInflection<TIn>
    where TIn : class
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _handled;

    /// <summary>Completes when the first message arrives.</summary>
    public Task Started => _started.Task;

    public int Handled => Volatile.Read(ref _handled);

    public void Open() => _open.TrySetResult();

    protected override async ValueTask HandleAsync(TIn message, ModuleContext context, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        await _open.Task.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _handled);
        context.Emit((Message)(object)message);
    }
}

/// <summary>A passthrough module that records what it handles, optionally waiting for a gate first.</summary>
public sealed class Recorder<TIn>(bool startOpen = true) : APassthrough<TIn>
    where TIn : class
{
    private readonly ConcurrentQueue<TIn> _handled = new();
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<TIn> Handled => [.. _handled];

    public void Open() => _open.TrySetResult();

    protected override async ValueTask HandleAsync(TIn message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (!startOpen)
            await _open.Task.WaitAsync(cancellationToken);
        _handled.Enqueue(message);
    }
}

/// <summary>Records the most messages this module was ever handling at the same moment.</summary>
public sealed class ConcurrencyProbe : AInflection<Message>
{
    private int _current;
    private int _highest;
    private int _handled;

    public int Highest => Volatile.Read(ref _highest);

    public int Handled => Volatile.Read(ref _handled);

    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _current);
        int highest;
        while (now > (highest = Volatile.Read(ref _highest))
               && Interlocked.CompareExchange(ref _highest, now, highest) != highest)
        {
        }
        await Task.Yield();
        Interlocked.Decrement(ref _current);
        Interlocked.Increment(ref _handled);
    }
}
