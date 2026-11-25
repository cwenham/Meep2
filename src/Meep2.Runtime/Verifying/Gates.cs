using System.Collections.Concurrent;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// What a pipeline's gates need: each permission a gate names, worked out against the trust store as it is now and
/// the pipeline's own <c>#permission</c> directives, which can only narrow a definition. Worked out again when the
/// store reloads or the pipeline's options change, and otherwise once.
/// </summary>
/// <param name="options">The pipeline's options as they are now.</param>
internal sealed class Gatekeeper(Func<PipelineOptions> options)
{
    private volatile Resolved? _resolved;
    private int _generation;

    /// <summary>The pipeline's trust store as it is now, or null if it has none.</summary>
    public TrustStore? Store => options().Trust?.Current;

    /// <summary>Goes up each time the pipeline's options change.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Says the pipeline's options changed, so its directives may have.</summary>
    public void OptionsChanged() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// What <paramref name="name"/> needs, as a gate asks it: the same object for every gate and message until the store
    /// or the pipeline's options change, so answers can be kept on messages.
    /// </summary>
    public Requirement Resolve(PermissionName name, TrustStore? store)
    {
        var generation = Volatile.Read(ref _generation);
        var resolved = _resolved;
        if (resolved is null || !ReferenceEquals(resolved.Store, store) || resolved.Generation != generation)
        {
            resolved = new Resolved(store, generation, Directives(options().Permissions));
            _resolved = resolved;
        }
        return resolved.ByName.GetOrAdd(name, static (n, r) => Make(n, r.Store, r.Directives), resolved);
    }

    /// <summary>
    /// What <paramref name="name"/> needs on <paramref name="target"/>, for one check in code
    /// (<see cref="SDK.Modules.ModuleContext.Demand"/>). Its answers aren't kept, since each target is different.
    /// </summary>
    public Requirement Resolve(PermissionName name, string? target, TrustStore? store)
    {
        var basis = Resolve(name, store);
        if (target is null || basis.Problem is not null)
            return basis;
        if (basis.Permission.Name.Scope is { } scope && !scope.Matches(target))
            return new Requirement(name, $"{target} is outside {basis.Permission.Name}, where {name.Name} counts in this pipeline.");
        return new Requirement(basis.Permission, target) { Name = name };
    }

    /// <summary>Why <paramref name="message"/> doesn't meet <paramref name="requirement"/>, or null if it does.</summary>
    public static string? WhyNot(Message message, Requirement requirement, TrustStore? store)
    {
        if (requirement.Problem is { } problem)
            return problem;
        if (requirement.Kept && GateAnswers.Find(message, requirement) is { } known)
            return known.WhyNot;
        return new PermissionLines(store!).WhyNotMet(message, requirement);
    }

    /// <summary>
    /// A definition as the pipeline's directive for it narrows it (Q257), or as it is if the pipeline has none.
    /// </summary>
    public static Permission Narrowed(Permission definition, IReadOnlyList<Permission> directives)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (var directive in directives)
        {
            if (directive.Name.Name == definition.Name.Name)
                return definition.NarrowedBy(directive);
        }
        return definition;
    }

    private static Dictionary<string, Permission> Directives(IReadOnlyList<Permission> directives)
    {
        var byName = new Dictionary<string, Permission>(StringComparer.Ordinal);
        foreach (var directive in directives)
            byName.TryAdd(directive.Name.Name, directive);
        return byName;
    }

    // A gate's requirement: the definition, narrowed by the pipeline's directive and by the gate's own scope, and well
    // defined, or why it can't be held.
    private static Requirement Make(PermissionName name, TrustStore? store, Dictionary<string, Permission> directives)
    {
        if (store is null)
            return new Requirement(name, Trust.NoStore);
        if (store.Permissions.Find(name.Name) is not { } definition)
        {
            // A name the store doesn't define can still be needed (Q289): any trusted authority confers it, and it
            // requires nothing, unless the pipeline's own directive narrows that. meep lsp warns about it.
            var open = directives.TryGetValue(name.Name, out var narrowing) ? narrowing : new Permission(new PermissionName(name.Name));
            var undefined = name.Scope is null ? open : new Permission(name) { Authorities = open.Authorities, Requires = open.Requires };
            return new Requirement(undefined) { Name = name, Kept = true };
        }
        var permission = directives.TryGetValue(name.Name, out var directive) ? definition.NarrowedBy(directive) : definition;
        if (name.Scope is not null)
            permission = new Permission(name) { Authorities = permission.Authorities, Requires = permission.Requires };
        var failures = store.Permissions.Check(permission);
        if (failures.Count > 0)
            return new Requirement(name, $"{name} isn't well defined here: {string.Join(" ", failures.Select(f => f.Reason))}");
        return new Requirement(permission) { Name = name, Kept = true };
    }

    // The requirements worked out for one store and one set of the pipeline's options.
    private sealed class Resolved(TrustStore? store, int generation, Dictionary<string, Permission> directives)
    {
        public TrustStore? Store => store;

        public int Generation => generation;

        public Dictionary<string, Permission> Directives => directives;

        public ConcurrentDictionary<PermissionName, Requirement> ByName { get; } = new();
    }
}

/// <summary>
/// The permissions a module needs before a message may reach it: its <c>@permission</c> modifiers and what its verb
/// requires (<see cref="RequiresPermissionAttribute"/>), checked against the message's family before it's delivered.
/// </summary>
/// <param name="keeper">The pipeline's gatekeeper.</param>
/// <param name="needed">The permissions, in the order written, modifiers first.</param>
internal sealed class Gate(Gatekeeper keeper, PermissionName[] needed)
{
    private volatile Worked? _worked;

    /// <summary>The permissions it needs.</summary>
    public IReadOnlyList<PermissionName> Needed => needed;

    /// <summary>Why <paramref name="message"/> may not reach the module, or null if it may.</summary>
    public Refusal? Check(Message message)
    {
        var store = keeper.Store;
        var requirements = RequirementsFor(store);
        List<PermissionName>? missing = null;
        List<string>? reasons = null;
        PermissionLines? lines = null;
        foreach (var requirement in requirements)
        {
            string? why;
            if (requirement.Problem is { } problem)
                why = problem;
            else if (GateAnswers.Find(message, requirement) is { } known)
                why = known.WhyNot;
            else
                why = (lines ??= new PermissionLines(store!)).WhyNotMet(message, requirement);
            if (why is null)
                continue;
            (missing ??= []).Add(requirement.Name);
            (reasons ??= []).Add(why);
        }
        return missing is null ? null : new Refusal(missing, reasons!);
    }

    // Each permission's requirement for this store, kept until the store or the pipeline's options change.
    private Requirement[] RequirementsFor(TrustStore? store)
    {
        var generation = keeper.Generation;
        var worked = _worked;
        if (worked is not null && ReferenceEquals(worked.Store, store) && worked.Generation == generation)
            return worked.Requirements;
        var requirements = new Requirement[needed.Length];
        for (var i = 0; i < needed.Length; i++)
            requirements[i] = keeper.Resolve(needed[i], store);
        _worked = new Worked(store, generation, requirements);
        return requirements;
    }

    private sealed record Worked(TrustStore? Store, int Generation, Requirement[] Requirements);
}

/// <summary>Why a gate refused a message: each permission it lacked, and why.</summary>
/// <param name="Needed">The permissions it lacked.</param>
/// <param name="Reasons">Why, for each, in the same order.</param>
internal sealed record Refusal(IReadOnlyList<PermissionName> Needed, IReadOnlyList<string> Reasons);
