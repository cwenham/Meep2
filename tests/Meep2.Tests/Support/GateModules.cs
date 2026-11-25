using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Tests.Support;

/// <summary>A verb that always needs ReadNotes, which a pipeline can't remove: passes on what reaches it.</summary>
[Verb("NeedsNotes")]
[RequiresPermission("ReadNotes")]
[EmitsInput]
public sealed class NeedsNotes : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A source that always needs ReadNotes, so its pipeline must hold it for it to start.</summary>
[Verb("NotesSource")]
[RequiresPermission("ReadNotes")]
public sealed class NotesSource : ASource
{
    protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Checks in code that a message may write the file it's given, as a finer gate would (ModuleContext.Demand).</summary>
[Verb("WritesNote")]
[EmitsInput]
public sealed class WritesNote(WritesNoteParameters parameters) : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Demand("WriteNotes", message, parameters.Path);
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="WritesNote"/>.</summary>
public sealed record WritesNoteParameters
{
    /// <summary>The file it would write.</summary>
    [Positional]
    public required string Path { get; init; }
}

/// <summary>Makes a new message from each one, its child, so a gate further down has a family to walk.</summary>
[Verb("Derive")]
[Emits(typeof(TextMessage))]
public sealed class Derive : AInflection<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(new TextMessage(message.ToText()));
        return ValueTask.CompletedTask;
    }
}

/// <summary>A passthrough that does nothing with what it takes, so its messages go on at once.</summary>
[Verb("Glance")]
public sealed class Glance : APassthrough<Message>
{
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
