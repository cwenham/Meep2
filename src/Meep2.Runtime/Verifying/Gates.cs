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

    /// <summary>
    /// The pipeline's trust store as it is now, or null if it has none. One that hasn't been made yet isn't made by
    /// asking (<see cref="TrustStoreSource.Existing"/>), since every pipeline's gates ask for Meep's own permissions.
    /// </summary>
    public TrustStore? Store => options().Trust?.Existing;

    /// <summary>Goes up each time the pipeline's options change.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Says the pipeline's options changed, so its directives may have.</summary>
    public void OptionsChanged() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// What <paramref name="name"/> needs, as a gate asks it: the same object for every gate and message until the store
    /// or the pipeline's options change, so answers can be kept on messages.
    /// </summary>
    public Requirement Resolve(PermissionName name, TrustStore? store, ProofSet proofs = default)
    {
        var generation = Volatile.Read(ref _generation);
        var unreadable = store is null && options().Trust?.CantBeRead == true;
        var resolved = _resolved;
        if (resolved is null || !ReferenceEquals(resolved.Store, store) || resolved.Generation != generation || resolved.Unreadable != unreadable)
        {
            resolved = new Resolved(store, generation, Directives(options().Permissions), unreadable);
            _resolved = resolved;
        }
        return resolved.ByName.GetOrAdd((name, proofs), static (key, r) => Make(key.Name, key.Proofs, r.Store, r.Directives, r.Unreadable), resolved);
    }

    /// <summary>
    /// What <paramref name="name"/> needs on <paramref name="target"/>, for one check in code
    /// (<see cref="SDK.Modules.ModuleContext.Demand"/>). Its answers aren't kept, since each target is different.
    /// </summary>
    public Requirement Resolve(PermissionName name, PermissionTarget? target, TrustStore? store, ProofSet proofs = default)
    {
        var basis = Resolve(name, store, proofs);
        if (target is null || basis.Problem is not null)
            return basis;
        if (basis.Permission.Name.Scope is { } scope && !Targets.Matches(scope, target))
            return new Requirement(name, $"{target} is outside {basis.Permission.Name}, where {name.Name} counts in this pipeline.");
        return new Requirement(basis.Permission, target) { Name = name, Proofs = proofs };
    }

    /// <summary>
    /// Why <paramref name="message"/> doesn't hold <paramref name="name"/> on <paramref name="target"/>, or null if it
    /// does, as <see cref="SDK.Modules.ModuleContext.Demand"/> asks. It first asks the question a gate would, without
    /// the target, whose answers are kept on messages: a message whose lines hold the permission as the pipeline
    /// defines it holds it on any target within that, so the usual case costs no more than a gate's. Only when that
    /// fails, such as for a credential holding a narrower scope, is the target itself checked.
    /// </summary>
    public string? WhyNot(Message message, PermissionName name, PermissionTarget? target, ProofSet proofs)
    {
        target = target is null ? null : Targets.Lazy(target);
        if (target is LazyFile file && options().MeepFolder is { } meepFolder && MeepsOwn(file, meepFolder) is { } own)
            return own;
        var store = Store;
        var basis = Resolve(name, store, proofs);
        if (basis.Problem is { } problem)
            return problem;
        if (target is not null && basis.Permission.Name.Scope is { } scope && !Targets.Matches(scope, target))
            return $"{target} is outside {basis.Permission.Name}, where {name.Name} counts in this pipeline.";
        var whyNot = WhyNot(message, basis, store);
        if (whyNot is null || target is null)
            return whyNot;
        return WhyNot(message, Resolve(name, target, store, proofs), store);
    }

    /// <summary>
    /// Why a file is out of reach of every <c>meep.files.*</c> permission, or null if it isn't: it's in Meep's own folder
    /// (by its real form), other than its <c>trees</c> folder (Q321). The trust store, plugins, and the daemon's settings
    /// and tokens change only through <c>meep trust</c>, <c>Authorise</c>, <c>Revoke</c> and Meep itself.
    /// </summary>
    private static string? MeepsOwn(LazyFile file, string meepFolder)
    {
        var meep = RealMeepFolder(meepFolder);
        var real = file.Real;
        if (real != meep && !real.StartsWith(meep + "/", StringComparison.Ordinal))
            return null;
        var trees = meep + "/trees";
        if (real.StartsWith(trees + "/", StringComparison.Ordinal))
            return null;
        return $"{file.Path} is in Meep's own folder, {meep}, which no meep.files permission reaches, except its trees folder. Its trust store changes through meep trust, Authorise and Revoke.";
    }

    // Meep's folder's real form, worked out at most once a second, as a scope's is.
    private static (string Folder, string Real, long Until) s_meepFolder;

    private static string RealMeepFolder(string folder)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var known = s_meepFolder;
        if (known.Folder == folder && now < known.Until)
            return known.Real;
        var real = Meep2.Shared.RealPaths.Of(folder).TrimEnd('/');
        s_meepFolder = (folder, real, now + System.Diagnostics.Stopwatch.Frequency);
        return real;
    }

    /// <summary>Why <paramref name="message"/> doesn't meet <paramref name="requirement"/>, or null if it does.</summary>
    public static string? WhyNot(Message message, Requirement requirement, TrustStore? store)
    {
        if (requirement.Problem is { } problem)
            return problem;
        if (requirement.Kept && GateAnswers.Find(message, requirement) is { } known)
            return known.WhyNot;
        return new PermissionLines(store).WhyNotMet(message, requirement);
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
    private static Requirement Make(PermissionName name, ProofSet proofs, TrustStore? store, Dictionary<string, Permission> directives, bool unreadable)
    {
        // Meep's own permissions are defined without a trust store (Q279), so pipelines on a machine that has none
        // work. A store that's there but can't be read might narrow them, so then nothing is held (Q312).
        if (unreadable)
            return new Requirement(name, Trust.Unreadable);
        if (store is null && MeepPermissions.Definition(name.Name) is { } meeps)
            return Defined(name, proofs, meeps, directives, PermissionDefinitions.None);
        if (store is null)
            return new Requirement(name, Trust.NoStore);
        if (MeepPermissions.WhyNotKnown(name.Name) is { } unknown)
            return new Requirement(name, unknown);
        if (store.Permissions.Find(name.Name) is not { } definition)
        {
            // A name the store doesn't define can still be needed (Q289): any trusted authority confers it, and it
            // requires nothing, unless the pipeline's own directive narrows that. meep lsp warns about it.
            var open = directives.TryGetValue(name.Name, out var narrowing) ? narrowing : new Permission(new PermissionName(name.Name));
            var undefined = name.Scope is null ? open : new Permission(name) { Authorities = open.Authorities, Requires = open.Requires };
            return new Requirement(undefined) { Name = name, Kept = true, Proofs = proofs };
        }
        return Defined(name, proofs, definition, directives, store.Permissions);
    }

    // A defined permission's requirement: the definition, narrowed by the pipeline's directive and the gate's scope,
    // checked against the definitions it's in.
    private static Requirement Defined(PermissionName name, ProofSet proofs, Permission definition, Dictionary<string, Permission> directives, PermissionDefinitions definitions)
    {
        var permission = directives.TryGetValue(name.Name, out var directive) ? definition.NarrowedBy(directive) : definition;
        if (name.Scope is not null)
            permission = new Permission(name) { Authorities = permission.Authorities, Requires = permission.Requires };
        var failures = definitions.Check(permission);
        if (failures.Count > 0)
            return new Requirement(name, $"{name} isn't well defined here: {string.Join(" ", failures.Select(f => f.Reason))}");
        return new Requirement(permission) { Name = name, Kept = true, Proofs = proofs };
    }

    // The requirements worked out for one store and one set of the pipeline's options.
    private sealed class Resolved(TrustStore? store, int generation, Dictionary<string, Permission> directives, bool unreadable)
    {
        public bool Unreadable => unreadable;

        public TrustStore? Store => store;

        public int Generation => generation;

        public Dictionary<string, Permission> Directives => directives;

        public ConcurrentDictionary<(PermissionName Name, ProofSet Proofs), Requirement> ByName { get; } = new();
    }
}

