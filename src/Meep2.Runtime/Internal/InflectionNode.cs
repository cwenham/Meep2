using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// A module that handles messages: a buffer that drops its oldest message when full, and a loop that hands one
/// message at a time to the inflection that takes it.
/// </summary>
internal sealed class InflectionNode : Node
{
    // The longest delay CancellationTokenSource.CancelAfter accepts.
    private static readonly TimeSpan s_longestTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // What the loop is doing, so a change can be applied between two messages (goal 4).
    private const int Busy = 0;
    private const int Waiting = 1;
    private const int Changing = 2;

    private readonly Channel<Arrival> _buffer;
    private readonly InflectionContext _context;
    private volatile TypeCheck? _handlesType;
    private readonly Slot[] _slots;
    private readonly ConcurrentDictionary<Type, Slot[]> _routes = new();
    private int _phase = Busy;

    // Work to do between two messages: changes to the module, and flushes of what it keeps.
    private readonly ConcurrentQueue<Job> _jobs = new();
    private int _jobCount;
    private Timer? _flushTimer;
    private readonly Queue<long> _failures = new();

    // Reasons inflections gave for refusing the message being routed. Only the loop uses it, and it's only turned
    // into text if every inflection refuses, so routing doesn't allocate.
    private readonly List<(InflectionSpec Inflection, string Reason)> _refusals = [];
    private volatile Route? _lastRoute;
    private CancellationTokenSource? _expiry;
    private volatile bool _stopped;

