using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Tests.Support;
using PublicKey = Meep2.Security.PublicKey;

namespace Meep2.Tests;

/// <summary>
/// Authorise, Revoke and Parse, the reader they share with Verify, grants signed with this machine's authority key,
/// and the trust store changed by more than one writer at once (permissions phase 5).
/// </summary>
public class AuthoriseTests
{
    private static PermissionName Name(string text) => PermissionName.Parse(text);

    private static IReadOnlyList<ErrorMessage> Errors(TestRun run) => [.. run.At("Errors").OfType<ErrorMessage>()];

    // ---- The shared reader ----

    [Test]
    public async Task The_shared_reader_tells_each_kind_of_text_apart_by_its_form()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync();
        using var ca = TestCa.Root();
        var grant = VerifyLab.Sign(lab.Lab.Granter, new GrantTerms(lab.Bob, Name("WriteNotes")));

        await Assert.That(SecurityText.KindOf(lab.Bob.ToText() + " bob@laptop")).IsEqualTo(SecurityTextKind.Credential);
        await Assert.That(SecurityText.KindOf(Convert.ToBase64String(lab.Bob.Blob.Span))).IsEqualTo(SecurityTextKind.Credential);
        await Assert.That(SecurityText.KindOf(ca.Certificate.ExportCertificatePem())).IsEqualTo(SecurityTextKind.Certificate);
        await Assert.That(SecurityText.KindOf(Convert.ToBase64String(ca.Certificate.RawData))).IsEqualTo(SecurityTextKind.Certificate);
        await Assert.That(SecurityText.KindOf("WriteNotes:~/notes/**")).IsEqualTo(SecurityTextKind.Permission);
        await Assert.That(SecurityText.KindOf("#permission WriteNotes requires=ReadNotes")).IsEqualTo(SecurityTextKind.Permission);
        await Assert.That(SecurityText.KindOf(grant.ToText())).IsEqualTo(SecurityTextKind.Grant);
        await Assert.That(SecurityText.KindOf(lab.BobKey.Public.Text)).IsEqualTo(SecurityTextKind.None);
        await Assert.That(SecurityText.KindOf("{\"credential\": 1}")).IsEqualTo(SecurityTextKind.None);
        await Assert.That(SecurityText.KindOf("   ")).IsEqualTo(SecurityTextKind.None);

        await Assert.That(SecurityText.Read(lab.Bob.ToText())).IsTypeOf<Credential>();
        await Assert.That(SecurityText.Read(grant.ToText()) is Grant read && read.Terms.Id == grant.Terms.Id).IsTrue();
        await Assert.That(((Permission)SecurityText.Read("WriteNotes:~/notes/**")).Name).IsEqualTo(Name("WriteNotes:~/notes/**"));
        await Assert.That(() => SecurityText.Read(lab.BobKey.Public.Text)).Throws<FormatException>().WithMessageContaining("SSH public key");
        await Assert.That(() => SecurityText.Read("{\"a\": 1}")).Throws<FormatException>().WithMessageContaining("JSON");
        await Assert.That(() => SecurityText.Read("not a thing at all")).Throws<FormatException>().WithMessageContaining("starts \"not\"");
        // Read as a kind, whatever its form, it says why it isn't one.
        await Assert.That(() => SecurityText.Read<Credential>("WriteNotes")).Throws<FormatException>();
        await Assert.That(SecurityText.Read<Permission>("WriteNotes").Name).IsEqualTo(Name("WriteNotes"));

