namespace Meep2.Meeplang.Compilation;

/// <summary>What names mean while compiling expressions.</summary>
internal sealed class CompileScope
{
    public static readonly CompileScope Empty = new(new Dictionary<string, ConstantValue>(), _ => null, _ => null, []);

    public CompileScope(
        IReadOnlyDictionary<string, ConstantValue> constants,
        Func<string, FamTarget?> fam,
        Func<string, Type?> messageType,
        IReadOnlyCollection<string> moduleNames,
        Func<string, string>? noMessageType = null)
    {
        Constants = constants;
        Fam = fam;
        MessageType = messageType;
        ModuleNames = moduleNames;
        NoMessageType = noMessageType ?? (name => $"There's no message type called {name}.");
    }

    /// <summary>Constants declared with <c>const</c>.</summary>
    public IReadOnlyDictionary<string, ConstantValue> Constants { get; }

    /// <summary>What <c>fam.Name</c> refers to: null if there's no such module.</summary>
    public Func<string, FamTarget?> Fam { get; }

    /// <summary>Message types by name, for <c>is</c>.</summary>
    public Func<string, Type?> MessageType { get; }

    /// <summary>Names that <c>fam</c> can use, for suggestions.</summary>
    public IReadOnlyCollection<string> ModuleNames { get; }

    /// <summary>Why <see cref="MessageType"/> found nothing for a name, in words.</summary>
    public Func<string, string> NoMessageType { get; }
}
