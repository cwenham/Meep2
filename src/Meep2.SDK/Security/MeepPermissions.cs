namespace Meep2.SDK.Security;

/// <summary>
/// Meep's own permissions, for what Meep does on the machine (phase 6): reading and writing files, listening and
/// sending on the network, running scripts, and the daemon's capabilities. Their names start with <c>meep.</c>, which
/// is kept for them, so they can't clash with names people make.
/// </summary>
/// <remarks>
/// <para>
/// Meep defines them itself, with no requirements, so they're defined without a trust store (Q279). A
/// <c>#permission meep.…</c> line in the trust store's <c>permissions.meep</c>, or in a pipeline, narrows Meep's
/// definition, as a pipeline's line narrows any other.
/// </para>
/// <para>
/// File permissions are scoped by path, as in <c>meep.files.write:~/notes/**</c>, matched against the path's real
/// form: whole, with symbolic links and <c>..</c> followed, in the case it has on disk (Q269). Network permissions are
/// scoped by host and port, as in <c>meep.network.send:*.example.com:443</c>, or <c>meep.network.listen:localhost</c>
/// for any port; see <see cref="PermissionTarget.Endpoint(string, int)"/>.
/// </para>
/// </remarks>
public static class MeepPermissions
{
    /// <summary>The start of every name Meep keeps for its own permissions.</summary>
    public const string Prefix = "meep.";

    /// <summary>Reading a file: Read, Fetch.File, and Learn and Classify with <c>file=</c>.</summary>
    public const string FilesRead = "meep.files.read";

    /// <summary>Writing a file: Save, Learn saving its tree, and Forget.</summary>
    public const string FilesWrite = "meep.files.write";

    /// <summary>Listening on the network: every Listen.</summary>
    public const string NetworkListen = "meep.network.listen";

    /// <summary>Sending on the network to an address of the pipeline's choosing: Fetch.HTTP, and Send.HTTP, TCP and WebSocket.</summary>
    public const string NetworkSend = "meep.network.send";

    /// <summary>Running a script embedded in a pipeline. Nothing in Meep runs scripts yet.</summary>
    public const string ScriptsRun = "meep.scripts.run";

    /// <summary>Watching the daemon: its pipelines, counts and taps. Daemon tokens map to it in phase 7.</summary>
    public const string DaemonObserve = "meep.daemon.observe";

    /// <summary>Sending messages into the daemon's pipelines. Daemon tokens map to it in phase 7.</summary>
    public const string DaemonInject = "meep.daemon.inject";

    /// <summary>Starting, stopping and changing the daemon's pipelines. Daemon tokens map to it in phase 7.</summary>
    public const string DaemonControl = "meep.daemon.control";

    /// <summary>Every permission Meep defines, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } =
        [FilesRead, FilesWrite, NetworkListen, NetworkSend, ScriptsRun, DaemonObserve, DaemonInject, DaemonControl];

    /// <summary>
    /// What a pipeline's own credential holds when it holds anything (Q252): every permission for what a pipeline does on
    /// the machine. Not the daemon's, which are for the daemon's clients rather than its pipelines.
    /// </summary>
    public static IReadOnlyList<PermissionName> ForPipelines { get; } =
        [new(FilesRead), new(FilesWrite), new(NetworkListen), new(NetworkSend), new(ScriptsRun)];

    private static readonly Dictionary<string, Permission> s_definitions =
        All.ToDictionary(name => name, name => new Permission(new PermissionName(name)), StringComparer.Ordinal);

    /// <summary>Whether <paramref name="name"/>, without any scope, starts with <c>meep.</c>, the prefix Meep keeps.</summary>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="name"/>, without any scope, is one of Meep's own permissions.</summary>
    public static bool IsMeeps(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return s_definitions.ContainsKey(name);
    }

    /// <summary>Meep's own definition of <paramref name="name"/>, with no requirements, or null if it isn't one of Meep's.</summary>
    public static Permission? Definition(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return s_definitions.GetValueOrDefault(name);
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be used, if it starts with <c>meep.</c> but isn't one of Meep's own, such as a
    /// misspelling; or null if it's fine.
    /// </summary>
    public static string? WhyNotKnown(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsReserved(name) || IsMeeps(name))
            return null;
        return $"Meep has no permission called {name}: names starting {Prefix} are Meep's own, and Meep has {string.Join(", ", All)}.";
    }
}
