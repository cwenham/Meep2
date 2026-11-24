using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Shared;
using Meep2.Tests.Support;
using Code = Meep2.SDK.Security.VerificationFailureCode;

namespace Meep2.Tests;

public class VerifyTests
{
    private static readonly VerifyParameters s_bearer = new() { Bearer = true };
    private static readonly VerifyParameters s_fromHeader = new() { From = Computed<string>.From(m => ((HttpRequest)m).Headers["Meep-Credential"]) };

    private static Code[] Codes(VerifiedMessage verified) => [.. verified.Failures.Select(f => f.Code)];

    // ---- Credentials ----

    [Test]
    public async Task A_bearer_credential_read_from_a_file_passes_with_its_permissions_and_grants_and_lapses_with_them()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var credential = await lab.IssueAsync("-V", "-1h:+2h", "-O", "extension:permit-bearer@meep", "-O", "extension:ReadNotes@meep", "-O", "extension:meep.files.write@meep=~/notes/**");
        lab.Folder.RecordGrant(lab.Grant(credential, "WriteNotes", until: DateTime.UtcNow.AddHours(1)));
        var file = Path.Combine(lab.Home, "alice.credential");
        File.WriteAllText(file, credential.ToText() + " alice@laptop\n");
        var store = lab.Store();

        var run = await TestRun.Start($"""
            Read "{file}"
                Verify bearer=true
                    Sink
            """, trust: TrustStoreSource.Of(store));

