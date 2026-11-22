namespace Meep2.Plugins;

/// <summary>
/// The one <see cref="HttpClient"/> Meep's own downloads share (plugins, keys from GitHub, pipelines and includes from
/// URLs), so connections are pooled and each says it's Meep in its User-Agent. Each request sets its own time limit.
/// Fetch and Send have their own, which follows redirects itself.
/// </summary>
public static class MeepHttp
{
    private static readonly Lazy<HttpClient> s_client = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meep2/1.0");
        return client;
    });

    /// <summary>The shared client.</summary>
    public static HttpClient Client => s_client.Value;
}
