using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class CredentialTests
{
    [Test]
    public async Task A_certificate_from_ssh_keygen_reads_with_its_names_dates_serial_and_permissions()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var credential = await lab.IssueAsync(
            "-I", "alice@example", "-n", "alice,bob", "-z", "42", "-V", "20260101000000Z:20270101000000Z",
            "-O", "extension:ReadNotes@meep", "-O", "extension:meep.files.write@meep=~/notes/**",
            "-O", "force-command=ls");

        await Assert.That(credential.CertificateType).IsEqualTo("ssh-ed25519-cert-v01@openssh.com");
        await Assert.That(credential.KeyType).IsEqualTo("ssh-ed25519");
        await Assert.That(credential.Kind).IsEqualTo(SshCertificateKind.User);
        await Assert.That(credential.KeyId).IsEqualTo("alice@example");
        await Assert.That(credential.Principals).IsEquivalentTo(["alice", "bob"]);
        await Assert.That(credential.Serial).IsEqualTo(42UL);
        await Assert.That(credential.ValidAfter).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Assert.That(credential.ValidBefore).IsEqualTo(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Assert.That(credential.CriticalOptions["force-command"]).IsEqualTo("ls");
        await Assert.That(credential.Extensions.ContainsKey("permit-pty")).IsTrue();
        await Assert.That(credential.Permissions.Select(p => p.ToString())).IsEquivalentTo(["ReadNotes", "meep.files.write:~/notes/**"]);
        await Assert.That(credential.PermitsBearer).IsFalse();
        await Assert.That(credential.InDateAt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))).IsTrue();
        await Assert.That(credential.InDateAt(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc))).IsFalse();
        await Assert.That(credential.InDateAt(new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc))).IsFalse();
    }

    [Test]
    [Arguments("ed25519", null)]
    [Arguments("ecdsa", 256)]
    [Arguments("ecdsa", 384)]
    [Arguments("rsa", 2048)]
    public async Task Its_keys_are_the_subjects_and_the_authoritys_and_the_signature_covers_what_was_read(string type, int? bits)
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync(type, bits);
        var credential = await lab.IssueAsync("-I", "alice", "-z", "7");

        var subject = PublicKey.Parse("subject", lab.SubjectPublicKey);
        var authority = PublicKey.Parse("authority", lab.AuthorityPublicKey);
        await Assert.That(credential.Key.ToArray()).IsEquivalentTo(subject.Blob);
        await Assert.That(credential.KeyType).IsEqualTo(subject.Type);
        await Assert.That(credential.KeyFingerprint).IsEqualTo(subject.Fingerprint);
        await Assert.That(credential.AuthorityKey.ToArray()).IsEquivalentTo(authority.Blob);
        await Assert.That(credential.AuthorityFingerprint).IsEqualTo(authority.Fingerprint);
        await Assert.That(authority.Verify(credential.SignedData.ToArray(), credential.Signature.ToArray())).IsTrue();
        await Assert.That(credential.Principals).IsEmpty();
        await Assert.That(credential.ValidAfter).IsNull();
        await Assert.That(credential.ValidBefore).IsNull();
    }

    [Test]
    public async Task Changing_a_signed_byte_breaks_the_signature()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var credential = await lab.IssueAsync("-I", "alice", "-z", "7");
        var blob = credential.Blob.ToArray();
        blob[credential.SignedData.Length - 40] ^= 1; // inside the authority's key

        var authority = PublicKey.Parse("authority", lab.AuthorityPublicKey);
        var changed = Credential.FromBlob(blob);
        await Assert.That(authority.Verify(changed.SignedData.ToArray(), changed.Signature.ToArray())).IsFalse();
    }

    [Test]
    public async Task Bearer_use_and_host_certificates_are_read_as_such()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();

        var bearer = await lab.IssueAsync("-I", "script", "-O", "extension:permit-bearer@meep");
        var host = await lab.IssueAsync("-I", "server", "-h");

        await Assert.That(bearer.PermitsBearer).IsTrue();
        await Assert.That(bearer.Permissions).IsEmpty();
        await Assert.That(host.Kind).IsEqualTo(SshCertificateKind.Host);
    }

    [Test]
    public async Task A_meep_extension_that_isnt_a_permission_is_refused()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();

        var text = await lab.IssueTextAsync("-I", "alice", "-O", "extension:1bad@meep");

        await Assert.That(() => Credential.Parse(text)).Throws<FormatException>().WithMessageContaining("1bad@meep");
    }

    [Test]
    public async Task It_reads_the_same_from_text_with_or_without_its_type_and_comment()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var credential = await lab.IssueAsync("-I", "alice", "-z", "9");
        var base64 = credential.ToText().Split(' ')[1];

        await Assert.That(Credential.Parse(base64).Serial).IsEqualTo(9UL);
        await Assert.That(Credential.Parse(credential.ToText() + " alice's laptop").Serial).IsEqualTo(9UL);
        await Assert.That(() => Credential.Parse("ssh-rsa-cert-v01@openssh.com " + base64)).Throws<FormatException>();
    }

    [Test]
    public async Task Broken_certificates_are_refused_saying_why()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var blob = (await lab.IssueAsync("-I", "alice")).Blob.ToArray();

        await Assert.That(() => Credential.FromBlob(blob[..^1])).Throws<FormatException>().WithMessageContaining("ends too soon");
        await Assert.That(() => Credential.FromBlob([.. blob, 0])).Throws<FormatException>().WithMessageContaining("left over");
        await Assert.That(() => Credential.Parse("not base64!")).Throws<FormatException>();
        await Assert.That(() => Credential.Parse("")).Throws<FormatException>();
        await Assert.That(() => Credential.Parse(lab.SubjectPublicKey)).Throws<FormatException>().WithMessageContaining("isn't a kind of SSH certificate");
    }

    [Test]
    public async Task Its_fields_cant_be_set_so_they_always_match_the_signed_bytes()
    {
        var properties = typeof(Credential).GetProperties().Where(p => p.DeclaringType == typeof(Credential));

        await Assert.That(properties.Where(p => p.SetMethod is not null).Select(p => p.Name)).IsEmpty();
        await Assert.That(typeof(Credential).GetConstructors()).IsEmpty();
        await Assert.That(typeof(Credential).IsSealed).IsTrue();
    }
}

