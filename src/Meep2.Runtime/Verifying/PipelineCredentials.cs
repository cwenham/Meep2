using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// A pipeline's own credential, verified when the pipeline starts (Q252): the <see cref="VerifiedMessage"/> every
/// message its sources emit descends from, so Meep's own permissions are held by them unless the pipeline starts with
/// less (<c>#holds</c>).
/// </summary>
internal static class PipelineCredentials
{
    /// <summary>What a pipeline's own verification records as emitting it.</summary>
    public static ModuleInfo Emitter { get; } = new(0, "Pipeline", "Pipeline");

    /// <summary>
    /// Verifies the credential <paramref name="options"/> give the pipeline: it holds what its credential does, narrowed
    /// to what <see cref="PipelineOptions.Holds"/> lists, if it lists anything. What it lists but its credential doesn't
    /// hold is noted, not held. It never expires: messages expire no later than their parents.
    /// </summary>
    public static VerifiedMessage Verify(PipelineOptions options, TrustStore? store)
    {
        ArgumentNullException.ThrowIfNull(options);
        var credential = options.Credential;
        var held = credential.Permissions;
        List<string> notes = [];
        if (credential.KeyFingerprint is { } signer && store?.Revocations.WhyKeyRevoked(signer) is { } revoked)
        {
            // A signer whose key has been revoked vouches for nothing (Q318).
            held = [];
            notes.Add($"It's signed by {credential.Holder}, but {revoked} So it holds nothing.");
        }
        if (options.Holds is { } holds)
        {
            var kept = new List<PermissionName>();
            foreach (var wanted in holds)
            {
                if (held.Any(h => h.Covers(wanted)))
                {
                    if (!kept.Contains(wanted))
                        kept.Add(wanted);
                }
                else
                {
                    notes.Add(credential.Vouch == PipelineVouch.Nobody
                        ? $"#holds lists {wanted}, but this pipeline has no credential of its own, as {credential.Why}, so it doesn't hold it."
                        : $"#holds lists {wanted}, but the pipeline's own credential doesn't hold it, so it isn't held.");
                }
            }
            held = kept;
        }
        var verified = new VerifiedMessage(credential)
        {
            Kind = VerificationKind.Pipeline,
            Permissions = held,
            Notes = notes,
            Proof = Proof.Pipeline,
            TrustStoreVersion = store?.Version ?? 0,
            RevocationVersion = store?.RevocationVersion ?? 0,
        };
        verified.EmittedBy = Emitter;
        return verified;
    }

    /// <summary>Who a pipeline's verification is of, as a refusal starts: "This pipeline's own credential, the owner's (alice),".</summary>
    public static string Who(PipelineCredential credential) => credential.Vouch switch
    {
        PipelineVouch.Owner => $"This pipeline's own credential, the owner's ({credential.Holder}),",
        PipelineVouch.Signer => $"This pipeline's own credential, {credential.Holder}'s,",
        _ => $"This pipeline has no credential of its own, as {credential.Why}, so it",
    };
}

/// <summary>
/// How a scoped permission is matched against what a module is about to touch (<see cref="PermissionTarget"/>): a file's
/// scope by its fixed start's real form (Q269), an endpoint's with any port if it names none, and anything else as written.
/// </summary>
internal static class Targets
{
    /// <summary>
    /// A target whose real form, for a file, is worked out the first time a scope is matched against it, and then kept,
    /// so a module holding an unscoped permission makes no system call to check one.
    /// </summary>
    public static PermissionTarget Lazy(PermissionTarget target) =>
        target.Kind == PermissionTargetKind.File ? new LazyFile(target.Text) : target;

    /// <summary>Whether holding <paramref name="held"/> allows <paramref name="name"/> on <paramref name="target"/>.</summary>
    public static bool Allows(PermissionName held, string name, PermissionTarget target) =>
        held.Name == name && (held.Scope is null || Matches(held.Scope, target));

