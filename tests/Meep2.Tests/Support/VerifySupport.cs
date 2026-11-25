using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using PublicKey = Meep2.Security.PublicKey;

namespace Meep2.Tests.Support;

/// <summary>
/// A trust store in a temporary home folder, with an SSH certificate authority from a <see cref="CredentialLab"/>
/// trusted to issue credentials and a key trusted to sign grants, for testing Verify.
/// </summary>
internal sealed class VerifyLab : IDisposable
{
    private readonly TemporaryFolder _home = new();

    private VerifyLab(CredentialLab credentials)
    {
        Credentials = credentials;
        Folder = TrustFolder.In(_home.Path);
        Folder.Create();
        Folder.AddAuthority("ca", PublicKey.Parse("ca", credentials.AuthorityPublicKey), [TrustStore.CredentialNamespace]);
        Folder.AddAuthority("granter", Granter.Public, [TrustStore.GrantNamespace]);
    }

    public CredentialLab Credentials { get; }

    public TrustFolder Folder { get; }

    /// <summary>A key trusted to sign grants, as <c>granter</c>.</summary>
    public SshKeyPair Granter { get; } = SshKeyPair.CreateEd25519("granter");

    /// <summary>A key whose credentials the lab issues, to sign requests with.</summary>
    public SshKeyPair Subject { get; } = SshKeyPair.CreateEd25519("alice");

    /// <summary>The home folder, for files the tests write.</summary>
    public string Home => _home.Path;

    public static async Task<VerifyLab> CreateAsync() => new(await CredentialLab.CreateAsync());

    /// <summary>The store as the folder has it now.</summary>
    public TrustStore Store() => TrustStore.Load(Folder);

    /// <summary>Writes the permission definitions.</summary>
    public void Define(string definitions) => File.WriteAllText(Folder.Permissions, definitions);

    /// <summary>A credential for <see cref="Subject"/>'s key, valid for an hour either side of now unless the options say.</summary>
    public Task<Credential> IssueAsync(params string[] options)
    {
        string[] dates = options.Contains("-V") ? [] : ["-V", "-1h:+1h"];
        return Credentials.IssueForAsync(Subject.Public.Text, [.. dates, "-I", "alice", "-n", "alice", .. options]);
    }

    /// <summary>A grant of <paramref name="permission"/> to <paramref name="credential"/>, signed by <paramref name="key"/> (the granter if none).</summary>
    public Grant Grant(Credential credential, string permission, SshKeyPair? key = null, DateTime? until = null) =>
        Sign(key ?? Granter, new GrantTerms(credential, PermissionName.Parse(permission)) { ValidBefore = until });

    public static Grant Sign(SshKeyPair key, GrantTerms terms) =>
        Meep2.SDK.Security.Grant.Signed(terms, key.Sign(terms.ToSignedData(), Meep2.SDK.Security.Grant.Namespace).ToBinary());

    /// <summary>
    /// Runs <paramref name="messages"/> through one Verify for each set of parameters, one after the other, with
    /// <paramref name="store"/>, and returns what the last emitted.
    /// </summary>
    public static async Task<IReadOnlyList<VerifiedMessage>> RunAsync(TrustStore? store, IReadOnlyList<VerifyParameters> stages, params Message[] messages)
    {
        var collect = new Collect<VerifiedMessage>();
        var builder = new PipelineBuilder("verify", new PipelineOptions { Trust = store is null ? null : TrustStoreSource.Of(store) });
        var chain = new List<Meep2.SDK.Modules.ModuleInfo> { builder.AddSource(() => new Messages(messages)) };
        foreach (var parameters in stages)
            chain.Add(builder.AddVerb("Verify", Inflections(parameters)));
        chain.Add(builder.Add(() => collect));
        builder.Chain([.. chain]);
        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());
        return collect.Received;
    }

    /// <summary>Runs messages through one Verify.</summary>
    public static async Task<VerifiedMessage> VerifyAsync(TrustStore? store, VerifyParameters parameters, Message message) =>
        (await RunAsync(store, [parameters], message)).Single();

    /// <summary>Verify's three inflections, chosen for each message as the loader would.</summary>
    public static IReadOnlyList<VerbInflection> Inflections(VerifyParameters parameters) =>
    [
        new("Credential", () => new VerifyCredential(parameters), m => VerifyCredential.WhyNot(parameters, m)),
        new("Certificate", () => new VerifyCertificate(parameters), m => VerifyCertificate.WhyNot(parameters, m)),
        new("Permission", () => new VerifyPermission(parameters), m => VerifyPermission.WhyNot(parameters, m)),
    ];

    public void Dispose()
    {
        Credentials.Dispose();
        _home.Dispose();
    }
}

/// <summary>Signs HTTP requests as RFC 9421 says, with an SSH signature in the meep-request namespace, as a client would.</summary>
internal sealed record RequestSigner(Func<byte[], string, byte[]> Sign, Credential Credential)
{
    public string Method { get; init; } = "POST";

    public string Url { get; init; } = "http://localhost:8080/notes";

    public string Body { get; init; } = "hello";

    /// <summary>When it was signed: now, unless set.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>The nonce: a new one each time the headers are made, unless set.</summary>
    public string? Nonce { get; init; }

    public string Namespace { get; init; } = "meep-request";

    /// <summary>What's covered: the method, address, authority and, with a body, its digest, unless set.</summary>
    public IReadOnlyList<string>? Components { get; init; }

