namespace Meep2.SDK.Messages;

/// <summary>
/// A message's family: its ancestors, found by the name of the module that emitted them. The message itself isn't
/// included. In Meeplang this is <c>fam.Name</c>; in C# it's <c>message.Fam["Name"]</c>.
/// </summary>
/// <remarks>
/// <para>A name matches a module's own name (<c>as Name</c>) if it has one, and otherwise its verb.</para>
/// <para>
/// The search goes one generation at a time, starting with the parents, so the nearest match wins. Between
/// matches the same number of generations away, the one reached through an earlier parent wins: a join's first
/// input beats its second.
/// </para>
/// </remarks>
public readonly struct Family
{
    private readonly Message _message;

    internal Family(Message message) => _message = message;

    /// <summary>The nearest ancestor emitted by the module called <paramref name="name"/>, or null if there's none.</summary>
    public Message? this[string name] => Nearest(name);

    /// <summary>The nearest ancestor emitted by the module called <paramref name="name"/>, or null if there's none.</summary>
    public Message? Nearest(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Most messages have one parent, so walk the chain without allocating until it branches.
        var parents = _message.Parents;
        while (true)
        {
            if (parents.Count == 0)
                return null;
            if (parents.Count > 1)
                return Search(parents, name, all: null);
            var parent = parents[0];
            if (Matches(parent, name))
                return parent;
            parents = parent.Parents;
        }
    }

    /// <summary>Every ancestor emitted by the module called <paramref name="name"/>, nearest first, each once.</summary>
    public IReadOnlyList<Message> All(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var matches = new List<Message>();
        Search(_message.Parents, name, matches);
        return matches;
    }

    // Breadth-first search from a generation of messages. Returns the first match, or collects every match in `all`.
    private static Message? Search(Parents start, string name, List<Message>? all)
    {
        var queue = new Queue<Message>();
        var seen = new HashSet<Message>(ReferenceEqualityComparer.Instance);
        foreach (var message in start)
            queue.Enqueue(message);

        while (queue.TryDequeue(out var message))
        {
            if (!seen.Add(message))
                continue;
            if (Matches(message, name))
            {
                if (all is null)
                    return message;
                all.Add(message);
            }
            foreach (var parent in message.Parents)
                queue.Enqueue(parent);
        }
        return null;
    }

    private static bool Matches(Message message, string name) => message.EmittedBy?.IsCalled(name) == true;
}
