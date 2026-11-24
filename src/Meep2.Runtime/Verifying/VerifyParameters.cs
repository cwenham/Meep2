using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>Parameters for <c>Verify</c>'s inflections, which share them.</summary>
public sealed record VerifyParameters
{
    /// <summary>
    /// What to verify, read from the message, such as <c>msg.Headers["Meep-Credential"]</c>: a credential (an SSH
    /// certificate, as a <c>-cert.pub</c> file has it or its base64 alone), an X.509 certificate (PEM, or DER in base64),
    /// or a permission (its name, or a <c>#permission</c> line). Without it, the message itself is verified if it's a
    /// credential, certificate or permission, or else its text, so <c>Read "~/me.credential"</c> can feed Verify directly.
    /// </summary>
    public Computed<string>? From { get; init; }

    /// <summary>
    /// Whether a credential may be used without proof that whoever presented it holds its key, as one read from a file
    /// is. Its issuer must allow that too, with the <c>permit-bearer@meep</c> extension. For a certificate, whether it
    /// may be used without a TLS handshake proving it.
    /// </summary>
    public bool Bearer { get; init; }

    /// <summary>
    /// How far from now a signature over a request may have been made, either way, and how long its nonce is
    /// remembered so it can't be replayed.
    /// </summary>
    public TimeSpan Window
    {
        get;
        init => field = value > TimeSpan.Zero && value <= TimeSpan.FromHours(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Window), value, "The window must be longer than zero and no longer than an hour.");
    } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The names a signed request may be addressed to (its <c>@authority</c>), separated by commas or spaces, each a
    /// host or <c>host:port</c>, such as <c>"api.example.com, localhost:8080"</c>. Without it, this machine's own names
    /// and addresses, on any port.
    /// </summary>
    public string? Audience { get; init; }

    /// <summary>What a certificate must be allowed to do: <c>client</c> (the default), <c>server</c> or <c>any</c>.</summary>
    public CertificateUsage Usage { get; init; } = CertificateUsage.Client;

    /// <summary>
    /// What a permission is checked against: <c>auto</c> (the default) checks that it's well defined, and that it's
    /// met when the message has verified credentials among its ancestors; <c>definition</c> only that it's well
    /// defined; <c>credentials</c> always that it's met as well.
    /// </summary>
    public PermissionAgainst Against { get; init; }
}

/// <summary>What an X.509 certificate must be allowed to do, by its extended key usage, if it has one.</summary>
public enum CertificateUsage
{
    /// <summary>Identify a client, as in mutual TLS.</summary>
    Client,

    /// <summary>Identify a server.</summary>
    Server,

    /// <summary>Anything.</summary>
    Any,
}

/// <summary>What <c>Verify</c> checks a permission against (Q257).</summary>
public enum PermissionAgainst
{
    /// <summary>Its definition, and the credentials among the message's ancestors if there are any.</summary>
    Auto,

    /// <summary>Only its definition: whether it's well defined.</summary>
    Definition,

    /// <summary>Its definition and the credentials among the message's ancestors: whether it's met.</summary>
    Credentials,
}
