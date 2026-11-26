using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Cli;

/// <summary>
/// Warns about permissions a file's gates need that this machine's trust store doesn't define (Q289), and names
/// starting <c>meep.</c> that aren't Meep's own. A gate still
/// works with one, conferred by any trusted authority and requiring nothing, but a misspelt name would then let
/// through whatever credential carries the misspelling, so the editor says so.
/// </summary>
/// <param name="home">Meep's home folder, whose <c>trust</c> folder is read, or null for none.</param>
internal sealed class LspPermissions(string? home)
{
    private TrustStore? _store;

    /// <summary>
    /// A warning for each permission a module, or a <c>#permission</c> line, names that isn't defined, and for each name
    /// starting <c>meep.</c> that isn't one of Meep's own, such as a misspelling, which is warned of with or without a
    /// trust store.
    /// </summary>
    public IReadOnlyList<Diagnostic> Warnings(PipelineShape shape, string path)
    {
        var store = Store();
        var warnings = new List<Diagnostic>();
        void Check(PermissionName name, Func<SourceSpan> span)
        {
            var why = MeepPermissions.WhyNotKnown(name.Name)
                ?? (store is not null && store.Permissions.Find(name.Name) is null
                    ? $"{name.Name} isn't defined in this machine's trust store (permissions.meep), so any trusted authority confers it and it requires nothing. Check the spelling, or define it."
                    : null);
            if (why is not null)
            {
                var at = span();
                warnings.Add(new Diagnostic(DiagnosticSeverity.Warning, why, at, at.File ?? path));
            }
        }

        foreach (var directive in shape.Syntax.Permissions)
            Check(directive.Permission.Name, () => directive.Span);
        foreach (var module in shape.Modules)
        {
            var written = module.Syntax.Written;
            foreach (var name in module.Permissions)
            {
                Check(name, () => written.Modifiers.FirstOrDefault(m => m.Name == "permission" && m.Value is not null
                    && PermissionName.TryParse(shape.Syntax.TextOf(m.Value.Span).Trim('"', '\''), out var named) && named == name)?.Span ?? written.VerbSpan);
            }
        }
        return warnings;
    }

    // This machine's store as its files are now, or null if there's none or it can't be read. Never made here.
    private TrustStore? Store()
    {
        if (home is null)
            return null;
        var folder = TrustFolder.In(home);
        if (!Directory.Exists(folder.Path))
            return _store = null;
        try
        {
            return _store = _store?.Reload() ?? TrustStore.Load(folder);
        }
        catch (Exception exception) when (exception is TrustStoreException or IOException or UnauthorizedAccessException or FormatException)
        {
            return _store = null;
        }
    }
}
