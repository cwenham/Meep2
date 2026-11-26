using System.Globalization;
using Meep2.SDK.Modules;

namespace Meep2.SDK.Security;

/// <summary>
/// What a module is about to touch, which a scoped permission must match: a file, a network endpoint, or anything
/// else, written as text. The kind says how a scope is read against it.
/// </summary>
internal record PermissionTarget(PermissionTargetKind Kind, string Text)
{
    /// <summary>
    /// A file, by its full path. The runtime reads it in its real form (<c>Meep2.Shared.RealPaths</c>) before matching
    /// a scope, and a scope's fixed start the same way, but only when a scope is matched at all.
    /// </summary>
    public static PermissionTarget File(string path) => new(PermissionTargetKind.File, path);

    /// <summary>A file by its real form, already worked out.</summary>
    public static PermissionTarget RealFile(string realPath) => new(PermissionTargetKind.RealFile, realPath);

    /// <summary>
    /// A network endpoint: the host as written, in lower case, in its ASCII form, without a final dot, then a colon and
    /// the port, as <c>api.example.com:443</c> or <c>[::1]:8080</c>. Listening on every network is <c>0.0.0.0</c> or
    /// <c>[::]</c>, as written. A scope without a port, such as <c>*.example.com</c>, matches any port.
    /// </summary>
    public static PermissionTarget Endpoint(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Any(c => c > 127))
        {
            try
            {
                name = new IdnMapping().GetAscii(name);
            }
            catch (ArgumentException)
            {
                // Not a name IDNA can write; matched as written, so a scope naming it in ASCII doesn't.
            }
        }
        return new(PermissionTargetKind.Endpoint, Peers.Endpoint(name, port));
    }

    /// <summary>
    /// The addresses of one reach on a port, as <c>public:443</c>, which only the keyword scopes and scopes whose host is
    /// a wildcard match (Q313).
    /// </summary>
    public static PermissionTarget Addresses(NetworkReach reach, int port) =>
        new(PermissionTargetKind.Addresses, $"{reach.Describe()}:{port}");

    /// <summary>Anything else, matched as written.</summary>
    public static PermissionTarget Of(string text) => new(PermissionTargetKind.Text, text);

    /// <summary>The target as written.</summary>
    public sealed override string ToString() => Text;
}

/// <summary>What kind of thing a <see cref="PermissionTarget"/> is.</summary>
internal enum PermissionTargetKind
{
    /// <summary>Text, matched as written.</summary>
    Text,

    /// <summary>A file's full path, not yet in its real form.</summary>
    File,

    /// <summary>A file's real path.</summary>
    RealFile,

    /// <summary>A host and port.</summary>
    Endpoint,

    /// <summary>The public or private addresses on a port.</summary>
    Addresses,
}
