using Meep2.Security;
namespace Meep2.Plugins;

/// <summary>Where plugins come from and go, and which ones Meep accepts.</summary>
public sealed record PluginOptions
{
    /// <summary>
    /// Meep's own folder, holding the plugin cache and the keys you trust: <c>~/.meep</c>, or the <c>MEEP_HOME</c>
    /// environment variable if it's set (Q106).
    /// </summary>
    public required string Home { get; init; }

    /// <summary>Where installed plugins are kept.</summary>
    public string Cache => Path.Combine(Home, "plugins");

    /// <summary>
    /// The file listing the SSH keys whose plugins you trust, in the allowed-signers form <c>ssh-keygen -Y verify</c>
    /// reads (Q101).
    /// </summary>
    public string TrustedKeysFile => Path.Combine(Home, "allowed_signers");

    /// <summary>
    /// Developer mode, as <c>meep run --dev</c> turns on: plugins on this machine load without being signed (Q36).
    /// Plugins from NuGet still need signatures, and GitHub plugins their SHAs.
    /// </summary>
    public bool DeveloperMode { get; init; }

    /// <summary>
    /// Whether only plugins already in the cache are used, with nothing downloaded, as while editing (Q138). A
    /// plugin that isn't there is refused, saying so.
    /// </summary>
    public bool Offline { get; init; }

    /// <summary>
    /// Where GitHub releases are downloaded from, and the keys of GitHub users, for <c>meep keys trust github:name</c>.
    /// Tests point it at a server of their own.
    /// </summary>
    public Uri GitHub { get; init; } = new("https://github.com/");

    /// <summary>The NuGet feeds to look in, in order: URLs of v3 feeds, or folders of packages.</summary>
    public IReadOnlyList<string> NuGetSources { get; init; } = ["https://api.nuget.org/v3/index.json"];

    /// <summary>The keys whose plugins you trust. Null means the ones in <see cref="TrustedKeysFile"/>.</summary>
    public TrustedKeys? Trusted { get; init; }

    /// <summary>The largest plugin download, a GitHub release asset or a NuGet package (S5). 256 MiB unless set.</summary>
    public long MaxDownload
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDownload), "The download limit must be at least 1 byte.");
    } = 256L * 1024 * 1024;

    /// <summary>The settings for this user: Meep's folder is <c>MEEP_HOME</c>, or <c>~/.meep</c>.</summary>
    public static PluginOptions ForUser(bool developerMode = false) => new()
    {
        Home = Environment.GetEnvironmentVariable("MEEP_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".meep"),
        DeveloperMode = developerMode,
    };

    /// <summary>
    /// The keys you trust: <see cref="Trusted"/>, or those in <see cref="TrustedKeysFile"/>. Throws
    /// <see cref="PluginException"/> if the file can't be used.
    /// </summary>
    public TrustedKeys TrustedKeys()
    {
        if (Trusted is not null)
            return Trusted;
        try
        {
            return Security.TrustedKeys.Load(TrustedKeysFile);
        }
        catch (TrustStoreException exception)
        {
            throw new PluginException(exception.Message, exception);
        }
    }
}
