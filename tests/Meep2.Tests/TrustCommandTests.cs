using System.Security.Cryptography;
using Meep2.Cli;
using Meep2.Plugins;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Shared;
using Meep2.Tests.Support;
using PublicKey = Meep2.Security.PublicKey;

namespace Meep2.Tests;

public class TrustCommandTests
{
    [Test]
    public async Task Init_makes_this_machines_authority_and_trusts_it_for_credentials_and_grants_once()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);

        var made = await Cli(home.Path, "trust", "init", "--name", "studio");
        var again = await Cli(home.Path, "trust", "init");
        var store = TrustStore.Load(folder);

        await Assert.That(made.Code).IsEqualTo(0).Because(made.Error);
        await Assert.That(made.Output).Contains("This machine's authority is studio, with the key SHA256:");
        await Assert.That(again.Code).IsEqualTo(0);
        await Assert.That(again.Output).Contains("is set up already");
        await Assert.That(store.IsSetUp).IsTrue();
        await Assert.That(store.OwnAuthority!.Type).IsEqualTo("ssh-ed25519");
        await Assert.That(store.Authority("studio")!.SameKeyAs(store.OwnAuthority)).IsTrue();
        await Assert.That(store.Authority("studio")!.Namespaces).IsEquivalentTo([TrustStore.CredentialNamespace, TrustStore.GrantNamespace]);
        await Assert.That(File.ReadAllText(folder.AuthorityKey)).StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----");
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(folder.Path)).IsEqualTo(PrivateFolders.Folder);
            foreach (var file in Directory.EnumerateFiles(folder.Path))
                await Assert.That(File.GetUnixFileMode(file)).IsEqualTo(PrivateFolders.File).Because(file);
        }
        await Assert.That((await Cli(home.Path, "trust", "init", "--name", "My Mac")).Code).IsEqualTo(2);
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Init_narrows_a_meep_folder_others_can_reach_into_and_says_so()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        Skip.When(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        using var outer = new TemporaryFolder();
        // As a umask of 002 makes it, with others able to write to it too, so no group rule lets it through (Q284).
        var home = Directory.CreateDirectory(Path.Combine(outer.Path, ".meep"), (UnixFileMode)Convert.ToInt32("777", 8)).FullName;
        File.SetUnixFileMode(home, (UnixFileMode)Convert.ToInt32("777", 8));

        var made = await Cli(home, "trust", "init", "--name", "studio");

        await Assert.That(made.Code).IsEqualTo(0).Because(made.Error);
        await Assert.That(made.Output).Contains($"Made {home} yours alone (700)");
        await Assert.That(File.GetUnixFileMode(home)).IsEqualTo(PrivateFolders.Folder);
        await Assert.That(TrustStore.Load(TrustFolder.In(home)).IsSetUp).IsTrue();
    }

    [Test]
    public async Task Issue_makes_a_credential_signed_by_this_machines_authority_with_the_next_serial()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var home = new TemporaryFolder();
        using var lab = await CredentialLab.CreateAsync();
        var key = lab.SubjectPublicKeyFile;
        var before = await Cli(home.Path, "trust", "issue", key);
        await Cli(home.Path, "trust", "init");
        File.AppendAllText(TrustFolder.In(home.Path).Permissions, "#permission ReadNotes\n");

        var issued = await Cli(home.Path, "trust", "issue", key, "--id", "alice", "--principals", "alice,al",
            "--permission", "ReadNotes", "--permission", "meep.files.write:~/notes/**", "--permission", "WriteNotes", "--permission", "meep.file.read",
            "--out", Path.Combine(lab.Folder, "alice.credential"));
        var second = await Cli(home.Path, "trust", "issue", key, "--valid", "always", "--bearer");
        var clash = await Cli(home.Path, "trust", "issue", key);

        await Assert.That(before.Code).IsEqualTo(1);
        await Assert.That(before.Error).Contains("meep trust init");
        await Assert.That(issued.Code).IsEqualTo(0).Because(issued.Error);
        await Assert.That(issued.Output).Contains("Issued alice a credential, serial 1, until ");
        await Assert.That(issued.Output).Contains("WriteNotes isn't defined");
        await Assert.That(issued.Output).Contains("Meep has no permission called meep.file.read");
        await Assert.That(issued.Output).DoesNotContain("ReadNotes isn't defined");
        await Assert.That(issued.Output).DoesNotContain("meep.files.write isn't defined").Because("Meep defines its own (Q279)");

        var store = TrustStore.Load(TrustFolder.In(home.Path));
        var alice = Credential.Parse(File.ReadAllText(Path.Combine(lab.Folder, "alice.credential")));
        await Assert.That(alice.KeyId).IsEqualTo("alice");
        await Assert.That(alice.Serial).IsEqualTo(1UL);
        await Assert.That(alice.Principals).IsEquivalentTo(["alice", "al"]);
        await Assert.That(alice.Permissions.Select(p => p.ToString())).IsEquivalentTo(["ReadNotes", "meep.files.write:~/notes/**", "WriteNotes", "meep.file.read"]);
        await Assert.That(alice.Extensions.Keys).IsEquivalentTo(["ReadNotes@meep", "meep.files.write@meep", "WriteNotes@meep", "meep.file.read@meep"]).Because("-O clear leaves out SSH's own");
        await Assert.That(alice.ValidBefore).IsNotNull();
        await Assert.That(alice.PermitsBearer).IsFalse();
        await Assert.That(alice.KeyFingerprint).IsEqualTo(PublicKey.Parse("subject", lab.SubjectPublicKey).Fingerprint);
        await Assert.That(store.AuthorityOf(alice)).IsNotNull();
        await Assert.That(store.OwnAuthority!.Verify(alice.SignedData.ToArray(), alice.Signature.ToArray())).IsTrue();

        await Assert.That(second.Code).IsEqualTo(0).Because(second.Error);
        var beside = Credential.Parse(File.ReadAllText(Path.ChangeExtension(key, null) + ".credential"));
        await Assert.That(beside.Serial).IsEqualTo(2UL);
        await Assert.That(beside.ValidBefore).IsNull();
        await Assert.That(beside.PermitsBearer).IsTrue();
        await Assert.That(beside.KeyId).IsEqualTo(lab.SubjectPublicKey.Trim().Split(' ')[2]).Because("the key's comment is the default key ID");
        await Assert.That(clash.Code).IsEqualTo(1);
        await Assert.That(clash.Error).Contains("already");
        await Assert.That(Directory.EnumerateDirectories(TrustFolder.In(home.Path).Path, "issuing-*")).IsEmpty();
    }

    [Test]
    [Arguments("--permission 1bad")]
    [Arguments("--id")]
    [Arguments("--colour blue")]
    public async Task Issue_refuses_what_it_doesnt_understand(string options)
    {
        using var home = new TemporaryFolder();
        var result = await Cli(home.Path, ["trust", "issue", "key.pub", .. options.Split(' ')]);

        await Assert.That(result.Code).IsEqualTo(2);
    }

    [Test]
    public async Task Add_authority_trusts_a_key_from_a_file_or_the_command_line_once_by_name()
    {
        using var home = new TemporaryFolder();
        using var ca = SshKeyPair.CreateEd25519("ca");
        using var friend = SshKeyPair.CreateEd25519("friend");
        var file = home.Write("ca.pub", ca.Public.Text + " ca@example\n");

        var fromFile = await Cli(home.Path, "trust", "add-authority", "work", file);
        var again = await Cli(home.Path, "trust", "add-authority", "other", file);
        var sameName = await Cli(home.Path, ["trust", "add-authority", "work", .. friend.Public.Text.Split(' ')]);
        var grants = await Cli(home.Path, ["trust", "add-authority", "friend", .. friend.Public.Text.Split(' '), "--grants"]);
        var badName = await Cli(home.Path, ["trust", "add-authority", "a=b", .. friend.Public.Text.Split(' ')]);
        var privateKey = home.Write("id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        var wrongHalf = await Cli(home.Path, "trust", "add-authority", "x", privateKey);

        await Assert.That(fromFile.Output).StartsWith($"Credentials issued by work's ssh-ed25519 key {ca.Public.Fingerprint} can be verified now.");
        await Assert.That(again.Output).Contains("already, as the authority work");
        await Assert.That(sameName.Code).IsEqualTo(2);
        await Assert.That(sameName.Error).Contains("There's an authority called work already");
        await Assert.That(grants.Output).Contains("and grants it signs");
        await Assert.That(badName.Code).IsEqualTo(2);
        await Assert.That(wrongHalf.Error).Contains("is a private key");

        var store = TrustStore.Load(TrustFolder.In(home.Path));
        await Assert.That(store.Authorities.Select(k => k.Name)).IsEquivalentTo(["work", "friend"]);
        await Assert.That(store.Authority("work")!.MaySignFor(TrustStore.GrantNamespace)).IsFalse();
        await Assert.That(store.Authority("friend")!.MaySignFor(TrustStore.GrantNamespace)).IsTrue();
    }

    [Test]
    public async Task Add_root_trusts_a_CA_certificate_and_refuses_one_that_isnt()
    {
        using var home = new TemporaryFolder();
        using var pki = new TestPki();
        using var leaf = pki.Issue("CN=alice", 1);
        var root = home.Write("Home Root.pem", pki.Root.ExportCertificatePem());
        var der = home.Write("leaf.cer", leaf.RawData);

        var added = await Cli(home.Path, "trust", "add-root", root);
        var again = await Cli(home.Path, "trust", "add-root", root, "another");
        var refused = await Cli(home.Path, "trust", "add-root", der);
        var missing = await Cli(home.Path, "trust", "add-root", home.Write("x.txt", "nothing"));

        await Assert.That(added.Code).IsEqualTo(0).Because(added.Error);
        await Assert.That(added.Output).StartsWith("Certificates that chain to CN=Test Root can be verified now.");
        await Assert.That(again.Output).Contains("already, as the root Home-Root");
        await Assert.That(refused.Code).IsEqualTo(1);
        await Assert.That(refused.Error).Contains("isn't a CA's certificate");
        await Assert.That(missing.Code).IsEqualTo(1);
        await Assert.That(TrustStore.Load(TrustFolder.In(home.Path)).Roots.Single().Name).IsEqualTo("Home-Root");
    }

    [Test]
    public async Task Revoke_works_out_what_its_given_and_says_so()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var home = new TemporaryFolder();
        using var lab = await CredentialLab.CreateAsync();
        using var pki = new TestPki();
        using var leaf = pki.Issue("CN=alice", 1);
        var credential = home.Write("alice-cert.pub", await lab.IssueTextAsync("-I", "alice", "-z", "3"));
        var unserialled = home.Write("bob-cert.pub", await lab.IssueTextAsync("-I", "bob"));
        var grant = Guid.CreateVersion7();
        var krl = home.Write("ours.krl", [.. "SSHKRL\n\0"u8, 0, 0, 0, 1, .. new byte[24], 0, 0, 0, 0, 0, 0, 0, 0]);
        var crl = home.Write("home.crl", pki.Crl(leaf));

        var bySerial = await Cli(home.Path, "trust", "revoke", credential);
        var twice = await Cli(home.Path, "trust", "revoke", credential);
        var byKey = await Cli(home.Path, "trust", "revoke", unserialled, "--key");
        var noSerial = await Cli(home.Path, "trust", "revoke", unserialled);
        var key = await Cli(home.Path, "trust", "revoke", lab.AuthorityPublicKeyFile);
        var fingerprint = await Cli(home.Path, "trust", "revoke", "SHA256:abcdef");
        var byGrant = await Cli(home.Path, "trust", "revoke", grant.ToString());
        var certificate = await Cli(home.Path, "trust", "revoke", home.Write("leaf.pem", leaf.ExportCertificatePem()));
        var addedKrl = await Cli(home.Path, "trust", "revoke", krl);
        var addedCrl = await Cli(home.Path, "trust", "revoke", crl);
        var privateKey = await Cli(home.Path, "trust", "revoke", home.Write("id", "-----BEGIN OPENSSH PRIVATE KEY-----\n"));
        var nothing = await Cli(home.Path, "trust", "revoke");

        await Assert.That(bySerial.Output).StartsWith("Revoked alice's credential, serial 3 from SHA256:");
        await Assert.That(twice.Output).StartsWith("That's revoked already: alice's credential");
        await Assert.That(byKey.Output).StartsWith("Revoked bob's key SHA256:");
        await Assert.That(noSerial.Code).IsEqualTo(2);
        await Assert.That(noSerial.Error).Contains("only be revoked by its key");
        await Assert.That(key.Output).StartsWith("Revoked the key SHA256:");
        await Assert.That(fingerprint.Output).StartsWith("Revoked the key SHA256:abcdef");
        await Assert.That(byGrant.Output).StartsWith($"Revoked the grant {grant}");
        await Assert.That(certificate.Output).StartsWith("Revoked the certificate CN=alice");
        await Assert.That(addedKrl.Output).StartsWith("Added the key revocation list ours.krl, version 0");
        await Assert.That(addedCrl.Output).StartsWith("Added the certificate revocation list home.crl, from CN=Test Root");
        await Assert.That(privateKey.Code).IsEqualTo(2);
        await Assert.That(nothing.Code).IsEqualTo(2);

        var store = TrustStore.Load(TrustFolder.In(home.Path));
        await Assert.That(store.Revocations.Entries.Select(e => e.Kind))
            .IsEquivalentTo([RevocationKind.Serial, RevocationKind.Key, RevocationKind.Key, RevocationKind.Key, RevocationKind.Grant, RevocationKind.Certificate]);
        await Assert.That(store.Revocations.Lists).IsEquivalentTo(["ours.krl", "home.crl"]);
        await Assert.That(store.Revocations.WhyRevoked(leaf)).IsNotNull();
    }

    [Test]
    public async Task Tidy_says_what_it_took_out_and_revoking_a_recorded_grant_by_ID_says_until_when()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var home = new TemporaryFolder();
        using var lab = await CredentialLab.CreateAsync();
        var folder = TrustFolder.In(home.Path);
        await Assert.That((await Cli(home.Path, "trust", "init", "--name", "studio")).Code).IsEqualTo(0);
        var bob = await lab.IssueAsync("-I", "bob", "-z", "3");
        var until = DateTime.UtcNow.AddDays(3);
        var grant = folder.SignGrant(new GrantTerms(bob, PermissionName.Parse("ReadNotes")) { ValidBefore = until });
        folder.RecordGrant(grant);
        folder.RecordGrant(folder.SignGrant(new GrantTerms(bob, PermissionName.Parse("WriteNotes")) { ValidBefore = DateTime.UtcNow.AddMinutes(-1) }));

        var first = await Cli(home.Path, "trust", "tidy");
        var revoked = await Cli(home.Path, "trust", "revoke", grant.Terms.Id.ToString());
        await Assert.That(revoked.Output).StartsWith($"Revoked the grant {grant.Terms.Id}, of ReadNotes");
        await Assert.That(File.ReadAllText(folder.Revoked)).Contains($"; needed until {until:yyyy-MM-dd}T");
        var tidied = await Cli(home.Path, "trust", "tidy");

        await Assert.That(first.Code).IsEqualTo(0).Because(first.Error);
        await Assert.That(first.Output).StartsWith("Took 1 line out of grants:\n  WriteNotes for ");
        await Assert.That(first.Output).Contains(": it expired at ");
        await Assert.That(tidied.Output).StartsWith("Took 1 line out of grants:\n  ReadNotes for ");
        await Assert.That(tidied.Output).Contains("it's revoked");
        await Assert.That(TrustStore.Load(folder).Revocations.Entries.Single().Value).IsEqualTo(grant.Terms.Id.ToString());
        await Assert.That((await Cli(home.Path, "trust", "tidy")).Output).IsEqualTo("Nothing in the trust store needed tidying.\n");
    }

    [Test]
    public async Task List_shows_everything_in_the_store_and_its_problems()
    {
        using var home = new TemporaryFolder();
        var empty = await Cli(home.Path, "trust", "list");
        using var ca = SshKeyPair.CreateEd25519("ca");
        using var pki = new TestPki();
        var folder = TrustFolder.In(home.Path);
        folder.AddAuthority("work", ca.Public, [TrustStore.CredentialNamespace]);
        folder.AddRoot("home", pki.Root.RawData);
        folder.Revoke(new Revocation(RevocationKind.Grant, Guid.Empty.ToString()), "nothing");
        folder.AddRevocationList("current.crl", pki.Crl());
        folder.AddRevocationList("old.crl", pki.Crl(DateTimeOffset.UtcNow.AddDays(-1)));
        File.AppendAllText(folder.Permissions, "#permission ReadNotes authority=work\n#permission WriteNotes requires=Missing\n");

        var listed = await Cli(home.Path, "trust", "list");

        await Assert.That(empty.Output).StartsWith("There's no trust store yet.");
        await Assert.That(listed.Code).IsEqualTo(0).Because(listed.Error);
        await Assert.That(listed.Output).Contains("This machine has no authority of its own");
        await Assert.That(listed.Output).Contains($"  work  {ca.Public.Fingerprint}  ssh-ed25519  meep-credential");
        await Assert.That(listed.Output).Contains("  home  CN=Test Root  until ");
        await Assert.That(listed.Output).Contains($"  grant {Guid.Empty}");
        await Assert.That(listed.Output).Contains("  the list current.crl\n".ReplaceLineEndings());
        await Assert.That(listed.Output).Contains("  the list old.crl, out of date since ");
        await Assert.That(listed.Output).Contains("Grants: none");
        await Assert.That(listed.Output).Contains("  #permission ReadNotes authority=work");
        await Assert.That(listed.Output).Contains("Problems:\n  WriteNotes: it requires Missing, which isn't defined.".ReplaceLineEndings());
    }

    [Test]
    public async Task A_store_that_cant_be_read_is_reported_by_every_command()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        File.AppendAllText(folder.Revoked, "nonsense\n");

        foreach (var args in new[] { new[] { "trust", "list" }, ["trust", "revoke", "SHA256:abc"], ["trust", "init"] })
        {
            var result = await Cli(home.Path, args);
            await Assert.That(result.Code).IsEqualTo(1).Because(string.Join(' ', args));
            await Assert.That(result.Error).Contains("revoked, line 2");
        }
    }

    private static async Task<(int Code, string Output, string Error)> Cli(string home, params string[] args)
    {
        var output = new SharedText();
        var error = new SharedText();
        var code = await Wait.For(MeepCommand.RunAsync(args, output, error, new PluginOptions { Home = home }, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }
}
