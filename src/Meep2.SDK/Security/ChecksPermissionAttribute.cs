namespace Meep2.SDK.Security;

/// <summary>
/// Says, for tools, that a verb's inflection checks a permission in its own code against what it touches, as Save
/// checks <c>meep.files.write</c> for each file with <c>ModuleContext.DemandFile</c> (Q315). It changes nothing at run
/// time: the check is the call. <c>meep lsp</c>'s hover and the verb catalogue show it, so people can see what a verb
/// needs; the graph's lock doesn't, as nearly every module that touches the machine would have one.
/// </summary>
/// <param name="permission">The permission's name, as in <c>meep.files.write</c>.</param>
/// <param name="on">What it's checked against, for people: "each file it writes".</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ChecksPermissionAttribute(string permission, string on) : Attribute
{
    /// <summary>The permission's name.</summary>
    public string Permission { get; } = permission;

    /// <summary>What it's checked against, for people.</summary>
    public string On { get; } = on;

    /// <summary>What <paramref name="moduleType"/>'s attributes say it checks, in the order written, as "meep.files.write, on each file it writes".</summary>
    /// <exception cref="FormatException">One of them isn't a permission's name.</exception>
    public static IReadOnlyList<string> Of(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        var checks = new List<string>();
        foreach (ChecksPermissionAttribute attribute in moduleType.GetCustomAttributes(typeof(ChecksPermissionAttribute), inherit: false))
        {
            if (!PermissionName.TryParse(attribute.Permission, out var name))
                throw new FormatException($"{moduleType.Name} checks \"{attribute.Permission}\", which isn't a permission's name.");
            checks.Add($"{name}, on {attribute.On}");
        }
        return checks;
    }
}
