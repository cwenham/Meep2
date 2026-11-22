namespace Meep2.Meeplang.Editing;

/// <summary>One change to a pipeline file's text, aimed at a module, or at the rest of the file (goal 4).</summary>
/// <param name="Module">The module: its name, its number, or a verb only one module has. Empty for edits to defaults, constants and plugins.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Name">The parameter, modifier, setting, constant or plugin, for those edits.</param>
/// <param name="Value">
/// The new value as Meeplang text, such as <c>5s</c> or <c>"hello"</c>; the new name for <see cref="EditKind.Rename"/>;
/// or the new module's line for <see cref="EditKind.Add"/> and <see cref="EditKind.Insert"/>.
/// </param>
public sealed record SourceEdit(string Module, EditKind Kind, string? Name = null, string? Value = null)
{
    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        EditKind.Set => Name is null ? $"set {Module}'s value to {Value}" : $"set {Module}'s {Name} to {Value}",
        EditKind.Unset => Name is null ? $"remove {Module}'s value" : $"remove {Module}'s {Name}",
        EditKind.SetModifier => $"set @{Name}{(Value is null ? "" : "=" + Value)} on {Module}",
        EditKind.RemoveModifier => $"remove @{Name} from {Module}",
        EditKind.Rename => Value is null ? $"take {Module}'s name away" : $"name {Module} {Value}",
        EditKind.Add => $"add {Value} under {Module}",
        EditKind.Insert => $"insert {Value} after {Module}",
        EditKind.Remove => $"remove {Module}",
        EditKind.RemoveBranch => $"remove {Module} and everything under it",
        EditKind.SetDefault => $"set the default {Name} to {Value}",
        EditKind.UnsetDefault => $"remove the default {Name}",
        EditKind.SetConstant => $"set the constant {Name} to {Value}",
        EditKind.UnsetConstant => $"remove the constant {Name}",
        EditKind.AddPlugin => $"add the plugin {Value}",
        EditKind.RemovePlugin => $"remove the plugin {Name}",
        _ => Kind.ToString(),
    };
}
