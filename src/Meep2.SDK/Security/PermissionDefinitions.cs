namespace Meep2.SDK.Security;

/// <summary>
/// The permissions a trust store defines, from its <c>permissions.meep</c> (Q257), and the checks that say whether a
/// permission is well defined: those that need no credentials.
/// </summary>
/// <remarks>
/// A permission is well defined when its name is defined here, it's no looser than its definition, and everything it
/// refers to exists: every authority it names is in the trust store, every permission it requires is defined, and it
/// doesn't require itself through a chain. Whether a message's credentials meet it is for <c>Verify.Permission</c>.
/// </remarks>
public sealed class PermissionDefinitions
{
    private readonly Dictionary<string, Permission> _byName;
    private readonly HashSet<string> _authorities;

    /// <summary>
    /// Holds these definitions, one for each name, checked against the names of the authorities the trust store has.
    /// Throws <see cref="FormatException"/> if a name is defined twice.
    /// </summary>
    public PermissionDefinitions(IEnumerable<Permission> definitions, IEnumerable<string> authorities)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(authorities);
        _byName = new Dictionary<string, Permission>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!_byName.TryAdd(definition.Name.Name, definition))
                throw new FormatException($"{definition.Name.Name} is defined twice. A permission has one definition; a pipeline's {Permission.Directive} narrows it.");
        }
        _authorities = new HashSet<string>(authorities, StringComparer.Ordinal);
        All = [.. _byName.Values];
        Problems = [.. All.SelectMany(definition => References(definition).Select(failure => $"{definition.Name.Name}: {failure.Reason}"))];
    }

    /// <summary>No definitions.</summary>
    public static PermissionDefinitions None { get; } = new([], []);

    /// <summary>Every definition, in the order they were written.</summary>
    public IReadOnlyList<Permission> All { get; }

    /// <summary>
    /// What's wrong with the definitions themselves, a sentence each: authorities that aren't in the trust store,
    /// requirements that aren't defined, and permissions that require themselves. A definition with a problem isn't
    /// well defined, so it can't be met until it's fixed.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Reads definitions written as <see cref="Permission.ParseDirective"/> reads them, one to a line, with blank lines
    /// and <c>//</c> comments. Throws <see cref="FormatException"/>, naming the line, if one can't be read.
    /// </summary>
    public static PermissionDefinitions Parse(string text, IEnumerable<string> authorities)
    {
        ArgumentNullException.ThrowIfNull(text);
        var definitions = new List<Permission>();
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;
            try
            {
                definitions.Add(Permission.ParseDirective(trimmed));
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Line {number}: {exception.Message}", exception);
            }
        }
        return new PermissionDefinitions(definitions, authorities);
    }

    /// <summary>The definition of the permission called <paramref name="name"/>, leaving out any scope, or null.</summary>
    public Permission? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Why <paramref name="permission"/> isn't well defined, or nothing if it is: it isn't defined
    /// (<see cref="VerificationFailureCode.UnknownPermission"/>), it's looser than its definition
    /// (<see cref="VerificationFailureCode.WiderThanDefinition"/>), or it refers to something that isn't there or
    /// requires itself (<see cref="VerificationFailureCode.UndefinedRequirement"/>).
    /// </summary>
    public IReadOnlyList<VerificationFailure> Check(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (Find(permission.Name.Name) is not { } definition)
        {
            return [new VerificationFailure(VerificationFailureCode.UnknownPermission,
                $"{permission.Name.Name} isn't defined in the trust store's permissions.")];
        }
        var failures = new List<VerificationFailure>(permission.WiderThan(definition));
        failures.AddRange(References(permission));
        if (!ReferenceEquals(permission, definition))
            failures.AddRange(References(definition).Where(failure => !failures.Contains(failure)));
        return failures;
    }

    /// <summary>
    /// Whether <paramref name="permission"/> is well defined: <see cref="Check"/> finds nothing wrong with it.
    /// </summary>
    public bool IsWellDefined(Permission permission) => Check(permission).Count == 0;

    // What a permission refers to that isn't there: authorities, required permissions, and a chain back to itself.
    private List<VerificationFailure> References(Permission permission)
    {
        var failures = new List<VerificationFailure>();
        void Undefined(string reason) => failures.Add(new VerificationFailure(VerificationFailureCode.UndefinedRequirement, reason));

        foreach (var authority in permission.Authorities.Where(a => !_authorities.Contains(a)))
            Undefined($"the authority {authority} isn't in the trust store.");
        foreach (var required in permission.Requires.Where(r => Find(r.Name) is null))
            Undefined($"it requires {required.Name}, which isn't defined.");
        if (RequiresItself(permission.Name.Name, permission.Requires) is { } chain)
            Undefined($"it requires itself, through {string.Join(" → ", chain)}.");
        return failures;
    }

    // The chain of requirements that leads back to the permission called name, or null if none does.
    private List<string>? RequiresItself(string name, IReadOnlyList<PermissionName> requires)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Name, List<string> Path)>();
        foreach (var required in requires)
            stack.Push((required.Name, [name, required.Name]));
        while (stack.TryPop(out var next))
        {
            if (next.Name == name)
                return next.Path;
            if (!seen.Add(next.Name) || Find(next.Name) is not { } definition)
                continue;
            foreach (var required in definition.Requires)
                stack.Push((required.Name, [.. next.Path, required.Name]));
        }
        return null;
    }
}
