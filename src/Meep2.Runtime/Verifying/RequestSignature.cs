using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// Proof that whoever sent a request holds its credential's key: an HTTP Message Signature (RFC 9421) over the request,
/// made with that key as an SSH signature in the <c>meep-request</c> namespace (Q253). It binds the request (method,
/// address and, through <c>Content-Digest</c> (RFC 9530), its body), the moment (a creation time within a window, and a
/// nonce that can't be used twice within it) and the server it's for (its <c>@authority</c>, one of this machine's
/// names or those given with <c>audience=</c>).
/// </summary>
/// <remarks>
/// <code>
/// Meep-Credential: &lt;base64 SSH certificate&gt;
/// Content-Digest: sha-256=:…:
/// Signature-Input: meep=("@method" "@target-uri" "@authority" "content-digest");created=1732370000;nonce="r4nd0m";keyid="SHA256:…"
/// Signature: meep=:&lt;SSH signature, binary, in base64&gt;:
/// </code>
/// The signature is of the signature base RFC 9421 describes, as UTF-8, and can be made by hand with
/// <c>ssh-keygen -Y sign -n meep-request</c>. Behind a proxy that ends TLS, sign <c>"@path" "@query"</c> in place of
/// <c>"@target-uri"</c>, since the scheme the client sees isn't the one Meep does.
/// </remarks>
internal static class RequestSignature
{
    /// <summary>The SSH signature namespace requests are signed in.</summary>
    public const string Namespace = "meep-request";

    /// <summary>The signature's label in <c>Signature-Input</c> and <c>Signature</c>.</summary>
    public const string Label = "meep";

    // What the signature must cover, at least: the request's method, the server it's for, and its address, as
    // @target-uri or as @path and @query, and its body's digest if it has a body. @path and @query leave out only the
    // scheme, which a proxy that ends TLS changes (Q285).
    private static readonly string[] s_required = ["@method", "@authority"];
    private static readonly string[] s_address = ["@path", "@query"];

    /// <summary>Whether the request carries a signature at all.</summary>
    public static bool IsSigned(IHttpRequest request) =>
        Header(request, "Signature-Input") is not null || Header(request, "Signature") is not null;

    /// <summary>
    /// The components the request's signature says it covers, as named in <c>Signature-Input</c> (<c>@method</c>,
    /// <c>content-digest</c>, …), or null if it has no signature Meep can read. Whether the signature is good is
    /// <see cref="Check"/>'s question.
    /// </summary>
    public static IReadOnlyList<string>? Covered(IHttpRequest request)
    {
        if (!IsSigned(request) || !TryRead(request, out var input, out _, out _) || input.InnerList is not { } covered)
            return null;
        return [.. covered.Select(component => component.Item?.Value as string).OfType<string>()];
    }

    /// <summary>
    /// Checks the request's signature against <paramref name="credential"/>'s key, adding what it finds to
    /// <paramref name="verification"/>, and marks the proof as <see cref="Proof.RequestSignature"/> if it's good.
    /// </summary>
    public static void Check(Verification verification, Credential credential, IHttpRequest request, VerifyParameters parameters)
    {
        if (WhyNot(verification, credential, request, parameters) is { } problem)
            verification.Fail(problem.Code, problem.Reason);
        else
            verification.Proof = Proof.RequestSignature;
    }