    /// <summary>Whether <paramref name="target"/> is within <paramref name="scope"/>, read for the target's kind.</summary>
    public static bool Matches(PermissionScope scope, PermissionTarget target) => target switch
    {
        LazyFile file => FileScope(scope).Matches(file.Real),
        { Kind: PermissionTargetKind.File } => FileScope(scope).Matches(Meep2.Shared.RealPaths.Of(target.Text)),
        { Kind: PermissionTargetKind.Endpoint } when IsKeyword(scope) => false,
        { Kind: PermissionTargetKind.Addresses } => ScopeFor(scope, PermissionTargetKind.Endpoint).Matches(target.Text),
        _ => ScopeFor(scope, target.Kind).Matches(target.Text),
    };

    // Whether a network scope's host is public or private, which only addresses of that kind match, never a name (Q313).
    private static bool IsKeyword(PermissionScope scope)
    {
        var colon = scope.Pattern.IndexOf(':', StringComparison.Ordinal);
        var host = colon < 0 ? scope.Pattern : scope.Pattern[..colon];
        return host is NetworkAddresses.PublicScope or NetworkAddresses.PrivateScope;
    }

    /// <summary>
    /// <paramref name="scope"/> as it's matched against a target of <paramref name="kind"/>. For a file, its fixed start,
    /// the parts before the first wildcard, in its real form, so <c>/tmp/**</c> on a Mac is <c>/private/tmp/**</c> and
    /// <c>~/Notes/**</c> is <c>/Users/you/notes/**</c> when that's the case on disk. For an endpoint, in lower case,
    /// with <c>:*</c> added when it names no port.
    /// </summary>
    public static PermissionScope ScopeFor(PermissionScope scope, PermissionTargetKind kind) => kind switch
    {
        PermissionTargetKind.File or PermissionTargetKind.RealFile => FileScope(scope),
        PermissionTargetKind.Endpoint => EndpointScope(scope),
        _ => scope,
    };

    // Scopes' real forms, worked out at most once a second each, since a scope is matched for every message a module
    // checks and its folders rarely change. Bounded, so scopes made on the fly can't fill it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (PermissionScope Real, long Until)> s_fileScopes = new(StringComparer.Ordinal);

    private static PermissionScope FileScope(PermissionScope scope)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (s_fileScopes.TryGetValue(scope.Pattern, out var known) && now < known.Until)
            return known.Real;
        var real = RealFileScope(scope);
        if (s_fileScopes.Count < 1024 || s_fileScopes.ContainsKey(scope.Pattern))
            s_fileScopes[scope.Pattern] = (real, now + System.Diagnostics.Stopwatch.Frequency);
        return real;
    }

    private static PermissionScope RealFileScope(PermissionScope scope)
    {
        var parts = scope.Pattern.Split('/');
        var fixedParts = 0;
        while (fixedParts < parts.Length && parts[fixedParts].AsSpan().IndexOfAny('*', '?') < 0)
            fixedParts++;
        var start = string.Join('/', parts[..fixedParts]);
        if (start.Length == 0 || !(start.StartsWith('/') || start == "~" || start.StartsWith("~/", StringComparison.Ordinal)))
            return scope; // a scope that doesn't start at a folder can't be read in real form
        var real = Meep2.Shared.RealPaths.Of(start);
        var rest = parts[fixedParts..];
        var pattern = rest.Length == 0 ? real : $"{real.TrimEnd('/')}/{string.Join('/', rest)}";
        return PermissionScope.TryParse(pattern) ?? scope;
    }

    private static PermissionScope EndpointScope(PermissionScope scope)
    {
        var pattern = scope.Pattern.ToLowerInvariant();
        var host = pattern.StartsWith('[') && pattern.IndexOf(']', StringComparison.Ordinal) is var close and > 0
            ? pattern[(close + 1)..]
            : pattern;
        if (!host.Contains(':', StringComparison.Ordinal))
            pattern += ":*";
        return PermissionScope.TryParse(pattern) ?? scope;
    }
}

/// <summary>A file whose real form is worked out once, the first time a scope is matched against it.</summary>
internal sealed record LazyFile(string Path) : PermissionTarget(PermissionTargetKind.File, Path)
{
    private string? _real;

    /// <summary>The path's real form.</summary>
    public string Real => _real ??= Meep2.Shared.RealPaths.Quickly(Path);
}
