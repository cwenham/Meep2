using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Security;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class SecurityMessageTests
{
    [Test]
    public async Task A_verification_passes_only_when_it_has_no_failures()
    {
        var permission = new Permission(PermissionName.Parse("ReadNotes"));
        var passed = new VerifiedMessage(permission)
        {
            Kind = VerificationKind.Permission,
            Permissions = [permission.Name],
            TrustStoreVersion = 3,
            RevocationVersion = 5,
        };
        var failed = new VerifiedMessage(permission)
        {
            Kind = VerificationKind.Permission,
            Failures = [new VerificationFailure(VerificationFailureCode.UnknownPermission, "ReadNotes isn't defined in the trust store.")],
        };

        await Assert.That(passed.Success).IsTrue();
        await Assert.That(passed.IsGenuine).IsTrue();
        await Assert.That(passed.Subject).IsSameReferenceAs(permission);
        await Assert.That(passed.Proof).IsEqualTo(Proof.None);
        await Assert.That(passed.ToText()).IsEqualTo("Permission verified");
        await Assert.That(failed.Success).IsFalse();
        await Assert.That(failed.ToText()).IsEqualTo("Permission not verified: ReadNotes isn't defined in the trust store.");
    }

    [Test]
    public async Task A_copy_of_a_verification_isnt_genuine()
    {
        var verified = new VerifiedMessage(new Permission(PermissionName.Parse("ReadNotes"))) { Permissions = [PermissionName.Parse("ReadNotes")] };

        var copy = verified with { };

        await Assert.That(copy.IsGenuine).IsFalse();
        await Assert.That(copy.Success).IsTrue();
        await Assert.That(copy.Permissions).IsEquivalentTo(verified.Permissions);
        await Assert.That(copy.Parent).IsSameReferenceAs(verified);
    }

    [Test]
    public async Task Nothing_outside_the_runtime_can_make_or_change_a_verification()
    {
        var type = typeof(VerifiedMessage);
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var setters = type.GetProperties()
            .Where(p => p.DeclaringType == type && p.SetMethod is not null)
            .Where(p => p.SetMethod!.IsPublic || p.SetMethod.IsFamily || p.SetMethod.IsFamilyOrAssembly)
            .Select(p => p.Name);

        await Assert.That(type.IsSealed).IsTrue();
        await Assert.That(constructors.Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly)).IsEmpty();
        await Assert.That(setters).IsEmpty();
    }

    [Test]
    public async Task Only_the_runtime_can_mark_a_certificate_as_proven_by_tls_and_a_copy_loses_the_mark()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var x509 = SelfSigned("CN=alice", key);
        var certificate = Certificate.FromDer(x509.RawData).MarkProvenByTls();

        var copy = certificate with { };

        await Assert.That(certificate.ProvenByTls).IsTrue();
        await Assert.That(copy.ProvenByTls).IsFalse();
        await Assert.That(copy.Subject).IsEqualTo("CN=alice");
        await Assert.That(typeof(Certificate).GetProperty(nameof(Certificate.ProvenByTls))!.SetMethod!.IsPublic).IsFalse();
        await Assert.That(typeof(Certificate).GetMethod("MarkProvenByTls", BindingFlags.NonPublic | BindingFlags.Instance)!.IsAssembly).IsTrue();
    }

    [Test]
    public async Task A_pipeline_refuses_an_injected_verification_or_a_message_descended_from_one()
    {
        var builder = new PipelineBuilder("forged");
        var feed = new Feed();
        var sink = new Collect<Message>();
        var module = builder.Add(() => sink, name: "Sink");
        builder.Chain(builder.AddSource(() => feed), module);
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        var verified = new VerifiedMessage(new Permission(PermissionName.Parse("meep.files.write")));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var x509 = SelfSigned("CN=mallory", key);

        await Assert.That(() => pipeline.Inject(module, verified)).Throws<ArgumentException>().WithMessageContaining("can't be injected");
        await Assert.That(() => pipeline.Inject(module, verified with { })).Throws<ArgumentException>();
        await Assert.That(() => pipeline.Inject(module, new TextMessage("hello") { Parents = new(verified) })).Throws<ArgumentException>();
        await Assert.That(() => pipeline.Inject(module, Certificate.FromDer(x509.RawData).MarkProvenByTls())).Throws<ArgumentException>();

        pipeline.Inject(module, Certificate.FromDer(x509.RawData)); // a certificate that only says what it is can come in
        await Wait.For(sink.Reached(1));
        feed.Finish();
        await Wait.For(running);
        await Assert.That(sink.Received.Single()).IsTypeOf<Certificate>();
    }

    [Test]
    public async Task MessageJson_cant_read_a_message_in()
    {
        var methods = typeof(MessageJson).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        await Assert.That(methods.Where(m => typeof(Message).IsAssignableFrom(m.ReturnType)).Select(m => m.Name)).IsEmpty();
    }

    [Test]
    public async Task A_certificate_reads_from_der_or_from_pem_with_its_intermediates()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Home CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var request = new CertificateRequest("CN=alice", leafKey, HashAlgorithmName.SHA256);
        using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3]);

        var fromDer = Certificate.FromDer(leaf.RawData, [root.RawData]);
        var fromPem = Certificate.FromPem(leaf.ExportCertificatePem() + "\n" + root.ExportCertificatePem());

        await Assert.That(fromDer.Subject).IsEqualTo("CN=alice");
        await Assert.That(fromDer.Issuer).IsEqualTo("CN=Home CA");
        await Assert.That(fromDer.SerialNumber).IsEqualTo("010203");
        await Assert.That(fromDer.NotAfter).IsGreaterThan(DateTime.UtcNow);
        await Assert.That(fromDer.Thumbprint).IsEqualTo(leaf.GetCertHashString(HashAlgorithmName.SHA256));
        await Assert.That(fromDer.ProvenByTls).IsFalse();
        await Assert.That(fromPem.Thumbprint).IsEqualTo(fromDer.Thumbprint);
        await Assert.That(fromPem.Intermediates.Count).IsEqualTo(1);
        await Assert.That(fromPem.Intermediates[0].ToArray()).IsEquivalentTo(root.RawData);
        using var loaded = fromPem.ToX509();
        await Assert.That(loaded.Subject).IsEqualTo("CN=alice");
    }

    [Test]
    public async Task A_certificate_that_cant_be_read_is_refused()
    {
        await Assert.That(() => Certificate.FromDer([1, 2, 3])).Throws<FormatException>();
        await Assert.That(() => Certificate.FromPem("no certificate here")).Throws<FormatException>();
    }

    [Test]
    public async Task A_grant_round_trips_as_text_and_its_signature_covers_its_terms()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var credential = await lab.IssueAsync("-I", "alice", "-z", "42");
        var other = await lab.IssueAsync("-I", "alice", "-z", "43");
        using var authority = SshKeyPair.CreateEd25519("authority");
        var terms = new GrantTerms(credential, PermissionName.Parse("meep.files.write:~/notes/**"))
        {
            ValidBefore = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var signature = authority.Sign(terms.ToSignedData(), Grant.Namespace);

        var grant = Grant.Signed(terms, Unarmor(signature.ToArmored()));
        var read = Grant.Parse(grant.ToText());

        await Assert.That(grant.ToText()).StartsWith("meep-grant-v01 ");
        await Assert.That(read.Terms).IsEqualTo(terms);
        await Assert.That(read.Terms.AppliesTo(credential)).IsTrue();
        await Assert.That(read.Terms.AppliesTo(other)).IsFalse();
        await Assert.That(read.AuthorityFingerprint).IsEqualTo(authority.Public.Fingerprint);
        await Assert.That(read.AuthorityKey.ToArray()).IsEquivalentTo(authority.Public.Blob);
        await Assert.That(SshSignature.Parse(Armor(read.Signature.ToArray())).Verifies(read.Terms.ToSignedData())).IsTrue();
        await Assert.That(signature.Verifies(new GrantTerms(credential, PermissionName.Parse("meep.files.write")) { Id = terms.Id, ValidBefore = terms.ValidBefore }.ToSignedData())).IsFalse();
    }

    [Test]
    public async Task A_grant_signed_for_another_namespace_is_refused()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var terms = new GrantTerms(await lab.IssueAsync("-I", "alice"), PermissionName.Parse("ReadNotes"));
        using var authority = SshKeyPair.CreateEd25519("authority");
        var signature = authority.Sign(terms.ToSignedData(), "meep-plugin");

        await Assert.That(() => Grant.Signed(terms, Unarmor(signature.ToArmored()))).Throws<FormatException>().WithMessageContaining("meep-plugin");
        await Assert.That(() => Grant.Parse("meep-grant-v01 AAAA")).Throws<FormatException>();
        await Assert.That(() => Grant.Parse("ssh-ed25519 AAAA")).Throws<FormatException>();
    }

    [Test]
    public async Task A_permission_reads_as_its_name()
    {
        var permission = new Permission(PermissionName.Parse("ReadNotes"))
        {
            Authorities = ["Home CA"],
            Requires = [PermissionName.Parse("meep.files.read")],
        };

        await Assert.That(permission.ToText()).IsEqualTo("ReadNotes");
        await Assert.That(permission.Requires.Single().Name).IsEqualTo("meep.files.read");
    }

    private static X509Certificate2 SelfSigned(string subject, ECDsa key) =>
        new CertificateRequest(subject, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

    // PROTOCOL.sshsig's binary form, from the armored form SshSignature writes, and back.
    private static byte[] Unarmor(string armored) =>
        Convert.FromBase64String(string.Concat(armored.Split('\n').Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal))));

    private static string Armor(byte[] blob) =>
        $"-----BEGIN SSH SIGNATURE-----\n{Convert.ToBase64String(blob)}\n-----END SSH SIGNATURE-----\n";
}