/// <summary>A certificate authority's key and a subject's key, in a folder, for issuing credentials with ssh-keygen.</summary>
internal sealed class CredentialLab : IDisposable
{
    private readonly TemporaryFolder _folder = new();
    private string _authority = "";
    private string _subject = "";
    private int _issued;

    public string AuthorityPublicKey => File.ReadAllText(_authority + ".pub");

    public string SubjectPublicKey => File.ReadAllText(_subject + ".pub");

    /// <summary>The folder the keys are in.</summary>
    public string Folder => _folder.Path;

    /// <summary>The authority's public key file.</summary>
    public string AuthorityPublicKeyFile => _authority + ".pub";

    /// <summary>The subject's public key file.</summary>
    public string SubjectPublicKeyFile => _subject + ".pub";

    /// <summary>Makes the keys: an Ed25519 authority, and a subject of <paramref name="type"/>.</summary>
    public static async Task<CredentialLab> CreateAsync(string type = "ed25519", int? bits = null)
    {
        var lab = new CredentialLab();
        lab._authority = await SshKeygen.NewKeyAsync(Path.Combine(lab._folder.Path, "ca"), type, bits);
        lab._subject = await SshKeygen.NewKeyAsync(Path.Combine(lab._folder.Path, "subject"), type, bits);
        return lab;
    }

    /// <summary>Issues a certificate for the subject's key with <c>ssh-keygen -s</c> and these options, and reads it.</summary>
    public async Task<Credential> IssueAsync(params string[] options) => Credential.Parse(await IssueTextAsync(options));

    /// <summary>Issues a certificate for the subject's key with <c>ssh-keygen -s</c>, and returns its text.</summary>
    public async Task<string> IssueTextAsync(params string[] options)
    {
        // Each certificate signs its own copy of the key, so each has its own -cert.pub.
        var key = Path.Combine(_folder.Path, $"issued{++_issued}");
        File.Copy(_subject + ".pub", key + ".pub");
        var (code, output) = await SshKeygen.Run(["-q", "-s", _authority, .. options, key + ".pub"]);
        return code == 0
            ? File.ReadAllText(key + "-cert.pub")
            : throw new InvalidOperationException($"ssh-keygen couldn't issue a certificate: {output}");
    }

    public void Dispose() => _folder.Dispose();
}