        // Verify chooses its inflection by the same reader.
        foreach (var (text, kind) in new[]
        {
            (lab.Bob.ToText(), VerificationKind.Credential),
            (ca.Certificate.ExportCertificatePem(), VerificationKind.Certificate),
            ("WriteNotes", VerificationKind.Permission),
            (grant.ToText(), VerificationKind.Credential),
            ("nonsense!", VerificationKind.Credential),
        })
        {
            await Assert.That(Meep2.Runtime.Verifying.VerifySubject.KindOfText(text)).IsEqualTo(kind);
        }
    }

    // ---- Parse ----

    [Test]
    public async Task Parse_emits_typed_messages_and_an_error_for_text_that_is_none_of_them()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync();
        using var ca = TestCa.Root();
        var pem = Path.Combine(lab.Lab.Home, "ca.pem");
        File.WriteAllText(pem, ca.Certificate.ExportCertificatePem());
        var key = Path.Combine(lab.Lab.Home, "bob.pub");
        File.WriteAllText(key, lab.BobKey.Public.Text + " bob@laptop\n");

        var run = await TestRun.Start($"""
            Read "{lab.BobFile}"
                Parse
                    Sink as Credentials
                    Parse
                        Sink as Again
                Parse kind=certificate
                    Sink as Errors @errors
                    Sink as Unexpected @no-errors
            Read "{pem}"
                Parse
                    Sink as Certificates
            Read "{key}"
                Parse
                    Sink as KeyErrors @errors
            Numbers to=1
                Parse from="WriteNotes:~/notes/**"
                    Sink as Permissions
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        var credential = (Credential)run.At("Credentials").Single();
        await Assert.That(credential.KeyFingerprint).IsEqualTo(lab.Bob.KeyFingerprint);
        await Assert.That(credential.Parent).IsTypeOf<FileText>();
        await Assert.That(run.At("Again").Single()).IsSameReferenceAs(credential);
        await Assert.That(((Certificate)run.At("Certificates").Single()).Subject).IsEqualTo("CN=Test Root");
        await Assert.That(((Permission)run.At("Permissions").Single()).Name).IsEqualTo(Name("WriteNotes:~/notes/**"));
        await Assert.That(Errors(run).Single().Error).Contains("certificate is written as PEM");
        await Assert.That(run.At("Unexpected")).IsEmpty();
        await Assert.That(((ErrorMessage)run.At("KeyErrors").Single()).Error).Contains("SSH public key");
    }

    // ---- Signing grants ----

    [Test]
    public async Task This_machines_authority_key_is_read_from_its_OpenSSH_file_and_signs_grants()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync();
        var grant = lab.Lab.Folder.SignGrant(new GrantTerms(lab.Bob, Name("WriteNotes")));
        var own = PublicKey.Parse("own", File.ReadAllText(lab.Lab.Folder.AuthorityPublicKey));
        var signature = SshSignature.FromBinary(grant.Signature.Span);
        await Assert.That(signature.Namespace).IsEqualTo(Grant.Namespace);
        await Assert.That(signature.Key.SameKeyAs(own)).IsTrue();
        await Assert.That(signature.Verifies(grant.Terms.ToSignedData())).IsTrue();
        await Assert.That(Meep2.Runtime.Verifying.CredentialCheck.WhyNotGood(grant, lab.Lab.Store(), DateTime.UtcNow)).IsNull();

        using var keys = new TemporaryFolder();
        var encrypted = Path.Combine(keys.Path, "encrypted");
        await SshKeygen.Run(["-q", "-t", "ed25519", "-N", "secret", "-f", encrypted]);
        await Assert.That(() => SshKeyPair.FromOpenSsh("x", File.ReadAllText(encrypted))).Throws<FormatException>().WithMessageContaining("passphrase");
        var ecdsa = await SshKeygen.NewKeyAsync(keys.Path, "ecdsa");
        await Assert.That(() => SshKeyPair.FromOpenSsh("x", File.ReadAllText(ecdsa))).Throws<FormatException>().WithMessageContaining("only with Ed25519");
        await Assert.That(() => SshKeyPair.FromOpenSsh("x", "hello")).Throws<FormatException>();

        File.Delete(lab.Lab.Folder.AuthorityKey);
        await Assert.That(() => lab.Lab.Folder.SignGrant(new GrantTerms(lab.Bob, Name("WriteNotes")))).Throws<TrustStoreException>().WithMessageContaining("meep trust init");
    }

    // ---- Authorise.Permission ----

    [Test]
    public async Task Authorise_grants_a_permission_records_it_and_it_counts_whenever_the_credential_is_verified()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "WriteNotes");
        var trust = lab.Trust();
        var before = trust.Current!.Version;

        var run = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\" valid=1h"), trust: trust);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Errors(run)).IsEmpty();
        var grant = (Grant)run.At("Granted").Single();
        await Assert.That(grant.Terms.Permission).IsEqualTo(Name("WriteNotes"));
        await Assert.That(grant.Terms.AppliesTo(lab.Bob)).IsTrue();
        await Assert.That(grant.Terms.ValidBefore!.Value).IsGreaterThan(DateTime.UtcNow.AddMinutes(59));
        await Assert.That(grant.Terms.ValidBefore!.Value).IsLessThan(DateTime.UtcNow.AddMinutes(61));
        await Assert.That(grant.Parent).IsTypeOf<Credential>();

        // Recorded, and in force in the pipeline's own store at once.
        await Assert.That(trust.Current!.Version).IsGreaterThan(before);
        await Assert.That(trust.Current!.GrantsFor(lab.Bob).Single().Terms.Id).IsEqualTo(grant.Terms.Id);
        await Assert.That(File.ReadAllText(lab.Lab.Folder.Grants)).Contains(grant.ToText());

        // And whenever bob's credential is verified later, from a store read afresh.
        var verified = await VerifyLab.VerifyAsync(lab.Lab.Store(), new VerifyParameters { Bearer = true }, lab.Bob);
        await Assert.That(verified.Success).IsTrue();
        await Assert.That(verified.Permissions).Contains(Name("WriteNotes"));
        await Assert.That(verified.Grants.Single().Terms.Id).IsEqualTo(grant.Terms.Id);
    }

    [Test]
    public async Task Authorise_passes_on_only_what_the_authorising_credential_holds_unless_it_holds_AuthoriseAny()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var scoped = await AuthoriseLab.CreateAsync("AuthorisePermission", "WriteNotes:~/notes/**");

        var narrower = await TestRun.Start(scoped.Granting("Authorise permission=\"WriteNotes:~/notes/inbox/**\""), trust: scoped.Trust());
        await Assert.That(narrower.Diagnostics).IsEmpty();
        await Assert.That(((Grant)narrower.At("Granted").Single()).Terms.Permission).IsEqualTo(Name("WriteNotes:~/notes/inbox/**"));

        var wider = await TestRun.Start(scoped.Granting("Authorise permission=\"WriteNotes\""), trust: scoped.Trust());
        await Assert.That(wider.At("Granted")).IsEmpty();
        var denied = (PermissionDenied)Errors(wider).Single();
        await Assert.That(denied.Needed.Single()).IsEqualTo(Name("WriteNotes"));
        await Assert.That(denied.Reasons.Single()).Contains("passes on only what the authorising credential holds, unless it holds AuthoriseAny");
        await Assert.That(wider.Result.Pipeline!.GetStats(wider.Result.Pipeline.Modules.Single(m => m.Verb == "Authorise")).Denied).IsEqualTo(1);

        var other = await TestRun.Start(scoped.Granting("Authorise permission=\"ReadNotes\""), trust: scoped.Trust());
        await Assert.That(other.At("Granted")).IsEmpty();
        await Assert.That(Errors(other).Single()).IsTypeOf<PermissionDenied>();
        await Assert.That(scoped.Lab.Store().Grants.Count).IsEqualTo(1);

        using var any = await AuthoriseLab.CreateAsync("AuthorisePermission", "AuthoriseAny");
        var granted = await TestRun.Start(any.Granting("Authorise permission=\"ReadNotes\""), trust: any.Trust());
        await Assert.That(Errors(granted)).IsEmpty();
        await Assert.That(((Grant)granted.At("Granted").Single()).Terms.Permission).IsEqualTo(Name("ReadNotes"));
    }

    [Test]
    public async Task Authorise_needs_AuthorisePermission_and_permission_written_on_it_adds_to_that()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("WriteNotes");
        var run = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\""), trust: lab.Trust());
        var denied = (PermissionDenied)Errors(run).Single();
        await Assert.That(denied.Needed.Single()).IsEqualTo(Name("AuthorisePermission"));
        await Assert.That(lab.Lab.Store().Grants).IsEmpty();

        using var both = await AuthoriseLab.CreateAsync("AuthorisePermission", "WriteNotes");
        var added = await TestRun.Start(both.Granting("Authorise permission=\"WriteNotes\" @permission=ReadNotes"), trust: both.Trust());
        await Assert.That(((PermissionDenied)Errors(added).Single()).Needed.Single()).IsEqualTo(Name("ReadNotes"));

        // The two inflections take different types, so each message meets only its own inflection's gate.
        var registry = new ModuleRegistry().AddAssembly(RuntimeModules.Assembly);
        await Assert.That(registry.TryGetVerb("Authorise", out var inflections)).IsTrue();
        var byInflection = inflections!.ToDictionary(r => r.Inflection);
        await Assert.That(byInflection["Permission"].InputType).IsEqualTo(typeof(Credential));
        await Assert.That(byInflection["Permission"].RequiredPermissions.Single()).IsEqualTo(Name("AuthorisePermission"));
        await Assert.That(byInflection["Certificate"].InputType).IsEqualTo(typeof(Certificate));
        await Assert.That(byInflection["Certificate"].RequiredPermissions.Single()).IsEqualTo(Name("AuthoriseCertificate"));
        await Assert.That(registry.TryGetVerb("Revoke", out var revoke) && revoke!.All(r => r.RequiredPermissions.Single() == Name("RevokePermission"))).IsTrue();
    }

    [Test]
    public async Task Authorize_is_the_same_verb_and_keeps_the_name_written()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "WriteNotes");
        var run = await TestRun.Start(lab.Granting("Authorize permission=\"WriteNotes\""), trust: lab.Trust());
        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Granted").Single()).IsTypeOf<Grant>();
        await Assert.That(run.Result.Pipeline!.Modules.Any(m => m.Verb == "Authorize")).IsTrue();

        var registry = new ModuleRegistry().AddAssembly(RuntimeModules.Assembly);
        await Assert.That(registry.TryGetVerb("Authorize", out var inflections)).IsTrue();
        await Assert.That(inflections!.Select(r => r.Inflection).Order()).IsEquivalentTo(["Certificate", "Permission"]);
        await Assert.That(inflections!.All(r => r.AliasOf == "Authorise")).IsTrue();
        await Assert.That(registry.TryGetVerb("Authorise", out var own) && own!.All(r => r.AliasOf is null)).IsTrue();
    }

    [Test]
    public async Task Authorise_refuses_what_it_cant_grant_saying_why()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "AuthoriseAny");

        // A credential from an authority this machine doesn't trust.
        using var stranger = await CredentialLab.CreateAsync();
        var foreign = await stranger.IssueAsync("-V", "-1h:+1h", "-I", "eve", "-n", "eve", "-z", "3");
        File.WriteAllText(lab.BobFile, foreign.ToText() + "\n");
        var untrusted = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\""), trust: lab.Trust());
        await Assert.That(Errors(untrusted).Single().Error).StartsWith("Authorise grants only to credentials this machine trusts, and eve's doesn't verify:");
        await Assert.That(Errors(untrusted).Single().Error).Contains("doesn't trust to issue credentials");

        // Not a permission's name.
        File.WriteAllText(lab.BobFile, lab.Bob.ToText() + "\n");
        var badName = await TestRun.Start(lab.Granting("Authorise permission=\"Write Notes\""), trust: lab.Trust());
        await Assert.That(Errors(badName).Single().Error).Contains("isn't a permission's name");

        // No authority key to sign with.
        File.Delete(lab.Lab.Folder.AuthorityKey);
        var unsigned = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\""), trust: lab.Trust());
        await Assert.That(Errors(unsigned).Single().Error).Contains("meep trust init");
        await Assert.That(lab.Lab.Store().Grants).IsEmpty();

        // A permission= is needed.
        var missing = await TestRun.Start(lab.Granting("Authorise.Permission"), trust: lab.Trust(), run: false);
        await Assert.That(missing.Diagnostics).Contains("permission");
    }

    [Test]
    public async Task Authorise_and_Revoke_need_a_proven_credential_unless_the_line_says_any()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "RevokePermission", "WriteNotes");
        foreach (var module in new[] { "Authorise permission=\"WriteNotes\"", "Revoke" })
        {
            var run = await TestRun.Start(lab.Granting(module, proven: true), trust: lab.Trust());
            await Assert.That(run.Diagnostics).IsEmpty();
            await Assert.That(run.At("Granted")).IsEmpty();
            var denied = (PermissionDenied)Errors(run).Single();
            await Assert.That(denied.Reasons.Single()).Contains("was verified with none, as a bearer credential, and this needs a signed request, a connection's proof or TLS.");
        }
        await Assert.That(lab.Lab.Store().Grants).IsEmpty();
        await Assert.That(lab.Lab.Store().Revocations.WhyRevoked(lab.Bob)).IsNull();

        // @proof=any says a bearer credential will do here.
        var any = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\" @proof=any", proven: true), trust: lab.Trust());
        await Assert.That(any.At("Granted").Single()).IsTypeOf<Grant>();

        // A line's own @proof replaces the default, rather than adding to it.
        var tls = await TestRun.Start(lab.Granting("Revoke @proof=tls", proven: true), trust: lab.Trust());
        await Assert.That(((PermissionDenied)Errors(tls).Single()).Reasons.Single()).Contains("this needs TLS.");
    }

    [Test]
    public async Task Only_a_credential_holding_AuthoriseAny_may_grant_the_permissions_over_the_trust_store()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "RevokePermission", "AuthoriseCertificate");
        foreach (var permission in SecurityPermissions.OverTrust)
        {
            var run = await TestRun.Start(lab.Granting($"Authorise permission=\"{permission}\""), trust: lab.Trust());
            await Assert.That(run.At("Granted")).IsEmpty();
            var denied = (PermissionDenied)Errors(run).Single();
            await Assert.That(denied.Needed.Single()).IsEqualTo(Name("AuthoriseAny"));
            await Assert.That(denied.Reasons.Single()).StartsWith($"Only a credential holding AuthoriseAny may grant {permission}, which changes what this machine trusts:");
        }
        await Assert.That(lab.Lab.Store().Grants).IsEmpty();

        using var any = await AuthoriseLab.CreateAsync("AuthorisePermission", "AuthoriseAny");
        var granted = await TestRun.Start(any.Granting("Authorise permission=\"AuthorisePermission\""), trust: any.Trust());
        await Assert.That(((Grant)granted.At("Granted").Single()).Terms.Permission).IsEqualTo(Name("AuthorisePermission"));
    }

    [Test]
    public async Task What_comes_from_a_signed_request_must_come_from_a_part_its_signature_covers()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync();
        var bob = Convert.ToBase64String(lab.Bob.Blob.Span);
        string? Why(RequestSigner signer, string text, bool whole = false, Action<Dictionary<string, string>>? change = null) =>
            Meep2.Runtime.Verifying.SignedParts.WhyNotCovered(signer.Request(change), text, whole, "the credential to grant to");
        var admin = Credential.Parse(File.ReadAllText(lab.AdminFile));
        var signer = new RequestSigner(RequestSigner.With(lab.Lab.Subject), admin);

        // In the body, which content-digest covers; in a covered header; in Meep-Credential; or not in the request at all.
        await Assert.That(Why(signer with { Body = lab.Bob.ToText() + " bob@laptop" }, bob)).IsNull();
        await Assert.That(Why(signer with { Extra = new Dictionary<string, string> { ["X-Subject"] = bob }, Components = ["@method", "@target-uri", "@authority", "content-digest", "x-subject"] }, bob)).IsNull();
        await Assert.That(Why(signer, Convert.ToBase64String(admin.Blob.Span))).IsNull();
        await Assert.That(Why(signer, bob)).IsNull();
        // In the query, which @target-uri covers.
        await Assert.That(Why(signer with { Url = $"http://localhost:8080/grants?subject={Uri.EscapeDataString(bob)}" }, bob)).IsNull();

        // In a header the signature doesn't cover: refused, naming it.
        var moved = Why(signer with { Extra = new Dictionary<string, string> { ["X-Subject"] = bob } }, bob);
        await Assert.That(moved).IsEqualTo("It took the credential to grant to from the request's X-Subject header, which the request's signature doesn't cover, so it could have been changed on the way. Read it from the body, or have the client sign x-subject too.");
        // A permission's name, matched as a header's whole value.
        await Assert.That(Why(signer with { Extra = new Dictionary<string, string> { ["X-Permission"] = "ReadNotes" } }, "ReadNotes", whole: true)).IsNotNull();
        await Assert.That(Why(signer with { Body = "ReadNotes", Extra = new Dictionary<string, string> { ["X-Permission"] = "ReadNotes" } }, "ReadNotes", whole: true)).IsNull();
        // Wrapped as PEM, base64 is still found.
        await Assert.That(Why(signer with { Body = string.Join("\n", bob.Chunk(64).Select(c => new string(c))) }, bob)).IsNull();

        // An unsigned request has nothing to compare with: Verify and the gate decide.
        await Assert.That(Why(signer, bob, change: h => { h.Remove("Signature"); h.Remove("Signature-Input"); h["X-Subject"] = bob; })).IsNull();
    }

    // ---- Authorise.Certificate ----

    [Test]
    public async Task Authorise_trusts_a_CA_so_certificates_that_chain_to_it_verify()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthoriseCertificate");
        using var ca = TestCa.Root("CN=Department CA");
        using var leaf = ca.Issue("CN=carol");
        var pem = Path.Combine(lab.Lab.Home, "ca.pem");
        File.WriteAllText(pem, ca.Certificate.ExportCertificatePem());
        var leafPem = Path.Combine(lab.Lab.Home, "leaf.pem");
        File.WriteAllText(leafPem, leaf.ExportCertificatePem());
        var certificate = Certificate.FromDer(leaf.RawData);

        var before = await VerifyLab.VerifyAsync(lab.Lab.Store(), new VerifyParameters { Bearer = true }, certificate);
        await Assert.That(before.Success).IsFalse();

        var trust = lab.Trust();
        var run = await TestRun.Start(lab.Granting("Authorise", pem, "certificate"), trust: trust);
        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Errors(run)).IsEmpty();
        await Assert.That(((Certificate)run.At("Granted").Single()).Subject).IsEqualTo("CN=Department CA");
        var root = trust.Current!.Roots.Single();
        await Assert.That(root.Name).StartsWith("Department-CA-");
        await Assert.That(root.Certificate.Thumbprint).IsEqualTo(ca.Certificate.Thumbprint);

        var after = await VerifyLab.VerifyAsync(lab.Lab.Store(), new VerifyParameters { Bearer = true }, certificate);
        await Assert.That(after.Success).IsTrue();

        // Trusted already: passed on without writing it again.
        var again = await TestRun.Start(lab.Granting("Authorise name=\"another\"", pem, "certificate"), trust: lab.Trust());
        await Assert.That(again.At("Granted").Single()).IsTypeOf<Certificate>();
        await Assert.That(Directory.GetFiles(lab.Lab.Folder.Roots).Length).IsEqualTo(1);

        // A leaf isn't a CA.
        var notCa = await TestRun.Start(lab.Granting("Authorise", leafPem, "certificate"), trust: lab.Trust());
        await Assert.That(Errors(notCa).Single().Error).Contains("isn't a CA's certificate");

        // AuthoriseCertificate is needed, not AuthorisePermission.
        using var other = await AuthoriseLab.CreateAsync("AuthorisePermission");
        var refused = await TestRun.Start(other.Granting("Authorise", pem, "certificate"), trust: other.Trust());
        await Assert.That(((PermissionDenied)Errors(refused).Single()).Needed.Single()).IsEqualTo(Name("AuthoriseCertificate"));
    }

    // ---- Revoke ----

    [Test]
    public async Task Revoke_revokes_grants_credentials_and_certificates_and_the_store_takes_it_away_at_once()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "RevokePermission", "WriteNotes");

        // A grant, revoked as soon as it's made.
        var trust = lab.Trust();
        var run = await TestRun.Start(lab.Granting("""
            Authorise permission="WriteNotes"
                                Revoke
            """), trust: trust);
        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Errors(run)).IsEmpty();
        var grant = (Grant)run.At("Granted").Single();
        await Assert.That(trust.Current!.Revocations.WhyRevoked(grant)).IsNotNull();
        var verified = await VerifyLab.VerifyAsync(lab.Lab.Store(), new VerifyParameters { Bearer = true }, lab.Bob);
        await Assert.That(verified.Permissions).DoesNotContain(Name("WriteNotes"));
        await Assert.That(verified.Notes.Single()).Contains("revoked");

        // A credential, by its serial, and again, which writes nothing more.
        var revision = trust.Current!.RevocationVersion;
        await TestRun.Start(lab.Granting("Revoke"), trust: trust);
        await Assert.That(trust.Current!.RevocationVersion).IsGreaterThan(revision);
        await Assert.That(trust.Current!.Revocations.WhyRevoked(lab.Bob)).IsNotNull();
        var lines = File.ReadAllLines(lab.Lab.Folder.Revoked).Count(l => l.StartsWith("serial ", StringComparison.Ordinal));
        var twice = await TestRun.Start(lab.Granting("Revoke"), trust: trust);
        await Assert.That(twice.At("Granted").Single()).IsTypeOf<Credential>();
        await Assert.That(File.ReadAllLines(lab.Lab.Folder.Revoked).Count(l => l.StartsWith("serial ", StringComparison.Ordinal))).IsEqualTo(lines);

        // By its key.
        var byKey = await TestRun.Start(lab.Granting("Revoke whole-key=true"), trust: trust);
        await Assert.That(byKey.Diagnostics).IsEmpty();
        await Assert.That(Errors(byKey)).IsEmpty();
        await Assert.That(File.ReadAllText(lab.Lab.Folder.Revoked)).Contains($"key {lab.Bob.KeyFingerprint}");

        // A certificate.
        using var ca = TestCa.Root();
        using var leaf = ca.Issue("CN=carol");
        var pem = Path.Combine(lab.Lab.Home, "leaf.pem");
        File.WriteAllText(pem, leaf.ExportCertificatePem());
        await TestRun.Start(lab.Granting("Revoke", pem, "certificate"), trust: trust);
        await Assert.That(trust.Current!.Revocations.WhyRevoked(leaf)).IsNotNull();

        // RevokePermission is needed.
        using var other = await AuthoriseLab.CreateAsync("AuthorisePermission");
        var refused = await TestRun.Start(other.Granting("Revoke"), trust: other.Trust());
        await Assert.That(((PermissionDenied)Errors(refused).Single()).Needed.Single()).IsEqualTo(Name("RevokePermission"));
        await Assert.That(other.Lab.Store().Revocations.WhyRevoked(other.Bob)).IsNull();
    }

    [Test]
    public async Task A_grant_names_who_authorised_it_and_falls_when_their_credential_is_revoked()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "WriteNotes");
        var admin = Credential.Parse(File.ReadAllText(lab.AdminFile));
        var trust = lab.Trust();

        var run = await TestRun.Start(lab.Granting("Authorise permission=\"WriteNotes\""), trust: trust);
        await Assert.That(Errors(run)).IsEmpty();
        var grant = (Grant)run.At("Granted").Single();
        await Assert.That(grant.Terms.Format).IsEqualTo(Grant.Format);
        await Assert.That(grant.Terms.AuthorisedBy.Single()).IsEqualTo(CredentialName.Of(admin));
        await Assert.That(trust.Current!.GrantsFor(lab.Bob).Single().Terms.AuthorisedBy.Single()).IsEqualTo(CredentialName.Of(admin));
        var before = await VerifyLab.VerifyAsync(trust.Current, new VerifyParameters { Bearer = true }, lab.Bob);
        await Assert.That(before.Permissions).Contains(Name("WriteNotes"));

        // Revoking someone else's credential changes nothing.
        lab.Lab.Folder.Revoke(Revoking.Credential(await lab.Lab.IssueAsync("-z", "99")));
        trust.Reload();
        await Assert.That(trust.Current!.Revocations.WhyRevoked(grant)).IsNull();

        // Revoking the administrator's takes the grant with it: Verify leaves it out, and a gate that let bob through
        // before, on a verification made before, refuses him now.
        lab.Lab.Folder.Revoke(Revoking.Credential(admin));
        trust.Reload();
        await Assert.That(trust.Current!.Revocations.WhyRevoked(grant)).Contains("falls with the credential that authorised it");
        var after = await VerifyLab.VerifyAsync(trust.Current, new VerifyParameters { Bearer = true }, lab.Bob);
        await Assert.That(after.Success).IsTrue();
        await Assert.That(after.Permissions).DoesNotContain(Name("WriteNotes"));
        await Assert.That(after.Notes.Single()).Contains("falls with the credential that authorised it");
        var gate = new Meep2.Runtime.Verifying.PermissionLines(trust.Current!).WhyNotMet(before, new Permission(Name("WriteNotes")));
        await Assert.That(gate).IsEqualTo("bob's credential doesn't hold WriteNotes.");
    }

    // ---- The worked example ----

    [Test]
    public async Task The_small_authorisation_system_in_the_docs_grants_and_revokes_over_HTTP()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "RevokePermission", "ReadNotes");
        var notes = Path.Combine(lab.Lab.Home, "today.txt");
        File.WriteAllText(notes, "Buy milk.");
        int[] ports = [TestServer.FreePort(), TestServer.FreePort(), TestServer.FreePort()];
        var notesUrl = $"http://127.0.0.1:{ports[0]}/notes/";
        var grantsUrl = $"http://127.0.0.1:{ports[1]}/grants/?permission=ReadNotes";
        var revokeUrl = $"http://127.0.0.1:{ports[2]}/revoke/";

        // The example in docs/verbs.md, under "A small authorisation system", with its ports and file.
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{ports[0]}}/notes/" signature=required
                Verify from=msg.Headers["Meep-Credential"]
                    Fetch "{{notes}}" @permission=ReadNotes
                        Send error-status=403 error-reply="{msg.Error}"

            Listen "http://127.0.0.1:{{ports[1]}}/grants/" signature=required
                Verify from=msg.Headers["Meep-Credential"]
                    Parse kind=credential from=fam.Listen.Text
                        Authorise permission=fam.Listen.Query['permission'] valid=90d
                            Send error-status=403 error-reply="{msg.Error}"

            Listen "http://127.0.0.1:{{ports[2]}}/revoke/" signature=required
                Verify from=msg.Headers["Meep-Credential"]
                    Parse kind=grant from=fam.Listen.Text
                        Revoke
                            Send text="Revoked." error-status=403 error-reply="{msg.Error}"
            """, lab.Trust());
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();
        var admin = await lab.Lab.Credentials.IssueForAsync(lab.Lab.Subject.Public.Text, "-V", "-1h:+1h", "-I", "admin", "-n", "admin", "-z", "7",
            "-O", "extension:AuthorisePermission@meep", "-O", "extension:RevokePermission@meep", "-O", "extension:ReadNotes@meep");

        Task<(System.Net.HttpStatusCode Status, string Text)> AsBob(string url, string body = "") =>
            PostAsync(client, new RequestSigner(RequestSigner.With(lab.BobKey), lab.Bob) { Url = url, Body = body });
        Task<(System.Net.HttpStatusCode Status, string Text)> AsAdmin(string url, string body) =>
            PostAsync(client, new RequestSigner(RequestSigner.With(lab.Lab.Subject), admin) { Url = url, Body = body });

        var before = await AsBob(notesUrl);
        await Assert.That(before.Status).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
        await Assert.That(before.Text).Contains("needs ReadNotes");

        var selfGranted = await AsBob(grantsUrl, lab.Bob.ToText());
        await Assert.That(selfGranted.Status).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
        await Assert.That(selfGranted.Text).Contains("needs AuthorisePermission");

        var granted = await AsAdmin(grantsUrl, lab.Bob.ToText());
        await Assert.That(granted.Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        var grant = Grant.Parse(granted.Text);
        await Assert.That(grant.Terms.Permission).IsEqualTo(Name("ReadNotes"));
        await Assert.That(grant.Terms.ValidBefore!.Value).IsGreaterThan(DateTime.UtcNow.AddDays(89));

        var after = await AsBob(notesUrl);
        await Assert.That(after.Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(after.Text).IsEqualTo("Buy milk.");

        var revoked = await AsAdmin(revokeUrl, granted.Text);
        await Assert.That(revoked.Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(revoked.Text).IsEqualTo("Revoked.");

        var again = await AsBob(notesUrl);
        await Assert.That(again.Status).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
        await Assert.That(again.Text).Contains("needs ReadNotes");
    }

    [Test]
    public async Task Authorise_refuses_a_credential_read_from_a_header_the_signature_doesnt_cover()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync("AuthorisePermission", "ReadNotes");
        var port = TestServer.FreePort();
        var url = $"http://127.0.0.1:{port}/grants/";
        await using var running = TestRun.Begin($$"""
            Listen "{{url}}" signature=required
                Verify from=msg.Headers["Meep-Credential"]
                    Parse kind=credential from=fam.Listen.Headers["X-Subject"]
                        Authorise permission="ReadNotes"
                            Send error-status=403 error-reply="{msg.Error}"
            """, lab.Trust());
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();
        var admin = Credential.Parse(File.ReadAllText(lab.AdminFile));
        var extra = new Dictionary<string, string> { ["X-Subject"] = Convert.ToBase64String(lab.Bob.Blob.Span) };
        var signer = new RequestSigner(RequestSigner.With(lab.Lab.Subject), admin) { Url = url, Body = "grant", Extra = extra };

        var uncovered = await PostAsync(client, signer);
        await Assert.That(uncovered.Status).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
        await Assert.That(uncovered.Text).Contains("from the request's X-Subject header, which the request's signature doesn't cover");
        await Assert.That(lab.Lab.Store().Grants).IsEmpty();

        var covered = await PostAsync(client, signer with { Components = ["@method", "@target-uri", "@authority", "content-digest", "x-subject"] });
        await Assert.That(covered.Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(Grant.Parse(covered.Text).Terms.AppliesTo(lab.Bob)).IsTrue();
    }

    // Posts a signed request, trying again until the Listen is up.
    private static async Task<(System.Net.HttpStatusCode Status, string Text)> PostAsync(HttpClient client, RequestSigner signer)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, signer.Url) { Content = new StringContent(signer.Body, Encoding.UTF8, "text/plain") };
            foreach (var (name, value) in signer.Headers())
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            try
            {
                using var response = await client.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    // ---- More than one writer ----

    [Test]
    public async Task Writers_at_once_lose_nothing_and_a_watcher_never_reads_half_a_file()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await AuthoriseLab.CreateAsync();
        var problems = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var watcher = new TrustStoreWatcher(lab.Lab.Folder, line =>
        {
            if (line.Contains("can't be read", StringComparison.Ordinal))
                problems.Enqueue(line);
        });

        const int Writers = 8;
        const int Each = 15;
        var grants = new System.Collections.Concurrent.ConcurrentBag<Grant>();
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            // A folder of its own, as another process would have.
            var folder = new TrustFolder(lab.Lab.Folder.Path);
            for (var i = 0; i < Each; i++)
            {
                var grant = folder.SignGrant(new GrantTerms(lab.Bob, Name($"Note{writer}x{i}")));
                folder.RecordGrant(grant);
                folder.Revoke(Revoking.Key($"SHA256:writer{writer}x{i}"));
                grants.Add(grant);
                watcher.Reload();
            }
        })));

        var store = lab.Lab.Store();
        await Assert.That(store.Grants.Count).IsEqualTo(Writers * Each);
        await Assert.That(store.Grants.Select(g => g.Terms.Id).ToHashSet().SetEquals(grants.Select(g => g.Terms.Id))).IsTrue();
        await Assert.That(store.Revocations.Entries.Count).IsEqualTo(Writers * Each);
        await Assert.That(problems).IsEmpty();
        await Assert.That(Directory.GetFiles(lab.Lab.Folder.Path, "*.new")).IsEmpty();
        await Assert.That(watcher.Reload().Grants.Count).IsEqualTo(Writers * Each);
    }

    /// <summary>
    /// A trust store with this machine's own authority key (trusted for credentials and grants), an admin's bearer
    /// credential holding some permissions in a file, and bob's bearer credential in another.
    /// </summary>
    private sealed class AuthoriseLab : IDisposable
    {
        private AuthoriseLab(VerifyLab lab, string adminFile, Credential bob, SshKeyPair bobKey, string bobFile)
        {
            Lab = lab;
            AdminFile = adminFile;
            Bob = bob;
            BobKey = bobKey;
            BobFile = bobFile;
        }

        public VerifyLab Lab { get; }

        public string AdminFile { get; }

        public Credential Bob { get; }

        public SshKeyPair BobKey { get; }

        public string BobFile { get; }

        public static async Task<AuthoriseLab> CreateAsync(params string[] adminPermissions)
        {
            var lab = await VerifyLab.CreateAsync();
            lab.Define("#permission ReadNotes\n#permission WriteNotes\n");
            var own = await SshKeygen.NewKeyAsync(Path.Combine(lab.Home, "own"), "ed25519");
            File.Copy(own, lab.Folder.AuthorityKey);
            File.Copy(own + ".pub", lab.Folder.AuthorityPublicKey);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lab.Folder.AuthorityKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(lab.Folder.AuthorityPublicKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            lab.Folder.AddAuthority("this", PublicKey.Parse("this", File.ReadAllText(own + ".pub")), [TrustStore.CredentialNamespace, TrustStore.GrantNamespace]);

            var options = new List<string> { "-z", "7", "-O", "extension:permit-bearer@meep" };
            foreach (var permission in adminPermissions)
            {
                var colon = permission.IndexOf(':', StringComparison.Ordinal);
                options.AddRange(["-O", colon < 0 ? $"extension:{permission}@meep" : $"extension:{permission[..colon]}@meep={permission[(colon + 1)..]}"]);
            }
            var admin = await lab.IssueAsync([.. options]);
            var adminFile = Path.Combine(lab.Home, "admin.credential");
            File.WriteAllText(adminFile, admin.ToText() + " admin@laptop\n");

            var bobKey = SshKeyPair.CreateEd25519("bob");
            var bob = await lab.Credentials.IssueForAsync(bobKey.Public.Text, "-V", "-1h:+1h", "-I", "bob", "-n", "bob", "-z", "9", "-O", "extension:permit-bearer@meep");
            var bobFile = Path.Combine(lab.Home, "bob.credential");
            File.WriteAllText(bobFile, bob.ToText() + " bob@laptop\n");
            return new AuthoriseLab(lab, adminFile, bob, bobKey, bobFile);
        }

        /// <summary>The store as the folder has it now, read again whenever this process changes it.</summary>
        public TrustStoreSource Trust() => TrustStoreSource.Of(Lab.Store());

        /// <summary>
        /// A pipeline in which the admin's verified credential reads <paramref name="file"/> (bob's credential unless
        /// given), parses it, and hands it to <paramref name="module"/>, whose output goes to Granted and errors to Errors.
        /// </summary>
        /// <remarks>
        /// The administrator's credential is a bearer credential read from a file, so each Authorise and Revoke is given
        /// <c>@proof=any</c> unless <paramref name="proven"/> leaves their default, which needs a signed request.
        /// </remarks>
        public string Granting(string module, string? file = null, string kind = "credential", bool proven = false) => $"""
            Read "{AdminFile}"
                Verify bearer=true
                    Fetch "{file ?? BobFile}"
                        Parse kind={kind}
                            {(proven ? module : AnyProof(module))}
                                Sink as Granted @no-errors
                                Sink as Errors @errors
            """;

        // Each Authorise, Authorize or Revoke line with @proof=any added.
        private static string AnyProof(string module) => string.Join("\n", module.Split('\n').Select(line =>
            line.TrimStart().StartsWith("Authori", StringComparison.Ordinal) || line.TrimStart().StartsWith("Revoke", StringComparison.Ordinal)
                ? line.TrimEnd() + " @proof=any"
                : line));

        public void Dispose() => Lab.Dispose();
    }
}
