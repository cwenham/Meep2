using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// What a change to a running pipeline does to its modules (<see cref="Pipeline.ApplyAsync"/>): which of the running
/// modules each of the new pipeline's is, kept in place or replaced, and which are new.
/// </summary>
internal sealed class ChangePlan
{
    private ChangePlan(int count) => Nodes = new Node[count];

    /// <summary>The new pipeline's modules, in the order of its specs: kept ones, and ones made for it.</summary>
    public Node[] Nodes { get; }

    /// <summary>Modules made for the change, whose instances aren't made yet.</summary>
    public List<Node> Created { get; } = [];

    /// <summary>Running modules kept in place, each with the spec it takes.</summary>
    public List<KeptNode> Updates { get; } = [];

    /// <summary>Running modules the change keeps.</summary>
    public HashSet<Node> Kept { get; } = [];

    /// <summary>Modules new to the pipeline.</summary>
    public List<ModuleInfo> Added { get; } = [];

    /// <summary>Modules made afresh in place of running ones, as the change reports them.</summary>
    public List<ModuleInfo> Replaced { get; } = [];

    /// <summary>The running modules those replace.</summary>
    public HashSet<Node> ReplacedOld { get; } = [];

    /// <summary>
    /// Works out the plan for a change, from the running modules by their identity and the pipeline's nodes now.
    /// <paramref name="sameShape"/> says whether a running module can carry on in place as a new spec, and
    /// <paramref name="newNode"/> makes a module for a spec.
    /// </summary>
    /// <exception cref="ArgumentException">The change keeps a module as a source and not, or as more than one module.</exception>
    public static ChangePlan Of(
        PipelineChange change,
        IReadOnlyDictionary<ModuleInfo, Node> running,
        Node[] nodes,
        Func<Node, NodeSpec, PipelineOptions, bool> sameShape,
        Func<NodeSpec, PipelineOptions, Node> newNode)
    {
        var (specs, options) = (change.Next.Specs, change.Next.Options);
        var plan = new ChangePlan(specs.Length);
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            Node? node = null;
            if (change.KeptAs(spec.Info) is var (was, reconfigure) && running.TryGetValue(was, out var old) && nodes.Contains(old))
                node = plan.Keep(old, was, spec, reconfigure, sameShape(old, spec, options));
            if (node is null)
            {
                node = newNode(spec, options);
                plan.Created.Add(node);
                if (!plan.Replaced.Contains(spec.Info))
                    plan.Added.Add(spec.Info);
            }
            plan.Nodes[i] = node;
        }
        return plan;
    }

    // Keeps a running module as a new spec, in place if it can carry on, or marks it to be replaced. Returns it if kept
    // in place.
    private Node? Keep(Node old, ModuleInfo was, NodeSpec spec, bool reconfigure, bool sameShape)
    {
        if (old.Spec.IsSource != spec.IsSource)
            throw new ArgumentException($"{was} can't become {spec.Info}: only one of them is a source.", "change");
        if (!Kept.Add(old))
            throw new ArgumentException($"{was} is kept as more than one module.", "change");
        if (sameShape)
        {
            Updates.Add(new KeptNode(old, was, spec, reconfigure));
            return old;
        }
        Kept.Remove(old); // it goes, and a new one takes its place
        Replaced.Add(spec.Info);
        ReplacedOld.Add(old);
        return null;
    }

    /// <summary>A running module kept in place: the identity it had, the spec it takes, and whether its parameters changed.</summary>
    public sealed record KeptNode(Node Node, ModuleInfo Old, NodeSpec Spec, bool Reconfigure);
}
