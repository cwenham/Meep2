using Meep2.SDK.Modules;

namespace Meep2.SDK.Security;

/// <summary>
/// Declares a permission a verb's inflection always needs, which a pipeline can't remove (Q263): every message that
/// reaches it must descend, on every line, from a verified credential holding the permission, or it's refused with a
/// <see cref="PermissionDenied"/> error, as <c>@permission</c> does. Writing <c>@permission</c> on the module adds to
/// it, and both are needed. On a source, the pipeline must hold it for the source to start. For example,
/// <c>[RequiresPermission("AuthorisePermission")]</c>.
/// </summary>
/// <remarks>
/// <para>
/// It goes on the inflection's class, so a verb's inflections can need different permissions. A message that more
/// than one inflection takes by its type needs what each of them needs, since which one handles it may only be
/// decided later.
/// </para>
/// <para>
/// The name may have a scope, as in <c>meep.files.read:~/notes/**</c>. A name that isn't a permission's is reported
/// when the verb is registered. For a check that depends on the message, such as which file it's about, use
/// <see cref="ModuleContext.Demand"/> instead.
/// </para>
/// </remarks>
/// <param name="permission">The permission's name, and its scope if it has one.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    /// <summary>The permission as written.</summary>
    public string Permission { get; } = permission;

    /// <summary>
    /// The permissions <paramref name="moduleType"/>'s attributes declare, in the order written.
    /// </summary>
    /// <exception cref="FormatException">One of them isn't a permission's name.</exception>
    public static IReadOnlyList<PermissionName> Of(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        var attributes = moduleType.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: false);
        if (attributes.Length == 0)
            return [];
        var names = new List<PermissionName>(attributes.Length);
        foreach (RequiresPermissionAttribute attribute in attributes)
        {
            if (!PermissionName.TryParse(attribute.Permission, out var name))
                throw new FormatException($"{moduleType.Name} requires \"{attribute.Permission}\", which isn't a permission's name.");
            if (!names.Contains(name))
                names.Add(name);
        }
        return names;
    }
}
