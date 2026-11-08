using System.Threading.Channels;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// A module that handles messages: a buffer that drops its oldest message when full, and a loop that hands the
/// module one message at a time.
/// </summary>
internal sealed class InflectionNode : Node
{
    private readonly Channel<Message> _buffer;
    private readonly InflectionContext _context;
    private readonly TypeCheck? _handlesType;
    private volatile AInflection? _module;
    private bool _broken;

    public InflectionNode(Pipeline pipeline, NodeSpec spec, int bufferSize)
        : base(pipeline, spec)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, $"{spec.Info} needs room for at least one message.");

        _buffer = Channel.CreateBounded<Message>(
            new BoundedChannelOptions(bufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            OnDropped);
        _context = new InflectionContext(this);
        if (spec.Options.HandlesType is { } handlesType)
            _handlesType = new TypeCheck(handlesType);
    }

    /// <summary>Whether messages pass straight on downstream while the module handles them separately.</summary>
    public bool IsPassthrough { get; private set; }

    /// <summary>Whether the module can handle this message's type.</summary>
    public bool Accepts(Message message) => _module!.Accepts(message);

    /// <summary>
    /// Whether the pipeline wants this module to receive messages of this type (<see cref="NodeOptions.HandlesType"/>).
    /// If not, they skip it.
    /// </summary>
    public bool Handles(Message message) => _handlesType is null || _handlesType.Check(message.GetType());

    public override void CreateModule()
    {
        var module = Create();
        _module = module;
        IsPassthrough = module.IsPassthrough;
    }

    public Task Start() => Task.Run(RunLoopAsync);

    /// <summary>Stops taking messages. The loop ends once the buffer is empty.</summary>
    public void Complete() => _buffer.Writer.TryComplete();

    /// <summary>Puts a message in the buffer, dropping the oldest if it's full.</summary>
    public void Enqueue(Message message)
    {
        CountReceived();
        Pipeline.AddRef(message);
        if (!_buffer.Writer.TryWrite(message))
            Pipeline.Release(message, MessageOutcome.Abandoned); // the pipeline is stopping
    }

    public override ValueTask DisposeModuleAsync() =>
        DisposeInstanceAsync(_module, $"{Info} couldn't be disposed");

    private void OnDropped(Message message)
    {
        CountDropped();
        Pipeline.Release(message, MessageOutcome.Dropped);
    }

    private AInflection Create()
    {
        var module = (AInflection)CreateInstance();
        var input = module.InputType;
        if (!input.IsInterface && !typeof(Message).IsAssignableFrom(input))
        {
            throw new InvalidOperationException(
                $"{Info} handles {input.Name}, which is neither a message type nor an interface, so it would never receive anything.");
        }
        return module;
    }

    private async Task RunLoopAsync()
    {
        var reader = _buffer.Reader;
        var cancellationToken = Pipeline.AbortToken;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var message))
            {
                if (Pipeline.IsAborting)
                {
                    Pipeline.Release(message, MessageOutcome.Abandoned);
                    continue;
                }
                if (_broken)
                {
                    CountDropped();
                    Pipeline.Release(message, MessageOutcome.Dropped);
                    continue;
                }

                // Handled inline rather than in a helper method, so awaiting doesn't allocate for each message.
                var outcome = MessageOutcome.Completed;
                _context.Begin(message);
                try
                {
                    await _module!.Handle(message, _context, cancellationToken).ConfigureAwait(false);
                    CountHandled();
                    if (message is ErrorMessage error && TookCareOf(error))
                        error.MarkHandled();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome = MessageOutcome.Abandoned;
                }
                catch (Exception exception)
                {
                    CountFailed();
                    _context.EmitFailure(message, exception);
                    await RestartAsync().ConfigureAwait(false);
                }
                finally
                {
                    _context.End();
                    Pipeline.Release(message, outcome);
                }
            }
        }
    }

    // A module handles an error it takes in unless it passes that same error on to modules after it. A passthrough
    // module always passes its messages on, and an error only goes on if there's a module after this one, so the
    // last module in a branch handles any error it takes.
    private bool TookCareOf(ErrorMessage error) =>
        !((IsPassthrough || _context.PassedOn) && Downstream.Length > 0);

    // After a module throws, replace it with a fresh one so the next message doesn't meet damaged state.
    private async ValueTask RestartAsync()
    {
        await DisposeInstanceAsync(_module, $"{Info} couldn't be disposed after it failed").ConfigureAwait(false);
        try
        {
            _module = Create();
            CountRestart();
        }
        catch (Exception exception)
        {
            _broken = true;
            Pipeline.ReportFault(Info, $"{Info} couldn't be restarted, so it will drop every message it receives", exception);
        }
    }
}
