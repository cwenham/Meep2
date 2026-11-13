using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.TestPlugin;

/// <summary>Text, shouted: the plugin's own message type.</summary>
/// <param name="Text">The shouted text.</param>
public sealed record Shouted(string Text) : Message, IHasText;

/// <summary>Shouts its value: "tick 1" becomes "TICK 1!". The words come from a library of the plugin's own.</summary>
[Verb("Shout")]
[Emits(typeof(Shouted))]
public sealed class Shout(ShoutParameters parameters) : AInflection<Message>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(new Shouted(Words.Words.Exclaim(parameters.Text.Evaluate(message))));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Shout"/>.</summary>
public sealed record ShoutParameters
{
    /// <summary>What to shout.</summary>
    [Positional]
    public required Computed<string> Text { get; init; }
}

/// <summary>The plugin's own book, for Enumerate: a field rather than a property, to test both.</summary>
public static class Shouts
{
    /// <summary>Things to shout.</summary>
    [LibraryBook("Shouts")]
    public static readonly IReadOnlyList<string> Book = ["hey", "oi", "yo"];
}