    // Why the signature doesn't prove possession, or null if it does. Checked in an order that leaves the nonce unused
    // unless the signature itself is good, so nobody can use up another's nonces.
    private static VerificationFailure? WhyNot(Verification verification, Credential credential, IHttpRequest request, VerifyParameters parameters)
    {
        static VerificationFailure No(string reason) => new(VerificationFailureCode.NoProofOfPossession, reason);

        if (!TryRead(request, out var input, out var signatureBytes, out var problem))
            return No(problem);
        if (input.InnerList is not { } covered)
            return No("Its Signature-Input isn't a list of what's signed.");
        var components = new List<string>();
        foreach (var component in covered)
        {
            if (component.Item?.Value is not string name || component.Item.IsToken || component.Parameters.Count > 0)
                return No("Its Signature-Input names what's signed in a way Meep doesn't read: each must be a quoted name with nothing after it.");
            components.Add(name);
        }
        if (s_required.FirstOrDefault(required => !components.Contains(required)) is { } missing)
            return No($"Its signature doesn't cover {missing}, and Meep needs it to cover @method, @authority, and @target-uri or @path and @query.");
        if (!components.Contains("@target-uri") && s_address.FirstOrDefault(required => !components.Contains(required)) is { } part)
            return No($"Its signature doesn't cover @target-uri or {part}, so the request's address could have been changed. Meep needs it to cover @target-uri, or @path and @query.");

        if (request.IsStreamed)
            return new(VerificationFailureCode.BodyMismatch, "Its body is streamed, so it can't be checked against what was signed. Listen without stream=true to verify requests.");
        var body = BodyOf(request);
        if (body.Length > 0 && !components.Contains("content-digest"))
            return No("It has a body, and its signature doesn't cover content-digest, so the body could have been changed.");

        if (input.Parameter("created") is not long created)
            return No("Its Signature-Input has no created time.");
        if (input.Parameter("nonce") is not string nonce || nonce.Length == 0)
            return No("Its Signature-Input has no nonce, so it could be replayed.");
        if (input.Parameter("keyid") is { } keyId && !Equals(keyId, credential.KeyFingerprint))
            return No($"Its signature says it was made with {keyId}, which isn't the credential's key, {credential.KeyFingerprint}.");

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return No($"Its address, {request.Url}, can't be read.");
        var signatureBase = new StringBuilder();
        foreach (var name in components)
        {
            if (ValueOf(name, request, uri) is not { } value)
                return No($"Its signature covers {name}, which the request doesn't have.");
            signatureBase.Append('"').Append(name).Append("\": ").Append(value).Append('\n');
        }
        signatureBase.Append("\"@signature-params\": ").Append(StructuredFields.Serialize(input));

        SshSignature signature;
        try
        {
            signature = SshSignature.FromBinary(signatureBytes);
        }
        catch (FormatException exception)
        {
            return new(VerificationFailureCode.BadSignature, $"Its Signature isn't an SSH signature: {exception.Message}.");
        }
        if (signature.Namespace != Namespace)
            return new(VerificationFailureCode.BadSignature, $"Its signature is for {signature.Namespace}, not {Namespace}.");
        if (!signature.Key.Blob.AsSpan().SequenceEqual(credential.Key.Span))
            return No($"Its signature was made with {signature.Key.Fingerprint}, which isn't the credential's key, {credential.KeyFingerprint}.");
        if (!signature.Verifies(Encoding.UTF8.GetBytes(signatureBase.ToString())))
            return new(VerificationFailureCode.BadSignature, "Its signature isn't good: the request isn't what was signed, or another key signed it.");

        if (components.Contains("content-digest") && WhyNotDigest(request, body) is { } mismatch)
            return new(VerificationFailureCode.BodyMismatch, mismatch);

        var now = verification.Now;
        var made = DateTime.UnixEpoch.AddSeconds(Math.Clamp(created, 0, 253402300799));
        if (made < now - parameters.Window)
            return new(VerificationFailureCode.StaleSignature, $"Its signature was made at {CredentialCheck.Time(made)}, more than {Seconds(parameters.Window)} ago.");
        if (made > now + parameters.Window)
            return new(VerificationFailureCode.StaleSignature, $"Its signature says it was made at {CredentialCheck.Time(made)}, more than {Seconds(parameters.Window)} from now.");
        if (input.Parameter("expires") is long expires && DateTime.UnixEpoch.AddSeconds(Math.Clamp(expires, 0, 253402300799)) <= now)
            return new(VerificationFailureCode.StaleSignature, "Its signature has expired, as its Signature-Input says.");

        if (Audience.WhyNot(uri, parameters.Audience) is { } wrong)
            return new(VerificationFailureCode.WrongAudience, wrong);

        return Nonces.Use($"{credential.KeyFingerprint} {nonce}", made + parameters.Window, now) switch
        {
            NonceUse.Fresh => null,
            NonceUse.Seen => new(VerificationFailureCode.Replayed, "Its signature has been seen before: the request is a replay."),
            _ => new(VerificationFailureCode.Replayed, "Too many signed requests arrived within the window to remember them all, so this one can't be shown not to be a replay."),
        };
    }