    public InflectionNode(Pipeline pipeline, NodeSpec spec, int bufferSize)
        : base(pipeline, spec)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, $"{spec.Info} needs room for at least one message.");

        _buffer = Channel.CreateBounded<Arrival>(
            new BoundedChannelOptions(bufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            OnDropped);
        Capacity = bufferSize;
        _context = new InflectionContext(this);
        _slots = [.. spec.Inflections.Select(inflection => new Slot(inflection))];
        if (spec.Options.HandlesType is { } handlesType)
            _handlesType = new TypeCheck(handlesType);
    }

    /// <summary>The identities of the module's inflections, which the messages they emit record.</summary>
    public IEnumerable<ModuleInfo> InflectionInfos => _slots.Select(slot => slot.Spec.Info);

    /// <summary>Whether messages pass straight on downstream while the module handles them separately.</summary>
    public bool IsPassthrough { get; private set; }

    /// <summary>Whether the error policy has stopped this module and its branch.</summary>
    public bool IsStopped => _stopped;

    public override long Queued => _buffer.Reader.Count;

    /// <summary>How many messages its buffer holds.</summary>
    public int Capacity { get; }

    /// <summary>Whether any of the module's inflections can handle this message's type.</summary>
    public bool Accepts(Message message) => RouteFor(message.GetType()).Length > 0;

    /// <summary>
    /// Whether the pipeline wants this module to receive messages of this type (<see cref="NodeOptions.HandlesType"/>).
    /// If not, they skip it.
    /// </summary>
    public bool Handles(Message message) => _handlesType is null || _handlesType.Check(message.GetType());

    /// <summary>Whether a message has expired, so this module shouldn't take it. Errors don't expire, so they're always reported.</summary>
    public bool HasExpiredFor(Message message) =>
        message.ExpiresTimestamp != 0
        && !Options.AcceptExpired
        && message is not ErrorMessage
        && Stopwatch.GetTimestamp() >= message.ExpiresTimestamp;

    public override void CreateModule()
    {
        foreach (var slot in _slots)
            slot.Module = Create(slot);
        IsPassthrough = _slots[0].Module!.IsPassthrough;
        if (_slots.Any(slot => slot.Module!.IsPassthrough != IsPassthrough))
            throw new InvalidOperationException($"{Info}'s inflections don't agree on whether they're passthrough modules, so it can't route between them.");
        Router = _slots.Length == 1 ? _slots[0].Module as IRouteMessages : null;
    }

    /// <summary>
    /// The module, if it sends each message down one copy of the branch below it, as Distribute does. Such a module
    /// doesn't handle messages itself: they're routed as they're delivered to it.
    /// </summary>
    public IRouteMessages? Router { get; private set; }

    /// <summary>How many messages the module keeps (<see cref="ModuleContext.Keep"/>).</summary>
    public int KeptCount => _context.KeptCount;

    /// <summary>Starts the loop that handles messages.</summary>
    public Task Start() => Loop = Task.Run(RunLoopAsync);

    /// <summary>The loop that handles messages, once started. It ends once the buffer is completed and empty.</summary>
    public Task Loop { get; private set; } = Task.CompletedTask;

    /// <summary>Stops taking messages. The loop ends once the buffer is empty.</summary>
    public void Complete() => _buffer.Writer.TryComplete();

    /// <summary>
    /// Takes the module out of the running pipeline: it stops taking messages, handles those in its buffer, then its
    /// instances are disposed.
    /// </summary>
    public async Task RemoveAsync()
    {
        IsRemoved = true;
        Complete();
        await Loop.ConfigureAwait(false);
        CloseTaps();
        await DisposeModuleAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> UpdateAsync(NodeSpec next, bool reconfigure)
    {
        var keptAll = false;
        await RunBetweenMessagesAsync(async () => keptAll = await ApplyAsync(new Change(next, reconfigure)).ConfigureAwait(false)).ConfigureAwait(false);
        return keptAll;
    }

    /// <summary>
    /// Asks the module to emit what it can of the messages it keeps (<see cref="AInflection.FlushAsync"/>), between
    /// messages, and completes once it has.
    /// </summary>
    /// <param name="finishing">Whether it's because nothing else is left for the pipeline to do.</param>
    public Task FlushAsync(bool finishing) => RunBetweenMessagesAsync(() => FlushModuleAsync(finishing));

    /// <summary>Flushes the module after a while, as it asked (<see cref="ModuleContext.FlushAfter"/>). A later request replaces an earlier one.</summary>
    public void FlushAfter(TimeSpan delay)
    {
        var timer = new Timer(_ =>
        {
            // A pipeline that has halted has nothing left to flush into.
            if (Pipeline.TryHold())
                _ = FlushAsync(finishing: false).ContinueWith(_ => Pipeline.ReleaseHold(), TaskScheduler.Default);
        }, null, Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _flushTimer, timer)?.Dispose();
        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Lets go of every message the module keeps, as the pipeline halts or gives up.</summary>
    public void AbandonKept() => _context.AbandonKept();

    // Runs work between two messages, or at once if the loop is waiting for messages, and completes once it has run.
    private async Task RunBetweenMessagesAsync(Func<ValueTask> work)
    {
        var job = new Job(work);
        _jobs.Enqueue(job);
        Interlocked.Increment(ref _jobCount);
        while (!job.Done.Task.IsCompleted)
        {
            if (Interlocked.CompareExchange(ref _phase, Changing, Waiting) == Waiting)
            {
                try
                {
                    await RunJobsAsync().ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _phase, Waiting);
                }
                break;
            }
            await Task.WhenAny(job.Done.Task, Task.Delay(1)).ConfigureAwait(false);
        }
        await job.Done.Task.ConfigureAwait(false);
    }

    // Runs what's waiting, on whichever thread holds the module: the loop, or a caller while the loop waits.
    private async ValueTask RunJobsAsync()
    {
        while (_jobs.TryDequeue(out var job))
        {
            Interlocked.Decrement(ref _jobCount);
            try
            {
                await job.Work().ConfigureAwait(false);
                job.Done.TrySetResult();
            }
            catch (Exception exception)
            {
                job.Done.TrySetException(exception);
            }
        }
    }

    private async ValueTask FlushModuleAsync(bool finishing)
    {
        _context.BeginFlush(finishing);
        try
        {
            foreach (var slot in _slots)
            {
                if (slot.Module is { } module && !slot.Broken)
                    await module.FlushAsync(_context, Pipeline.AbortToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !Pipeline.AbortToken.IsCancellationRequested)
        {
            CountFailed();
            Pipeline.ReportFault(Info, $"{Info} couldn't flush the messages it kept", exception);
        }
        finally
        {
            _context.EndFlush();
        }
    }

    private async ValueTask<bool> ApplyAsync(Change change)
    {
        var keptAll = true;
        {
            var next = change.Spec;
            foreach (var slot in _slots)
            {
                var spec = next.Inflections.First(i => i.Info.Inflection == slot.Spec.Info.Inflection);
                if (change.Reconfigure && slot.Module is { } module && !slot.Broken && !TryReconfigure(module, slot.Spec, spec))
                {
                    keptAll = false;
                    var old = slot.Spec;
                    slot.Spec = spec;
                    try
                    {
                        slot.Module = Create(slot);
                        await DisposeInstanceAsync(module, $"{Describe(old.Info)} couldn't be disposed after its parameters changed").ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // The old instance carries on, with the old parameters.
                        slot.Spec = old;
                        Pipeline.ReportFault(spec.Info, $"{Describe(spec.Info)} couldn't be made with its new parameters, so it carries on with the old ones", exception);
                        continue;
                    }
                }
                else if (change.Reconfigure && slot.Broken)
                {
                    // New parameters may let a module that couldn't be restarted be made again.
                    var old = slot.Spec;
                    slot.Spec = spec;
                    try
                    {
                        slot.Module = Create(slot);
                        slot.Broken = false;
                    }
                    catch (Exception exception)
                    {
                        slot.Spec = old;
                        Pipeline.ReportFault(spec.Info, $"{Describe(spec.Info)} still couldn't be made", exception);
                        continue;
                    }
                    keptAll = false;
                }
                slot.Spec = spec;
            }
            Spec = next;
            _handlesType = next.Options.HandlesType is { } handlesType ? new TypeCheck(handlesType) : null;
            _routes.Clear();
            _lastRoute = null;
        }
        return keptAll;
    }

    /// <summary>Puts a message in the buffer, dropping the oldest if it's full.</summary>
    /// <param name="input">Which of the module's inputs it came in on, counting from 1; 0 for a message sent in as a test.</param>
    public void Enqueue(Message message, int input)
    {
        CountReceived();
        Pipeline.AddRef(message);
        if (!_buffer.Writer.TryWrite(new Arrival(message, input)))
            Pipeline.Release(message, MessageOutcome.Abandoned); // the pipeline is stopping
    }

    /// <summary>Stops the module and its branch: from now on, messages that reach it are dropped.</summary>
    public void Stop(string reason)
    {
        if (_stopped)
            return;
        _stopped = true;
        Pipeline.Notify($"Stopped {Info} and the branch below it, because {reason}. Messages that reach it are dropped from now on.");
    }

    public override async ValueTask DisposeModuleAsync()
    {
        Interlocked.Exchange(ref _flushTimer, null)?.Dispose();
        foreach (var slot in _slots)
            await DisposeInstanceAsync(slot.Module, $"{Describe(slot.Spec.Info)} couldn't be disposed").ConfigureAwait(false);
        _expiry?.Dispose();
    }

    private void OnDropped(Arrival arrival)
    {
        CountDropped();
        Pipeline.Release(arrival.Message, MessageOutcome.Dropped);
    }

    private AInflection Create(Slot slot)
    {
        var module = (AInflection)CreateInstance(slot.Spec);
        var input = module.InputType;
        if (!input.IsInterface && !typeof(Message).IsAssignableFrom(input))
        {
            throw new InvalidOperationException(
                $"{Describe(slot.Spec.Info)} handles {input.Name}, which is neither a message type nor an interface, so it would never receive anything.");
        }
        slot.InputType = input;
        return module;
    }

    // The inflections that take a type, closest fit first (Q20). The answer for each type is remembered.
    private Slot[] RouteFor(Type type)
    {
        var last = _lastRoute;
        if (last is not null && last.Type == type)
            return last.Slots;
        var slots = _routes.GetOrAdd(type, static (messageType, all) => Order(all, messageType), _slots);
        _lastRoute = new Route(type, slots);
        return slots;
    }

    private static Slot[] Order(Slot[] slots, Type messageType) =>
        [.. slots
            .Select(slot => (Slot: slot, Rank: InflectionOrder.Rank(messageType, slot.InputType)))
            .Where(candidate => candidate.Rank is not null)
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => candidate.Slot)];

    // Picks the inflection for a message: the closest fit by type whose parameters take it.
    private Slot? Choose(Message message, out string refusals)
    {
        refusals = "";
        var route = RouteFor(message.GetType());
        if (route.Length > 0 && route[0].Spec.WhyNot is null)
            return route[0];

        _refusals.Clear();
        foreach (var slot in route)
        {
            if (slot.Spec.WhyNot is not { } whyNot)
                return slot;
            string? reason;
            try
            {
                reason = whyNot(message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reason = exception.Message;
            }
            if (reason is null)
                return slot;
            _refusals.Add((slot.Spec, reason));
        }
        refusals = _refusals.Count == 0
            ? "it has none for this type"
            : string.Join("; ", _refusals.Select(r => $"{r.Inflection.Info.Inflection ?? Info.Verb} {r.Reason}"));
        return null;
    }

    private async Task RunLoopAsync()
    {
        var reader = _buffer.Reader;
        var abort = Pipeline.AbortToken;
        while (true)
        {
            while (reader.TryRead(out var arrival))
            {
                var message = arrival.Message;
                if (Volatile.Read(ref _jobCount) != 0)
                    await RunJobsAsync().ConfigureAwait(false);
                if (Pipeline.IsAborting)
                {
                    Pipeline.Release(message, MessageOutcome.Abandoned);
                    continue;
                }
                if (_stopped)
                {
                    CountDropped();
                    Pipeline.Release(message, MessageOutcome.Dropped);
                    continue;
                }
                if (HasExpiredFor(message))
                {
                    // It expired while it waited in the buffer.
                    CountExpired();
                    Pipeline.Release(message, MessageOutcome.Expired);
                    continue;
                }

                var slot = Choose(message, out var refusals);
                if (slot is null)
                {
                    // Parameters worked out for this message ruled out every inflection that takes its type (Q54).
                    CountRefused();
                    Pipeline.Emit(this, Info, new ErrorMessage { Error = $"None of {Info}'s inflections can take this message: {refusals}." }, defaultParent: message);
                    Pipeline.Release(message, MessageOutcome.Completed);
                    continue;
                }
                if (slot.Broken)
                {
                    CountDropped();
                    Pipeline.Release(message, MessageOutcome.Dropped);
                    continue;
                }

                // Handled inline rather than in a helper method, so awaiting doesn't allocate for each message.
                var cancellationToken = TokenFor(message, abort);
                var outcome = MessageOutcome.Completed;
                Exception? failure = null;
                var span = Telemetry.Tracing ? Telemetry.StartHandling(this, slot.Spec.Info, message) : null;
                var started = Telemetry.Duration.Enabled ? Stopwatch.GetTimestamp() : 0;
                _context.Begin(message, slot.Spec.Info, arrival.Input);
                try
                {
                    await slot.Module!.Handle(message, _context, cancellationToken).ConfigureAwait(false);
                    CountHandled();
                    if (message is ErrorMessage error && TookCareOf(error) && !ReferenceEquals(error.AnsweredBy, slot.Spec.Info) && !ReferenceEquals(error.LeftAloneBy, slot.Spec.Info))
                        error.MarkHandled();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (abort.IsCancellationRequested)
                    {
                        outcome = MessageOutcome.Abandoned;
                    }
                    else
                    {
                        // The module stopped because the message expired, as it's free to (Q12).
                        outcome = MessageOutcome.Expired;
                        CountExpired();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                    CountFailed();
                    _context.EmitFailure(message, exception);
                    await RestartAsync(slot).ConfigureAwait(false);
                    CountTowardsStopping();
                }
                finally
                {
                    _context.End();
                    ResetExpiry(cancellationToken);
                    if (started != 0)
                        Telemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new TagList { { "meep.pipeline", Pipeline.Name }, { "meep.module", Label } });
                    if (span is not null)
                        Telemetry.EndHandling(this, span, outcome, failure);
                    Pipeline.Release(message, outcome);
                }
            }

            // Nothing waiting: apply any change now, then let UpdateAsync apply them itself while the loop waits.
            if (Volatile.Read(ref _jobCount) != 0)
                await RunJobsAsync().ConfigureAwait(false);
            Volatile.Write(ref _phase, Waiting);
            var more = await reader.WaitToReadAsync().ConfigureAwait(false);
            while (Interlocked.CompareExchange(ref _phase, Busy, Waiting) != Waiting)
                await Task.Yield(); // a change is being applied
            if (!more)
                break;
        }
        Volatile.Write(ref _phase, Waiting); // so a change that arrives after the loop has ended is applied at once
    }

    // The token a module gets: cancelled when the pipeline gives up, and when the message expires (Q12). A module
    // with @accept-expired gets messages that may have expired already, so its token ignores expiry.
    private CancellationToken TokenFor(Message message, CancellationToken abort)
    {
        if (message.ExpiresTimestamp == 0 || Options.AcceptExpired)
            return abort;
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), message.ExpiresTimestamp);
        if (remaining >= s_longestTimer)
            return abort;
        _expiry ??= CancellationTokenSource.CreateLinkedTokenSource(abort);
        _expiry.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return _expiry.Token;
    }

    // Readies the expiry token for the next message. One that has fired can't be reused, so it's replaced next time.
    private void ResetExpiry(CancellationToken token)
    {
        if (_expiry is null || token != _expiry.Token)
            return;
        if (!_expiry.TryReset())
        {
            _expiry.Dispose();
            _expiry = null;
        }
    }

    // A module handles an error it takes in unless it passes that same error on to modules after it. A passthrough
    // module always passes its messages on, and an error only goes on if there's a module after this one, so the
    // last module in a branch handles any error it takes.
    private bool TookCareOf(ErrorMessage error) =>
        !((IsPassthrough || _context.PassedOn) && Downstream.Length > 0);

    // After an inflection throws, replace it with a fresh one so the next message doesn't meet damaged state.
    private async ValueTask RestartAsync(Slot slot)
    {
        await DisposeInstanceAsync(slot.Module, $"{Describe(slot.Spec.Info)} couldn't be disposed after it failed").ConfigureAwait(false);
        try
        {
            slot.Module = Create(slot);
            CountRestart();
        }
        catch (Exception exception)
        {
            slot.Broken = true;
            Pipeline.ReportFault(slot.Spec.Info, $"{Describe(slot.Spec.Info)} couldn't be restarted, so it will drop every message it receives", exception);
        }
    }

    // Stops the module and its branch once it has thrown too often lately (stop-branch).
    private void CountTowardsStopping()
    {
        if (Policy.StopBranch is not { } limit)
            return;
        var now = Stopwatch.GetTimestamp();
        var window = (long)Math.Min(limit.Within.TotalSeconds * Stopwatch.Frequency, long.MaxValue / 2);
        _failures.Enqueue(now);
        while (_failures.Count > 0 && now - _failures.Peek() > window)
            _failures.Dequeue();
        if (_failures.Count >= limit.Failures)
            Stop($"it failed {limit.Failures} times within {Describe(limit.Within)}");
    }

    private sealed class Slot(InflectionSpec spec)
    {
        private volatile AInflection? _module;

        public InflectionSpec Spec { get; set; } = spec;

        public AInflection? Module
        {
            get => _module;
            set => _module = value;
        }

        public Type InputType { get; set; } = typeof(Message);

        public bool Broken { get; set; }
    }

    private sealed record Route(Type Type, Slot[] Slots);

    /// <summary>A new spec to apply between messages.</summary>
    private sealed record Change(NodeSpec Spec, bool Reconfigure);

    /// <summary>Work to do between messages, and when it's done.</summary>
    private sealed class Job(Func<ValueTask> work)
    {
        public Func<ValueTask> Work { get; } = work;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A message in the buffer, with the input it came in on.</summary>
    private readonly record struct Arrival(Message Message, int Input);
}