/// <summary>
/// The permissions a module needs before a message may reach it: its <c>@permission</c> modifiers and what its verb
/// requires (<see cref="RequiresPermissionAttribute"/>), checked against the message's family before it's delivered.
/// </summary>
/// <param name="keeper">The pipeline's gatekeeper.</param>
/// <param name="needed">The permissions, in the order written, modifiers first.</param>
/// <param name="proofs">The kinds of proof the credentials must have been verified with (<c>@proof</c>), or any.</param>
internal sealed class Gate(Gatekeeper keeper, PermissionName[] needed, ProofSet proofs = default)
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
                why = (lines ??= new PermissionLines(store)).WhyNotMet(message, requirement);
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
            requirements[i] = keeper.Resolve(needed[i], store, proofs);
        _worked = new Worked(store, generation, requirements);
        return requirements;
    }

    private sealed record Worked(TrustStore? Store, int Generation, Requirement[] Requirements);
}

/// <summary>Why a gate refused a message: each permission it lacked, and why.</summary>
/// <param name="Needed">The permissions it lacked.</param>
/// <param name="Reasons">Why, for each, in the same order.</param>
internal sealed record Refusal(IReadOnlyList<PermissionName> Needed, IReadOnlyList<string> Reasons);

/// <summary>
/// The kinds of proof a gate accepts (<c>@proof</c>, Q299): how whoever presented a credential proved they hold its key,
/// as its <see cref="VerifiedMessage.Proof"/> says. The default accepts any, a bearer credential included.
/// </summary>
/// <param name="Mask">A bit for each <see cref="Proof"/> accepted, or 0 for any.</param>
internal readonly record struct ProofSet(int Mask)
{
    /// <summary>The kinds of proof in <paramref name="proofs"/>, or any if there are none.</summary>
    public static ProofSet Of(IReadOnlyList<Proof> proofs)
    {
        var mask = 0;
        foreach (var proof in proofs)
            mask |= 1 << (int)proof;
        return new ProofSet(mask);
    }

    /// <summary>Whether any kind of proof will do.</summary>
    public bool IsAny => Mask == 0;

    /// <summary>Whether <paramref name="proof"/> will do.</summary>
    public bool Allows(Proof proof) => IsAny || (Mask & (1 << (int)proof)) != 0;

    /// <summary>The kinds accepted, in words: "a signed request, a connection's proof or TLS".</summary>
    public override string ToString()
    {
        if (IsAny)
            return "any proof";
        var mask = Mask;
        var words = Enum.GetValues<Proof>().Where(p => (mask & (1 << (int)p)) != 0).Select(Describe).ToList();
        return words.Count == 1 ? words[0] : $"{string.Join(", ", words[..^1])} or {words[^1]}";
    }

    /// <summary>A kind of proof in words.</summary>
    public static string Describe(Proof proof) => proof switch
    {
        Proof.Bearer => "none, as a bearer credential",
        Proof.RequestSignature => "a signed request",
        Proof.Connection => "a connection's proof",
        Proof.Tls => "TLS",
        Proof.Pipeline => "the pipeline's own proof, when it loaded",
        _ => "no proof",
    };
}