    // The signature's parameters and its bytes, from Signature-Input and Signature, under Meep's label, or the only
    // label if there's one.
    private static bool TryRead(IHttpRequest request, out SfMember input, out byte[] signature, out string problem)
    {
        input = null!;
        signature = [];
        var inputText = Header(request, "Signature-Input");
        var signatureText = Header(request, "Signature");
        if (inputText is null || signatureText is null)
        {
            problem = "It has a signature, but not both Signature-Input and Signature.";
            return false;
        }
        Dictionary<string, SfMember> inputs, signatures;
        try
        {
            inputs = StructuredFields.ParseDictionary(inputText);
            signatures = StructuredFields.ParseDictionary(signatureText);
        }
        catch (FormatException exception)
        {
            problem = $"Its signature headers can't be read: {exception.Message}.";
            return false;
        }
        var label = inputs.ContainsKey(Label) ? Label : inputs.Count == 1 ? inputs.Keys.First() : null;
        if (label is null || !signatures.TryGetValue(label, out var value) || !inputs.TryGetValue(label, out var found))
        {
            problem = $"It has no signature labelled {Label}.";
            return false;
        }
        if (value.Item?.Value is not byte[] bytes)
        {
            problem = $"Its signature labelled {label} isn't a byte sequence.";
            return false;
        }
        input = found;
        signature = bytes;
        problem = "";
        return true;
    }

    // A component's value, as RFC 9421 section 2 derives it, or null if the request doesn't have it.
    private static string? ValueOf(string name, IHttpRequest request, Uri uri) => name switch
    {
        "@method" => request.Method,
        "@target-uri" => request.Url,
        "@authority" => AuthorityOf(uri),
        "@scheme" => uri.Scheme.ToLowerInvariant(),
        "@path" => uri.AbsolutePath,
        "@query" => uri.Query.Length == 0 ? "?" : uri.Query,
        "@request-target" => uri.PathAndQuery,
        ['@', ..] => null,
        _ => Header(request, name)?.Trim(),
    };

    // The authority as RFC 9421 has it: the host in lower case, with the port unless it's the scheme's default.
    internal static string AuthorityOf(Uri uri) =>
        (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}").ToLowerInvariant();

    private static string? Header(IHttpRequest request, string name)
    {
        if (request.Headers.TryGetValue(name, out var value))
            return value;
        foreach (var (key, found) in request.Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return found;
        }
        return null;
    }

    private static byte[] BodyOf(IHttpRequest request)
    {
        using var stream = request.OpenBody();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // Why the body doesn't match its Content-Digest, or null if it does. Every digest Meep knows must match, and there
    // must be one.
    private static string? WhyNotDigest(IHttpRequest request, byte[] body)
    {
        Dictionary<string, SfMember> digests;
        try
        {
            digests = StructuredFields.ParseDictionary(Header(request, "Content-Digest") ?? "");
        }
        catch (FormatException exception)
        {
            return $"Its Content-Digest can't be read: {exception.Message}.";
        }
        var checkedAny = false;
        foreach (var (algorithm, member) in digests)
        {
            byte[]? expected = algorithm switch
            {
                "sha-256" => SHA256.HashData(body),
                "sha-512" => SHA512.HashData(body),
                _ => null,
            };
            if (expected is null)
                continue;
            if (member.Item?.Value is not byte[] given || !CryptographicOperations.FixedTimeEquals(given, expected))
                return $"Its body doesn't match its Content-Digest ({algorithm}).";
            checkedAny = true;
        }
        return checkedAny ? null : "Its Content-Digest has no sha-256 or sha-512 digest, which are the ones Meep checks.";
    }

    private static string Seconds(TimeSpan window) => $"{window.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
}

/// <summary>
/// Whether a signed request was addressed to this server: its <c>@authority</c> is one of the names in
/// <c>audience=</c>, or without it, one of this machine's own names or addresses. A request signed for another server
/// and passed on here can't then be used.
/// </summary>
internal static class Audience
{
    private static readonly TimeSpan s_fresh = TimeSpan.FromSeconds(30);
    private static (long At, HashSet<string> Names) s_own = (0, []);