    /// <summary>Other headers to send, which the signature covers only if <see cref="Components"/> names them.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    /// <summary>The headers a client would send: the credential, the body's digest and the signature.</summary>
    public Dictionary<string, string> Headers()
    {
        var body = Encoding.UTF8.GetBytes(Body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Meep-Credential"] = Convert.ToBase64String(Credential.Blob.Span),
        };
        foreach (var (name, value) in Extra ?? new Dictionary<string, string>())
            headers[name] = value;
        if (body.Length > 0)
            headers["Content-Digest"] = $"sha-256=:{Convert.ToBase64String(SHA256.HashData(body))}:";
        var components = Components ?? (body.Length > 0 ? ["@method", "@target-uri", "@authority", "content-digest"] : ["@method", "@target-uri", "@authority"]);
        var created = (Created ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var parameters = $"({string.Join(' ', components.Select(c => $"\"{c}\""))});created={created};nonce=\"{Nonce ?? Guid.NewGuid().ToString("N")}\";keyid=\"{Credential.KeyFingerprint}\"";
        var uri = new Uri(Url);
        var lines = components.Select(c => $"\"{c}\": " + c switch
        {
            "@method" => Method,
            "@target-uri" => Url,
            "@path" => uri.AbsolutePath,
            "@query" => uri.Query.Length == 0 ? "?" : uri.Query,
            "@authority" => (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").ToLowerInvariant(),
            _ => headers[c],
        });
        var signatureBase = string.Join("\n", lines) + $"\n\"@signature-params\": {parameters}";
        headers["Signature-Input"] = $"meep={parameters}";
        headers["Signature"] = $"meep=:{Convert.ToBase64String(Sign(Encoding.UTF8.GetBytes(signatureBase), Namespace))}:";
        return headers;
    }

    /// <summary>
    /// The request as Listen's HTTP inflection would emit it, with the headers and body changed as asked, and seen at
    /// <paramref name="url"/> if given, as behind a proxy.
    /// </summary>
    public HttpRequest Request(Action<Dictionary<string, string>>? change = null, string? body = null, string? method = null, string? url = null)
    {
        var headers = Headers();
        change?.Invoke(headers);
        var uri = new Uri(url ?? Url);
        var bytes = Encoding.UTF8.GetBytes(body ?? Body);
        return new HttpRequest
        {
            Method = method ?? Method,
            Url = url ?? Url,
            Path = uri.AbsolutePath,
            Query = new Dictionary<string, string>(),
            Headers = headers,
            Body = bytes,
            Text = body ?? Body,
        };
    }

    /// <summary>Signs with a key Meep holds.</summary>
    public static Func<byte[], string, byte[]> With(SshKeyPair key) => (data, ns) => key.Sign(data, ns).ToBinary();
}

/// <summary>
/// A security key, as a YubiKey has one (<c>sk-ssh-ed25519@openssh.com</c>), made in memory: it signs as the device
/// would, over a hash of its application, its flags and a counter.
/// </summary>
internal sealed class TestSecurityKey
{
    private readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());
    private uint _counter;

    public TestSecurityKey()
    {
        Public = PublicKey.FromBlob("yubikey", new SshWireWriter().Text("sk-ssh-ed25519@openssh.com").String(_key.GeneratePublicKey().GetEncoded()).Text("ssh:").ToArray());
    }

    public PublicKey Public { get; }

    /// <summary>The flags it signs with: user presence (touched) unless set.</summary>
    public byte Flags { get; set; } = 0x01;

    /// <summary>Signs data as a security key does, in SSH's wire format.</summary>
    public byte[] SignRaw(byte[] data)
    {
        var counter = ++_counter;
        var covered = new SshWireWriter().Raw(SHA256.HashData("ssh:"u8)).Raw([Flags]).UInt32(counter).Raw(SHA256.HashData(data)).ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(covered, 0, covered.Length);
        return new SshWireWriter().Text("sk-ssh-ed25519@openssh.com").String(signer.GenerateSignature()).Raw([Flags]).UInt32(counter).ToArray();
    }

    /// <summary>An SSH signature of a message in a namespace, binary, as <c>ssh-keygen -Y sign</c> makes with the device.</summary>
    public byte[] Sign(byte[] message, string ns) =>
        new SshSignature(Public, ns, "sha512", SignRaw(SshSignature.SignedData(ns, "sha512", message))).ToBinary();
}

/// <summary>A certificate authority for tests: a root, or an intermediate under one, issuing certificates and CRLs.</summary>
internal sealed class TestCa : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private TestCa(string subject, TestCa? parent)
    {
        var request = new CertificateRequest(subject, _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Certificate = parent is null
            ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddYears(1))
            : request.Create(parent.Certificate, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(8)).CopyWithPrivateKey(_key);
    }

    /// <summary>Its own certificate, with its private key.</summary>
    public X509Certificate2 Certificate { get; }

    public static TestCa Root(string subject = "CN=Test Root") => new(subject, null);

    public TestCa Intermediate(string subject) => new(subject, this);

    /// <summary>A leaf it issues, for a client unless the usages say otherwise, valid from yesterday for a month unless the dates say.</summary>
    public X509Certificate2 Issue(string subject, string[]? usages = null, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var oids = new OidCollection();
        foreach (var usage in usages ?? ["1.3.6.1.5.5.7.3.2"])
            oids.Add(new Oid(usage));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        return request.Create(Certificate, notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>A CRL revoking these certificates, due to be replaced at <paramref name="nextUpdate"/>, as DER.</summary>
    public byte[] Crl(DateTimeOffset nextUpdate, params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var certificate in revoked)
            builder.AddEntry(certificate);
        return builder.Build(Certificate, 1, nextUpdate, HashAlgorithmName.SHA256, thisUpdate: nextUpdate.AddDays(-7));
    }

    public void Dispose()
    {
        Certificate.Dispose();
        _key.Dispose();
    }
}