        await Assert.That(run.Diagnostics).IsEmpty();
        var verified = (VerifiedMessage)run.At("Sink").Single();
        await Assert.That(verified.Success).IsTrue().Because(verified.ToText());
        await Assert.That(verified.Kind).IsEqualTo(VerificationKind.Credential);
        await Assert.That(verified.IsGenuine).IsTrue();
        await Assert.That(verified.Proof).IsEqualTo(Proof.Bearer);
        await Assert.That(verified.Permissions.Select(p => p.ToString())).IsEquivalentTo(["ReadNotes", "meep.files.write:~/notes/**", "WriteNotes"]);
        await Assert.That(verified.Grants.Count).IsEqualTo(1);
        await Assert.That(verified.Subject).IsTypeOf<Credential>();
        await Assert.That(verified.Parent).IsTypeOf<FileText>();
        await Assert.That(verified.TrustStoreVersion).IsEqualTo(store.Version);
        await Assert.That(verified.RevocationVersion).IsEqualTo(store.RevocationVersion);
        // It lapses with the grant, an hour from now, before the credential's two.
        var lapses = verified.Expires!.Value.ToUniversalTime();
        await Assert.That(Math.Abs((lapses - DateTime.UtcNow.AddHours(1)).TotalMinutes)).IsLessThan(2);
    }

    [Test]
    public async Task A_credential_without_proof_fails_unless_both_the_pipeline_and_its_issuer_allow_a_bearer()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var permitted = await lab.IssueAsync("-O", "extension:permit-bearer@meep");
        var plain = await lab.IssueAsync();
        var store = lab.Store();

        var notAllowedHere = await VerifyLab.VerifyAsync(store, new VerifyParameters(), permitted);
        var notAllowedByIssuer = await VerifyLab.VerifyAsync(store, s_bearer, plain);
        var allowed = await VerifyLab.VerifyAsync(store, s_bearer, await lab.IssueAsync("-O", "extension:permit-bearer@meep"));

        await Assert.That(Codes(notAllowedHere)).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(notAllowedHere.Failures[0].Reason).Contains("bearer=true");
        await Assert.That(Codes(notAllowedByIssuer)).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(notAllowedByIssuer.Failures[0].Reason).Contains("permit-bearer@meep");
        await Assert.That(notAllowedByIssuer.Permissions).IsEmpty();
        await Assert.That(notAllowedByIssuer.Expires).IsNull().Because("a failure doesn't lapse early, so it can still be answered");
        await Assert.That(allowed.Success).IsTrue();
    }

    [Test]
    public async Task A_credential_fails_for_each_thing_wrong_with_it()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var stranger = await CredentialLab.CreateAsync();
        const string bearer = "extension:permit-bearer@meep";
        var revoked = await lab.IssueAsync("-O", bearer, "-z", "9");
        lab.Folder.Revoke(Revocation.Of(revoked), "alice's");
        var good = await lab.IssueAsync("-O", bearer);
        var tampered = good.Blob.ToArray();
        tampered[^1] ^= 1; // the signature's last byte
        var store = lab.Store();

        async Task<Code[]> Check(Credential credential) => Codes(await VerifyLab.VerifyAsync(store, s_bearer, credential));

        await Assert.That(await Check(await stranger.IssueForAsync(lab.Subject.Public.Text, "-I", "x", "-O", bearer))).IsEquivalentTo([Code.UnknownAuthority]);
        await Assert.That(await Check(Credential.FromBlob(tampered))).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await Check(await lab.IssueAsync("-O", bearer, "-V", "-2d:-1d"))).IsEquivalentTo([Code.Expired]);
        await Assert.That(await Check(await lab.IssueAsync("-O", bearer, "-V", "+1d:+2d"))).IsEquivalentTo([Code.NotYetValid]);
        await Assert.That(await Check(revoked)).IsEquivalentTo([Code.Revoked]);
        await Assert.That(await Check(await lab.IssueAsync("-h", "-O", bearer))).Contains(Code.WrongUsage);
        await Assert.That(await Check(await lab.IssueAsync("-O", bearer, "-O", "force-command=/bin/true"))).IsEquivalentTo([Code.UnknownCriticalOption]);
        await Assert.That(await Check(good)).IsEmpty();
    }

    [Test]
    public async Task Grants_count_when_trusted_in_date_and_not_revoked_whether_recorded_or_carried_and_the_rest_are_noted()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var untrusted = SshKeyPair.CreateEd25519("untrusted");
        var credential = await lab.IssueAsync("-O", "extension:permit-bearer@meep");
        var revokedGrant = lab.Grant(credential, "Revoked");
        lab.Folder.RecordGrant(lab.Grant(credential, "Recorded"));
        lab.Folder.RecordGrant(lab.Grant(credential, "Untrusted", untrusted));
        lab.Folder.RecordGrant(lab.Grant(credential, "Lapsed", until: DateTime.UtcNow.AddMinutes(-1)));
        lab.Folder.RecordGrant(revokedGrant);
        lab.Folder.Revoke(new Revocation(RevocationKind.Grant, revokedGrant.Terms.Id.ToString()), "a grant");
        var store = lab.Store();
        var carried = lab.Grant(credential, "Carried");

        // The grant arrives first, and the credential is made from it, so the grant is among its ancestors.
        var collect = new Collect<VerifiedMessage>();
        var builder = new PipelineBuilder("grants", new PipelineOptions { Trust = TrustStoreSource.Of(store) });
        builder.Chain(
            builder.AddSource(() => new Messages(carried)),
            builder.Add(() => new Map<Grant>((_, context) => context.Emit(Credential.FromBlob(credential.Blob.Span)))),
            builder.AddVerb("Verify", VerifyLab.Inflections(s_bearer)),
            builder.Add(() => collect));
        await using (var pipeline = builder.Build())
            await Wait.For(pipeline.RunAsync());

        var verified = collect.Received.Single();
        await Assert.That(verified.Success).IsTrue();
        await Assert.That(verified.Permissions.Select(p => p.Name)).IsEquivalentTo(["Recorded", "Carried"]);
        // Each grant left out is noted, with why (Q283).
        await Assert.That(verified.Notes.Count).IsEqualTo(3);
        await Assert.That(verified.Notes.Any(note => note.Contains("doesn't trust to sign grants", StringComparison.Ordinal) && note.EndsWith("So it doesn't give Untrusted.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(verified.Notes.Any(note => note.Contains("expired at", StringComparison.Ordinal) && note.EndsWith("So it doesn't give Lapsed.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(verified.Notes.Any(note => note.Contains("is revoked", StringComparison.Ordinal) && note.EndsWith("So it doesn't give Revoked.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(CredentialCheck.WhyNotGood(revokedGrant, store, DateTime.UtcNow)).Contains("is revoked");
        await Assert.That(CredentialCheck.WhyNotGood(lab.Grant(credential, "X", untrusted), store, DateTime.UtcNow)).Contains("doesn't trust to sign grants");
    }

    [Test]
    public async Task What_cant_be_read_and_a_pipeline_with_no_trust_store_fail_as_results_not_errors()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var store = lab.Store();
        var noHeader = new HttpRequest { Method = "GET", Url = "http://localhost/", Path = "/", Query = new Dictionary<string, string>(), Headers = new Dictionary<string, string>() };

        var missing = await VerifyLab.VerifyAsync(store, s_fromHeader, noHeader);
        var nonsense = await VerifyLab.VerifyAsync(store, s_bearer, new FileText("x.credential", "ssh-ed25519-cert-v01@openssh.com AAAA"));
        var unconfigured = await VerifyLab.VerifyAsync(null, s_bearer, await lab.IssueAsync("-O", "extension:permit-bearer@meep"));

        await Assert.That(Codes(missing)).IsEquivalentTo([Code.Malformed]);
        await Assert.That(missing.Failures[0].Reason).StartsWith("There's nothing to verify: from= can't be worked out");
        await Assert.That(missing.Subject).IsSameReferenceAs(missing.Parent);
        await Assert.That(Codes(nonsense)).IsEquivalentTo([Code.Malformed]);
        await Assert.That(Codes(unconfigured)).IsEquivalentTo([Code.UnknownAuthority]);
        await Assert.That(unconfigured.Failures[0].Reason).Contains("no trust store");
    }

    // ---- Proof of possession: request signatures ----

    [Test]
    public async Task A_signed_request_proves_its_credential_once()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var credential = await lab.IssueAsync("-O", "extension:ReadNotes@meep");
        var signer = new RequestSigner(RequestSigner.With(lab.Subject), credential) { Nonce = Guid.NewGuid().ToString("N") };
        var request = signer.Request();
        var store = lab.Store();

        var verified = await VerifyLab.VerifyAsync(store, s_fromHeader, request);
        var replayed = await VerifyLab.VerifyAsync(store, s_fromHeader, signer.Request());
        var bodiless = await VerifyLab.VerifyAsync(store, s_fromHeader, (signer with { Method = "GET", Body = "", Nonce = "another" }).Request());

        await Assert.That(verified.Success).IsTrue().Because(verified.ToText());
        await Assert.That(verified.Proof).IsEqualTo(Proof.RequestSignature);
        await Assert.That(verified.Permissions.Select(p => p.Name)).IsEquivalentTo(["ReadNotes"]);
        await Assert.That(verified.Parent).IsSameReferenceAs(request);
        await Assert.That(Codes(replayed)).IsEquivalentTo([Code.Replayed]);
        await Assert.That(bodiless.Success).IsTrue().Because(bodiless.ToText());
    }

    [Test]
    public async Task A_request_signed_by_ssh_keygen_is_proven()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var credential = await lab.Credentials.IssueAsync("-I", "subject", "-V", "-1h:+1h");
        var folder = lab.Credentials.Folder;
        var signer = new RequestSigner((data, ns) =>
        {
            var file = Path.Combine(folder, $"base-{Guid.NewGuid():N}");
            File.WriteAllBytes(file, data);
            var (code, said) = SshKeygen.Run(["-Y", "sign", "-n", ns, "-f", lab.Credentials.SubjectPrivateKeyFile, file]).GetAwaiter().GetResult();
            return code == 0 ? SshSignature.Parse(File.ReadAllText(file + ".sig")).ToBinary() : throw new InvalidOperationException(said);
        }, credential);

        var verified = await VerifyLab.VerifyAsync(lab.Store(), s_fromHeader, signer.Request());

        await Assert.That(verified.Success).IsTrue().Because(verified.ToText());
        await Assert.That(verified.Proof).IsEqualTo(Proof.RequestSignature);
    }

    [Test]
    public async Task A_signed_request_fails_for_each_thing_wrong_with_its_signature()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var other = SshKeyPair.CreateEd25519("mallory");
        var credential = await lab.IssueAsync();
        var signer = new RequestSigner(RequestSigner.With(lab.Subject), credential);
        var store = lab.Store();

        async Task<VerifiedMessage> Check(HttpRequest request, VerifyParameters? parameters = null) =>
            await VerifyLab.VerifyAsync(store, parameters ?? s_fromHeader, request);
        async Task<Code[]> CodesOf(HttpRequest request, VerifyParameters? parameters = null) => Codes(await Check(request, parameters));

        await Assert.That(await CodesOf((signer with { Created = DateTimeOffset.UtcNow.AddMinutes(-5) }).Request())).IsEquivalentTo([Code.StaleSignature]);
        await Assert.That(await CodesOf((signer with { Created = DateTimeOffset.UtcNow.AddMinutes(5) }).Request())).IsEquivalentTo([Code.StaleSignature]);
        await Assert.That(await CodesOf((signer with { Created = DateTimeOffset.UtcNow.AddMinutes(-5) }).Request(), s_fromHeader with { Window = TimeSpan.FromMinutes(10) })).IsEmpty();
        await Assert.That(await CodesOf((signer with { Url = "https://example.com/notes" }).Request())).IsEquivalentTo([Code.WrongAudience]);
        await Assert.That(await CodesOf((signer with { Url = "https://example.com/notes" }).Request(), s_fromHeader with { Audience = "example.com, localhost:1" })).IsEmpty();
        await Assert.That(await CodesOf((signer with { Url = "http://localhost:8080/notes" }).Request(), s_fromHeader with { Audience = "localhost:1" })).IsEquivalentTo([Code.WrongAudience]);
        await Assert.That(await CodesOf(signer.Request(body: "changed"))).IsEquivalentTo([Code.BodyMismatch]);
        await Assert.That(await CodesOf(signer.Request(method: "DELETE"))).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await CodesOf((signer with { Namespace = "meep-grant" }).Request())).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await CodesOf((signer with { Sign = RequestSigner.With(other) }).Request())).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(await CodesOf((signer with { Components = ["@method", "@target-uri", "@authority"] }).Request())).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(await CodesOf((signer with { Components = ["@method", "@target-uri", "content-digest"] }).Request())).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(await CodesOf(signer.Request(headers => headers.Remove("Signature")))).IsEquivalentTo([Code.NoProofOfPossession]);
        // Behind a proxy that ends TLS, Meep sees http:// where the client signed https://: @path and @query stand in for
        // @target-uri (Q285), but one of them alone doesn't.
        var proxied = signer with { Url = "https://localhost/notes?day=1" };
        const string seen = "http://localhost/notes?day=1";
        await Assert.That(await CodesOf(proxied.Request(url: seen))).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await CodesOf((proxied with { Components = ["@method", "@authority", "@path", "@query", "content-digest"] }).Request(url: seen))).IsEmpty();
        await Assert.That(await CodesOf((proxied with { Components = ["@method", "@authority", "@path", "content-digest"] }).Request(url: seen))).IsEquivalentTo([Code.NoProofOfPossession]);
        await Assert.That(await CodesOf((proxied with { Components = ["@method", "@authority", "@path", "@query", "content-digest"] }).Request(url: "http://localhost/notes?day=2"))).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await CodesOf(signer.Request(headers => headers["Signature-Input"] = headers["Signature-Input"].Replace(";nonce=", ";x=", StringComparison.Ordinal)))).IsEquivalentTo([Code.NoProofOfPossession]);
        // A signature that's bad doesn't use up its nonce.
        var nonce = Guid.NewGuid().ToString("N");
        await Assert.That(await CodesOf((signer with { Nonce = nonce }).Request(method: "PUT"))).IsEquivalentTo([Code.BadSignature]);
        await Assert.That(await CodesOf((signer with { Nonce = nonce }).Request())).IsEmpty();
    }

    [Test]
    public async Task A_credential_on_a_security_key_proves_a_request_only_when_touched()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var yubikey = new TestSecurityKey();
        var credential = await lab.Credentials.IssueForAsync(yubikey.Public.Text, "-I", "alice", "-V", "-1h:+1h");
        var signer = new RequestSigner(yubikey.Sign, credential);
        var store = lab.Store();

        var touched = await VerifyLab.VerifyAsync(store, s_fromHeader, signer.Request());
        yubikey.Flags = 0;
        var untouched = await VerifyLab.VerifyAsync(store, s_fromHeader, signer.Request());

        await Assert.That(credential.KeyType).IsEqualTo("sk-ssh-ed25519@openssh.com");
        await Assert.That(touched.Success).IsTrue().Because(touched.ToText());
        await Assert.That(Codes(untouched)).IsEquivalentTo([Code.BadSignature]);
    }

    [Test]
    public async Task A_request_signed_with_its_credential_passes_through_a_real_Listen()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var credential = await lab.IssueAsync();
        var port = TestServer.FreePort();
        var url = $"http://127.0.0.1:{port}/notes/";
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/notes/"
                Verify from=msg.Headers["Meep-Credential"]
                    Send text="{msg.Success} {msg.Proof}"
            """, TrustStoreSource.Of(lab.Store()));
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();

        // Posts "hello" with these headers, trying again while Listen is still starting.
        async Task<string> PostAsync(Dictionary<string, string> headers)
        {
            var deadline = DateTime.UtcNow + Wait.Limit;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
                foreach (var (name, value) in headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                }
                try
                {
                    using var response = await client.SendAsync(request);
                    return await response.Content.ReadAsStringAsync();
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
            }
        }

        var signer = new RequestSigner(RequestSigner.With(lab.Subject), credential) { Url = url };
        await Assert.That(await PostAsync(signer.Headers())).IsEqualTo("True RequestSignature");
        await Assert.That(await PostAsync(new() { ["Meep-Credential"] = Convert.ToBase64String(credential.Blob.Span) })).IsEqualTo("False None");
    }

    // ---- Certificates ----

    [Test]
    public async Task A_certificate_chained_to_a_root_passes_with_bearer_true_or_a_TLS_handshake()
    {
        using var lab = new TemporaryFolder();
        var folder = TrustFolder.In(lab.Path);
        folder.Create();
        using var root = TestCa.Root();
        using var intermediate = root.Intermediate("CN=Test Intermediate");
        using var leaf = intermediate.Issue("CN=alice", notAfter: DateTimeOffset.UtcNow.AddDays(10));
        folder.AddRoot("root", root.Certificate.RawData);
        var store = TrustStore.Load(folder);
        Certificate Presented() => Certificate.FromDer(leaf.RawData, [intermediate.Certificate.RawData]);

        var bearer = await VerifyLab.VerifyAsync(store, s_bearer, Presented());
        var tls = await VerifyLab.VerifyAsync(store, new VerifyParameters(), Presented().MarkProvenByTls());
        var unproven = await VerifyLab.VerifyAsync(store, new VerifyParameters(), Presented());

        await Assert.That(bearer.Success).IsTrue().Because(bearer.ToText());
        await Assert.That(bearer.Kind).IsEqualTo(VerificationKind.Certificate);
        await Assert.That(bearer.Proof).IsEqualTo(Proof.Bearer);
        await Assert.That(bearer.Permissions).IsEmpty();
        await Assert.That(Math.Abs((bearer.Expires!.Value.ToUniversalTime() - leaf.NotAfter.ToUniversalTime()).TotalMinutes)).IsLessThan(2);
        await Assert.That(tls.Proof).IsEqualTo(Proof.Tls);
        await Assert.That(Codes(unproven)).IsEquivalentTo([Code.NoProofOfPossession]);
    }

    [Test]
    public async Task Any_certificate_in_roots_is_an_anchor_even_an_intermediate_without_its_parent()
    {
        using var lab = new TemporaryFolder();
        var folder = TrustFolder.In(lab.Path);
        folder.Create();
        using var root = TestCa.Root("CN=Parent Root");
        using var department = root.Intermediate("CN=Department CA");
        using var leaf = department.Issue("CN=bob");
        folder.AddRoot("department", department.Certificate.RawData);
        var store = TrustStore.Load(folder);

        var alone = await VerifyLab.VerifyAsync(store, s_bearer, Certificate.FromDer(leaf.RawData));
        var withParent = await VerifyLab.VerifyAsync(store, s_bearer, Certificate.FromDer(leaf.RawData, [department.Certificate.RawData, root.Certificate.RawData]));

        await Assert.That(alone.Success).IsTrue().Because(alone.ToText());
        await Assert.That(withParent.Success).IsTrue().Because(withParent.ToText());
    }

    [Test]
    public async Task A_certificate_fails_for_each_thing_wrong_with_its_chain()
    {
        using var lab = new TemporaryFolder();
        var folder = TrustFolder.In(lab.Path);
        folder.Create();
        using var root = TestCa.Root();
        using var intermediate = root.Intermediate("CN=Test Intermediate");
        using var stranger = TestCa.Root("CN=Stranger Root");
        folder.AddRoot("root", root.Certificate.RawData);
        using var revokedByCrl = root.Issue("CN=revoked");
        using var revokedByHash = root.Issue("CN=hashed");
        folder.AddRevocationList("root.crl", root.Crl(DateTimeOffset.UtcNow.AddDays(7), revokedByCrl));
        folder.Revoke(new Revocation(RevocationKind.Certificate, revokedByHash.GetCertHashString(HashAlgorithmName.SHA256)), "hashed");
        var store = TrustStore.Load(folder);

        async Task<Code[]> Check(X509Certificate2 certificate, IEnumerable<X509Certificate2>? sent = null, CertificateUsage usage = CertificateUsage.Client) =>
            Codes(await VerifyLab.VerifyAsync(store, s_bearer with { Usage = usage }, Certificate.FromDer(certificate.RawData, (sent ?? []).Select(c => (ReadOnlyMemory<byte>)c.RawData))));

        using var unknown = stranger.Issue("CN=unknown");
        using var orphan = intermediate.Issue("CN=orphan");
        using var expired = root.Issue("CN=expired", notBefore: DateTimeOffset.UtcNow.AddDays(-10), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        using var early = root.Issue("CN=early", notBefore: DateTimeOffset.UtcNow.AddDays(1), notAfter: DateTimeOffset.UtcNow.AddDays(10));
        using var server = root.Issue("CN=server", ["1.3.6.1.5.5.7.3.1"]);

        await Assert.That(await Check(unknown)).IsEquivalentTo([Code.ChainIncomplete]);
        await Assert.That(await Check(unknown, [stranger.Certificate])).IsEquivalentTo([Code.UntrustedRoot]);
        await Assert.That(await Check(orphan)).IsEquivalentTo([Code.ChainIncomplete]);
        await Assert.That(await Check(orphan, [intermediate.Certificate])).IsEmpty();
        await Assert.That(await Check(expired)).IsEquivalentTo([Code.Expired]);
        await Assert.That(await Check(early)).IsEquivalentTo([Code.NotYetValid]);
        await Assert.That(await Check(server)).IsEquivalentTo([Code.WrongUsage]);
        await Assert.That(await Check(server, usage: CertificateUsage.Server)).IsEmpty();
        await Assert.That(await Check(server, usage: CertificateUsage.Any)).IsEmpty();
        await Assert.That(await Check(revokedByCrl)).IsEquivalentTo([Code.Revoked]);
        await Assert.That(await Check(revokedByHash)).IsEquivalentTo([Code.Revoked]);
    }

    [Test]
    public async Task A_certificate_from_an_issuer_whose_CRL_is_out_of_date_fails_as_revocation_unknown()
    {
        using var lab = new TemporaryFolder();
        var folder = TrustFolder.In(lab.Path);
        folder.Create();
        using var root = TestCa.Root();
        using var leaf = root.Issue("CN=alice");
        folder.AddRoot("root", root.Certificate.RawData);
        folder.AddRevocationList("old.crl", root.Crl(DateTimeOffset.UtcNow.AddHours(-1)));

        var stale = await VerifyLab.VerifyAsync(TrustStore.Load(folder), s_bearer, Certificate.FromDer(leaf.RawData));
        File.Delete(Path.Combine(folder.RevocationLists, "old.crl"));
        folder.AddRevocationList("new.crl", root.Crl(DateTimeOffset.UtcNow.AddHours(3)));
        var fresh = await VerifyLab.VerifyAsync(TrustStore.Load(folder), s_bearer, Certificate.FromDer(leaf.RawData));

        await Assert.That(Codes(stale)).IsEquivalentTo([Code.RevocationUnknown]);
        await Assert.That(fresh.Success).IsTrue().Because(fresh.ToText());
        // It lapses when the CRL is due, before the certificate does.
        await Assert.That(Math.Abs((fresh.Expires!.Value.ToUniversalTime() - DateTime.UtcNow.AddHours(3)).TotalMinutes)).IsLessThan(2);
    }

    // ---- Permissions ----

    [Test]
    public async Task A_permission_is_checked_against_its_definition_alone_when_there_are_no_credentials()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        File.WriteAllText(folder.Permissions, "#permission ReadNotes:~/notes/**\n#permission WriteNotes requires=ReadNotes\n");
        var store = TrustStore.Load(folder);
        var parameters = new VerifyParameters();

        async Task<VerifiedMessage> Check(Message message) => await VerifyLab.VerifyAsync(store, parameters, message);

        var defined = await Check(new Permission(PermissionName.Parse("ReadNotes:~/notes/work/**")));
        var unknown = await Check(new Permission(PermissionName.Parse("Nope")));
        var wider = await Check(new Permission(PermissionName.Parse("ReadNotes")));
        var dropped = await Check(new FileText("p", "#permission WriteNotes"));
        var narrowed = await Check(new FileText("p", "#permission WriteNotes requires=ReadNotes:~/notes/**"));
        var demanded = await VerifyLab.VerifyAsync(store, parameters with { Against = PermissionAgainst.Credentials }, new Permission(PermissionName.Parse("ReadNotes:~/notes/**")));

        await Assert.That(defined.Success).IsTrue().Because(defined.ToText());
        await Assert.That(defined.Kind).IsEqualTo(VerificationKind.Permission);
        await Assert.That(defined.Permissions).IsEmpty().Because("only a permission met is established");
        await Assert.That(Codes(unknown)).IsEquivalentTo([Code.UnknownPermission]);
        await Assert.That(Codes(wider)).IsEquivalentTo([Code.WiderThanDefinition]);
        await Assert.That(Codes(dropped)).IsEmpty().Because("a directive adds its requirements to the definition's");
        await Assert.That(Codes(narrowed)).IsEmpty();
        await Assert.That(Codes(demanded)).IsEquivalentTo([Code.MissingRequirement]);
    }

    [Test]
    public async Task A_permission_is_met_by_the_credentials_verified_before_it()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        lab.Define("""
            #permission ReadNotes authority=ca
            #permission WriteNotes requires=ReadNotes
            #permission Granted authority=ca
            #permission Admin
            """);
        var credential = await lab.IssueAsync("-O", "extension:permit-bearer@meep", "-O", "extension:ReadNotes@meep", "-O", "extension:WriteNotes@meep");
        lab.Folder.RecordGrant(lab.Grant(credential, "Granted"));
        var store = lab.Store();

        async Task<VerifiedMessage> Check(string permission, VerifyParameters? then = null) =>
            (await VerifyLab.RunAsync(store, [s_bearer, then ?? new VerifyParameters { From = Computed<string>.Constant(permission) }], Credential.FromBlob(credential.Blob.Span))).Single();

        var read = await Check("ReadNotes");
        var write = await Check("WriteNotes");
        var admin = await Check("Admin");
        var granted = await Check("Granted");
        var definitionOnly = await Check("Admin", new VerifyParameters { From = Computed<string>.Constant("Admin"), Against = PermissionAgainst.Definition });

        await Assert.That(read.Success).IsTrue().Because(read.ToText());
        await Assert.That(read.Permissions.Select(p => p.ToString())).IsEquivalentTo(["ReadNotes"]);
        await Assert.That(write.Success).IsTrue().Because(write.ToText());
        await Assert.That(Codes(admin)).IsEquivalentTo([Code.MissingRequirement]);
        await Assert.That(admin.Failures[0].Reason).Contains("alice's credential doesn't hold Admin");
        await Assert.That(Codes(granted)).IsEquivalentTo([Code.MissingRequirement]).Because("granter, not ca, conferred it");
        await Assert.That(granted.Failures[0].Reason).Contains("conferred by granter, but only ca may confer it");
        await Assert.That(definitionOnly.Success).IsTrue();
        await Assert.That(definitionOnly.Permissions).IsEmpty();
    }

    [Test]
    public async Task Every_line_of_a_family_must_carry_the_permission_and_a_revocation_since_is_seen()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        lab.Define("#permission ReadNotes\n");
        var credential = await lab.IssueAsync("-O", "extension:ReadNotes@meep", "-z", "5");
        var store = lab.Store();
        var verified = new VerifiedMessage(credential)
        {
            Kind = VerificationKind.Credential,
            Permissions = credential.Permissions,
            TrustStoreVersion = store.Version,
            RevocationVersion = store.RevocationVersion,
        };
        var permission = new Permission(PermissionName.Parse("ReadNotes"));
        var anonymous = new Word("anonymous");
        var both = new Word("zipped") { Parents = new Parents([new Word("a") { Parents = new Parents(verified) }, new Word("b") { Parents = new Parents(verified) }]) };
        var mixed = new Word("zipped") { Parents = new Parents([new Word("a") { Parents = new Parents(verified) }, anonymous]) };
        var copied = verified with { Parents = Parents.None };

        await Assert.That(new PermissionLines(store, DateTime.UtcNow).WhyNotMet(both, permission)).IsNull();
        await Assert.That(new PermissionLines(store, DateTime.UtcNow).WhyNotMet(mixed, permission)).Contains("Nothing on one of the lines");
        await Assert.That(new PermissionLines(store, DateTime.UtcNow).WhyNotMet(new Word("c") { Parents = new Parents(copied) }, permission)).IsNotNull().Because("a copy isn't genuine, and this one isn't made from the original");

        lab.Folder.Revoke(Revocation.Of(credential), "alice's");
        var later = store.Reload();
        await Assert.That(later.RevocationVersion).IsGreaterThan(store.RevocationVersion);
        await Assert.That(new PermissionLines(later, DateTime.UtcNow).WhyNotMet(both, permission)).Contains("is revoked");
    }

    // ---- Choosing an inflection ----

    [Test]
    public async Task Verify_chooses_its_inflection_by_what_it_is_given()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var root = TestCa.Root();
        using var leaf = root.Issue("CN=alice");
        lab.Folder.AddRoot("root", root.Certificate.RawData);
        lab.Define("#permission ReadNotes\n");
        var credential = await lab.IssueAsync("-O", "extension:permit-bearer@meep");
        var credentialFile = Path.Combine(lab.Home, "alice.credential");
        var certificateFile = Path.Combine(lab.Home, "alice.pem");
        var permissionFile = Path.Combine(lab.Home, "permission.txt");
        File.WriteAllText(credentialFile, credential.ToText());
        File.WriteAllText(certificateFile, PemEncoding.WriteString("CERTIFICATE", leaf.RawData));
        File.WriteAllText(permissionFile, "ReadNotes\n");

        var run = await TestRun.Start($"""
            Read "{credentialFile}"
                Verify bearer=true
                    Sink
            Read "{certificateFile}"
                Verify bearer=true
                    Sink
            Read "{permissionFile}"
                Verify
                    Sink
            """, trust: TrustStoreSource.Of(lab.Store()));

        await Assert.That(run.Diagnostics).IsEmpty();
        var verified = run.At("Sink").Cast<VerifiedMessage>().ToList();
        await Assert.That(verified.Select(v => v.Kind)).IsEquivalentTo([VerificationKind.Credential, VerificationKind.Certificate, VerificationKind.Permission]);
        await Assert.That(verified.All(v => v.Success)).IsTrue().Because(string.Join("; ", verified.Select(v => v.ToText())));
        await Assert.That(verified.Select(v => v.EmittedBy!.Inflection ?? "")).IsEquivalentTo(["Credential", "Certificate", "Permission"]);
    }

    [Test]
    [Arguments("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", VerificationKind.Certificate)]
    [Arguments("MIIBkTCCATeg", VerificationKind.Certificate)]
    [Arguments("#permission ReadNotes requires=X", VerificationKind.Permission)]
    [Arguments("meep.files.write:~/notes/**", VerificationKind.Permission)]
    [Arguments("ReadNotes", VerificationKind.Permission)]
    [Arguments("ssh-ed25519-cert-v01@openssh.com AAAA alice", VerificationKind.Credential)]
    [Arguments("AAAAIHNzaC1lZDI1NTE5LWNlcnQtdjAxQG9wZW5zc2guY29t", VerificationKind.Credential)]
    [Arguments("", VerificationKind.Credential)]
    [Arguments("not anything at all", VerificationKind.Credential)]
    public async Task What_text_is_decides_which_inflection_reads_it(string text, VerificationKind kind) =>
        await Assert.That(VerifySubject.KindOfText(text)).IsEqualTo(kind);

    // ---- Pieces ----

    [Test]
    public async Task Signature_parameters_are_read_and_written_back_as_RFC_9421_signs_them()
    {
        const string input = "sig1=(\"@method\" \"@authority\" \"@path\" \"content-digest\");created=1618884473;keyid=\"test-key\";nonce=\"b3k2pp5k7z\", other=:aGVsbG8=:";

        var members = StructuredFields.ParseDictionary(input);

        await Assert.That(StructuredFields.Serialize(members["sig1"])).IsEqualTo("(\"@method\" \"@authority\" \"@path\" \"content-digest\");created=1618884473;keyid=\"test-key\";nonce=\"b3k2pp5k7z\"");
        await Assert.That(members["sig1"].Parameter("created")).IsEqualTo(1618884473L);
        await Assert.That(Encoding.ASCII.GetString((byte[])members["other"].Item!.Value)).IsEqualTo("hello");
        await Assert.That(() => StructuredFields.ParseDictionary("sig1=(\"@method\"")).Throws<FormatException>();
        await Assert.That(() => StructuredFields.ParseDictionary("Sig=1")).Throws<FormatException>();
    }

    [Test]
    public async Task A_security_key_signature_needs_a_touch_and_its_own_application()
    {
        var yubikey = new TestSecurityKey();
        var data = "plugin bytes"u8.ToArray();

        var touched = yubikey.Sign(data, "meep-plugin");
        yubikey.Flags = 0x04; // user verified, but not present
        var untouched = yubikey.Sign(data, "meep-plugin");

        await Assert.That(yubikey.Public.IsSecurityKey).IsTrue();
        await Assert.That(SshSignature.FromBinary(touched).Verifies(data)).IsTrue();
        await Assert.That(SshSignature.FromBinary(touched).Verifies("other bytes"u8.ToArray())).IsFalse();
        await Assert.That(SshSignature.FromBinary(untouched).Verifies(data)).IsFalse();
        await Assert.That(Meep2.Security.PublicKey.Parse("k", yubikey.Public.Text).SameKeyAs(yubikey.Public)).IsTrue();
    }

    [Test]
    public async Task The_trust_store_is_only_read_when_first_wanted_and_tried_again_after_a_failure()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        var log = new StringBuilder();
        using var source = TrustStoreSource.Watching(folder, line => { lock (log) log.AppendLine(line); });

        await Assert.That(Directory.Exists(folder.Path)).IsFalse();
        var store = source.Current;
        await Assert.That(store).IsNotNull();
        await Assert.That(Directory.Exists(folder.Path)).IsTrue();
        await Assert.That(source.Current).IsSameReferenceAs(store);

        if (!OperatingSystem.IsWindows())
        {
            using var other = new TemporaryFolder();
            var shared = TrustFolder.In(other.Path);
            shared.Create();
            File.SetUnixFileMode(shared.Authorities, PrivateFolders.File | UnixFileMode.OtherWrite);
            using var broken = TrustStoreSource.Watching(shared, line => { lock (log) log.AppendLine(line); });
            await Assert.That(broken.Current).IsNull();
            await Assert.That(broken.Current).IsNull();
            await Assert.That(log.ToString().Split("can't be read").Length - 1).IsEqualTo(1).Because("a failure is reported once, then left for ten seconds");
        }
    }
}