    /// <summary>Why <paramref name="uri"/>'s authority isn't in the audience, or null if it is.</summary>
    public static string? WhyNot(Uri uri, string? audience)
    {
        var host = Normal(uri.Host);
        var port = uri.Port;
        if (audience is not null)
        {
            foreach (var entry in audience.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (allowedHost, allowedPort) = HostAndPort(entry);
                if (Normal(allowedHost) == host && (allowedPort is null || allowedPort == port))
                    return null;
            }
            return $"Its signature is for {RequestSignature.AuthorityOf(uri)}, which isn't among the names this Verify answers to: {audience}.";
        }
        return OwnNames().Contains(host)
            ? null
            : $"Its signature is for {RequestSignature.AuthorityOf(uri)}, which isn't one of this machine's names. Give the names it may be addressed by with audience=.";
    }

    // An audience entry's host and port, if it has one: host, host:port, [v6] or [v6]:port.
    private static (string Host, int? Port) HostAndPort(string entry)
    {
        var colon = entry.LastIndexOf(':');
        var bracket = entry.LastIndexOf(']');
        if (colon > bracket && colon > 0 && int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && (bracket >= 0 || entry.IndexOf(':') == colon))
            return (entry[..colon], port);
        return (entry, null);
    }

    // This machine's names and addresses, read again every 30 seconds, as addresses come and go.
    private static HashSet<string> OwnNames()
    {
        var own = s_own;
        if (own.At != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(own.At) < s_fresh)
            return own.Names;
        var names = new HashSet<string>(StringComparer.Ordinal) { "localhost", Normal(Environment.MachineName), Normal(Environment.MachineName + ".local") };
        try
        {
            names.Add(Normal(Dns.GetHostName()));
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                    names.Add(Normal(address.Address.ToString()));
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            // The loopback names are always there; the rest are a convenience.
        }
        names.Add("127.0.0.1");
        names.Add("::1");
        s_own = (System.Diagnostics.Stopwatch.GetTimestamp(), names);
        return names;
    }

    // A host as compared: lower case, without IPv6's brackets or zone, or a trailing dot.
    private static string Normal(string host)
    {
        var name = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        var zone = name.IndexOf('%', StringComparison.Ordinal);
        name = zone >= 0 ? name[..zone] : name;
        return IPAddress.TryParse(name, out var address) ? address.ToString() : name;
    }
}

/// <summary>What happened when a nonce was used.</summary>
internal enum NonceUse
{
    /// <summary>It hadn't been seen, and is remembered now.</summary>
    Fresh,

    /// <summary>It had been seen within its window.</summary>
    Seen,

    /// <summary>Too many are remembered already to remember another.</summary>
    Full,
}

/// <summary>
/// The nonces of signed requests seen in this process, each remembered until its signature's window has passed, so a
/// request can't be replayed to any pipeline here.
/// </summary>
internal static class Nonces
{
    private const int Most = 1_000_000;
    private static readonly ConcurrentDictionary<string, DateTime> s_seen = new(StringComparer.Ordinal);
    private static int s_added;

    /// <summary>Uses a nonce, remembering it until <paramref name="until"/>.</summary>
    public static NonceUse Use(string nonce, DateTime until, DateTime now)
    {
        if (Interlocked.Increment(ref s_added) % 4096 == 0 || s_seen.Count >= Most)
            Forget(now);
        while (true)
        {
            if (s_seen.TryGetValue(nonce, out var remembered))
            {
                if (remembered > now)
                    return NonceUse.Seen;
                if (s_seen.TryUpdate(nonce, until, remembered))
                    return NonceUse.Fresh;
                continue;
            }
            if (s_seen.Count >= Most)
                return NonceUse.Full;
            if (s_seen.TryAdd(nonce, until))
                return NonceUse.Fresh;
        }
    }

    private static void Forget(DateTime now)
    {
        foreach (var (nonce, until) in s_seen)
        {
            if (until <= now)
                s_seen.TryRemove(new KeyValuePair<string, DateTime>(nonce, until));
        }
    }
}
