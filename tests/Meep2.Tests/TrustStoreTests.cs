using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Shared;
using Meep2.Tests.Support;
using PublicKey = Meep2.Security.PublicKey;

namespace Meep2.Tests;

public class TrustStoreTests
{
    [Test]
    public async Task A_store_that_isnt_there_is_empty_and_not_set_up()
    {
        using var home = new TemporaryFolder();
        var store = TrustStore.Load(TrustFolder.In(home.Path));

        await Assert.That(store.IsSetUp).IsFalse();
        await Assert.That(store.Authorities).IsEmpty();
        await Assert.That(store.Roots).IsEmpty();
        await Assert.That(store.Grants).IsEmpty();
        await Assert.That(store.Permissions.All).IsEmpty();
        await Assert.That(store.Revocations.Entries).IsEmpty();
        await Assert.That(store.Version).IsEqualTo(1L);
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Its_folders_and_files_are_kept_to_the_user()
    {
        Skip.When(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        using var key = SshKeyPair.CreateEd25519("home");
        folder.AddAuthority("home", key.Public, [TrustStore.CredentialNamespace]);

        foreach (var path in new[] { folder.Path, folder.Roots, folder.RevocationLists })
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(PrivateFolders.Folder).Because(path);
        foreach (var path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(PrivateFolders.File).Because(path);
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_store_others_could_change_or_whose_authority_key_others_could_read_is_refused()
    {
        Skip.When(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();

        File.SetUnixFileMode(folder.Authorities, PrivateFolders.File | UnixFileMode.OtherWrite);
        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("can be changed by other users");
        File.SetUnixFileMode(folder.Authorities, PrivateFolders.File);

        File.SetUnixFileMode(folder.Roots, PrivateFolders.Folder | UnixFileMode.OtherWrite);
        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("roots can be changed");
        File.SetUnixFileMode(folder.Roots, PrivateFolders.Folder);

        File.WriteAllText(folder.AuthorityKey, "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        File.SetUnixFileMode(folder.AuthorityKey, PrivateFolders.File | UnixFileMode.OtherRead);
        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("others can read it");
    }

    [Test]
    public async Task Authorities_are_trusted_for_their_namespaces_and_found_by_name_or_by_what_they_signed()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var granter = SshKeyPair.CreateEd25519("granter");
        folder.AddAuthority("ca", PublicKey.Parse("ca", lab.AuthorityPublicKey), [TrustStore.CredentialNamespace]);
        folder.AddAuthority("granter", granter.Public, [TrustStore.GrantNamespace]);
        var credential = await lab.IssueAsync("-I", "alice", "-z", "7");
        var grant = Sign(granter, new GrantTerms(credential, PermissionName.Parse("ReadNotes")));
        var caGrant = await GrantSignedByLabAuthorityAsync(lab, credential);

        var store = TrustStore.Load(folder);

        await Assert.That(store.Authorities.Select(key => key.Name)).IsEquivalentTo(["ca", "granter"]);
        await Assert.That(store.Authority("ca")!.Namespaces).IsEquivalentTo([TrustStore.CredentialNamespace]);
        await Assert.That(store.AuthorityOf(credential)!.Name).IsEqualTo("ca");
        await Assert.That(store.AuthorityOf(grant)!.Name).IsEqualTo("granter");
        await Assert.That(store.AuthorityOf(caGrant)).IsNull().Because("ca may issue credentials, not sign grants");
        await Assert.That(store.Authority("nobody")).IsNull();
        // TrustedKeys reads the same file as an allowed-signers file.
        await Assert.That(TrustedKeys.Load(folder.Authorities).Find(granter.Public, Grant.Namespace)).IsNotNull();
    }

    [Test]
    public async Task Two_authorities_with_one_name_are_refused()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var one = SshKeyPair.CreateEd25519("home");
        using var two = SshKeyPair.CreateEd25519("home");
        folder.AddAuthority("home", one.Public, [TrustStore.CredentialNamespace]);
        folder.AddAuthority("home", two.Public, [TrustStore.CredentialNamespace]);

        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("two authorities called home");
        await Assert.That(() => folder.AddAuthority("Home CA", one.Public, [])).Throws<FormatException>();
    }

    [Test]
    public async Task Grants_recorded_apply_to_their_own_credential_alone()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var granter = SshKeyPair.CreateEd25519("granter");
        var credential = await lab.IssueAsync("-I", "alice", "-z", "7");
        var renewed = await lab.IssueAsync("-I", "alice", "-z", "8");
        var read = Sign(granter, new GrantTerms(credential, PermissionName.Parse("ReadNotes")));
        var write = Sign(granter, new GrantTerms(credential, PermissionName.Parse("meep.files.write:~/notes/**")));
        folder.RecordGrant(read);
        folder.RecordGrant(write);

        var store = TrustStore.Load(folder);

        await Assert.That(store.Grants.Count).IsEqualTo(2);
        await Assert.That(store.GrantsFor(credential).Select(g => g.Terms.Id)).IsEquivalentTo([read.Terms.Id, write.Terms.Id]);
        await Assert.That(store.GrantsFor(renewed)).IsEmpty();
        await Assert.That(File.ReadAllText(folder.Grants)).Contains("# ReadNotes for SHA256:");
    }

    [Test]
    public async Task Credentials_are_revoked_by_serial_key_or_authority_and_grants_by_ID_or_signer()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var granter = SshKeyPair.CreateEd25519("granter");
        var seven = await lab.IssueAsync("-I", "alice", "-z", "7");
        var eight = await lab.IssueAsync("-I", "alice", "-z", "8");
        var grant = Sign(granter, new GrantTerms(eight, PermissionName.Parse("ReadNotes")));
        var other = Sign(granter, new GrantTerms(eight, PermissionName.Parse("WriteNotes")));

        folder.Revoke(Revocation.Of(seven), "alice's first");
        folder.Revoke(new Revocation(RevocationKind.Grant, grant.Terms.Id.ToString()), "a grant");
        var store = TrustStore.Load(folder);

        await Assert.That(store.Revocations.WhyRevoked(seven)).IsEqualTo("alice's credential, serial 7, is revoked.");
        await Assert.That(store.Revocations.WhyRevoked(eight)).IsNull();
        await Assert.That(store.Revocations.WhyRevoked(grant)).Contains("is revoked");
        await Assert.That(store.Revocations.WhyRevoked(other)).IsNull();
        await Assert.That(File.ReadAllText(folder.Revoked)).Contains("# alice's first, revoked ");

        folder.Revoke(new Revocation(RevocationKind.Key, seven.KeyFingerprint), "alice's key");
        folder.Revoke(new Revocation(RevocationKind.Key, granter.Public.Fingerprint), "the granter");
        store = TrustStore.Load(folder);
        await Assert.That(store.Revocations.WhyRevoked(eight)).Contains("alice's key, SHA256:");
        await Assert.That(store.Revocations.WhyRevoked(other)).Contains("The key that signed the grant");

        File.WriteAllText(folder.Revoked, $"key {seven.AuthorityFingerprint}\n");
        await Assert.That(TrustStore.Load(folder).Revocations.WhyRevoked(eight)).Contains("The key of the authority that issued alice's credential");
    }

    [Test]
    public async Task A_credential_with_no_serial_can_only_be_revoked_by_its_key()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();

        await Assert.That(() => Revocation.Of(lab.IssueAsync("-I", "alice").GetAwaiter().GetResult())).Throws<FormatException>().WithMessageContaining("only be revoked by its key");
        await Assert.That(() => Revocation.Parse("serial SHA256:abc 0")).Throws<FormatException>();
        await Assert.That(() => Revocation.Parse("key abc")).Throws<FormatException>();
        await Assert.That(Revocation.Parse("certificate " + new string('a', 64)).Value).IsEqualTo(new string('A', 64));
    }

    [Test]
    public async Task A_key_revocation_list_from_ssh_keygen_revokes_by_serial_range_bitmap_key_ID_key_and_hash()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var other = await CredentialLab.CreateAsync();
        // Many serials close together, which ssh-keygen writes as a bitmap, and a long run, which it writes as a range.
        var dense = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"serial: {100 + 2 * i}"));
        var krl = await MakeKrlAsync(lab, $"serial: 5\nserial: 10-20\nserial: 1000-1000000\n{dense}\nid: mallory\n");
        var subject = PublicKey.Parse("subject", lab.SubjectPublicKey);

        async Task<string?> Why(CredentialLab issuer, params string[] options) => krl.WhyRevoked(await issuer.IssueAsync(options));

        await Assert.That(await Why(lab, "-I", "alice", "-z", "5")).IsEqualTo("its serial, 5, is revoked");
        await Assert.That(await Why(lab, "-I", "alice", "-z", "15")).IsEqualTo("its serial, 15, is revoked");
        await Assert.That(await Why(lab, "-I", "alice", "-z", "20")).IsNotNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "21")).IsNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "102")).IsNotNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "103")).IsNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "178")).IsNotNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "500000")).IsNotNull();
        await Assert.That(await Why(lab, "-I", "alice", "-z", "1000001")).IsNull();
        await Assert.That(await Why(lab, "-I", "mallory", "-z", "1")).IsEqualTo("its key ID, mallory, is revoked");
        await Assert.That(await Why(other, "-I", "alice", "-z", "5")).IsNull().Because("another authority's serial 5 isn't revoked");

        // Serials far apart, which ssh-keygen writes as a list.
        var sparse = await MakeKrlAsync(lab, "serial: 5\nserial: 7000000000000\nserial: 9000000000000\n");
        await Assert.That(sparse.WhyRevoked(await lab.IssueAsync("-I", "alice", "-z", "7000000000000"))).IsNotNull();
        await Assert.That(sparse.WhyRevoked(await lab.IssueAsync("-I", "alice", "-z", "7000000000001"))).IsNull();

        var byKey = await MakeKrlAsync(lab, $"key: {lab.SubjectPublicKey.Trim()}\n");
        var byHash = await MakeKrlAsync(lab, $"hash: {subject.Fingerprint}\n");
        var byAuthority = await MakeKrlAsync(lab, $"key: {lab.AuthorityPublicKey.Trim()}\n");
        var credential = await lab.IssueAsync("-I", "alice", "-z", "1");
        await Assert.That(byKey.WhyRevoked(credential)).IsEqualTo("its key is revoked");
        await Assert.That(byHash.WhyRevoked(credential)).IsEqualTo("its key is revoked by its SHA-256 hash");
        await Assert.That(byAuthority.WhyRevoked(credential)).IsEqualTo("its authority's key is revoked");
        await Assert.That(byKey.RevokesKey(subject.Blob)).IsTrue();
        await Assert.That(() => SshRevocationList.Parse([1, 2, 3])).Throws<FormatException>();
    }

    [Test]
    public async Task A_key_revocation_list_revokes_a_credential_named_only_by_its_fingerprints_and_serial()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var subject = PublicKey.Parse("alice", lab.SubjectPublicKey);
        var nine = CredentialName.Of(await lab.IssueAsync("-I", "alice", "-z", "9"));
        var ten = CredentialName.Of(await lab.IssueAsync("-I", "alice", "-z", "10"));

        var bySerial = await MakeKrlAsync(lab, "serial: 9\n");
        await Assert.That(bySerial.WhyRevoked(nine)).IsEqualTo("its serial, 9, is revoked");
        await Assert.That(bySerial.WhyRevoked(ten)).IsNull();
        await Assert.That(bySerial.WhyRevoked(nine with { Authority = "SHA256:another" })).IsNull();
        await Assert.That((await MakeKrlAsync(lab, $"key: {lab.SubjectPublicKey.Trim()}\n")).WhyRevoked(ten)).IsEqualTo("its key is revoked");
        await Assert.That((await MakeKrlAsync(lab, $"hash: {subject.Fingerprint}\n")).WhyRevoked(ten)).IsEqualTo("its key is revoked");
        await Assert.That((await MakeKrlAsync(lab, $"key: {lab.AuthorityPublicKey.Trim()}\n")).WhyRevoked(ten)).IsEqualTo("its authority's key is revoked");
    }

    [Test]
    public async Task Tidy_takes_out_what_is_no_longer_needed_and_keeps_the_rest()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        using var granter = SshKeyPair.CreateEd25519("granter");
        var now = DateTime.UtcNow;
        var past = now.AddDays(-1);
        var future = now.AddDays(30);
        var bob = await lab.IssueAsync("-I", "bob", "-z", "30");
        var carol = await lab.IssueAsync("-I", "carol", "-z", "31");
        var authority = bob.AuthorityFingerprint;
        var oldAdmin = new CredentialName("SHA256:oldadmin", 40, authority);
        var goneAdmin = new CredentialName("SHA256:goneadmin", 41, authority);
        var liveAdmin = new CredentialName("SHA256:liveadmin", 44, authority);

        var expired = Sign(granter, new GrantTerms(bob, PermissionName.Parse("A")) { ValidBefore = past, AuthorisedBy = [goneAdmin] });
        var live = Sign(granter, new GrantTerms(bob, PermissionName.Parse("B")) { ValidBefore = future, AuthorisedBy = [liveAdmin] });
        var revoked = Sign(granter, new GrantTerms(bob, PermissionName.Parse("C")) { ValidBefore = future });
        var toRevoked = Sign(granter, new GrantTerms(carol, PermissionName.Parse("D")));
        var fallen = Sign(granter, new GrantTerms(bob, PermissionName.Parse("E")) { AuthorisedBy = [oldAdmin] });
        foreach (var grant in new[] { expired, live, revoked, toRevoked, fallen })
            folder.RecordGrant(grant);

        folder.Revoke(Revoking.Grant(revoked));
        folder.Revoke(Revoking.Grant(expired.Terms.Id));
        folder.Revoke(Revoking.Credential(carol) with { Until = future });
        folder.Revoke(new Revocation(RevocationKind.Serial, authority, 40), "the old admin", past);
        folder.Revoke(new Revocation(RevocationKind.Serial, authority, 41), "the gone admin", past);
        folder.Revoke(new Revocation(RevocationKind.Serial, authority, 42), "someone long gone", past);
        folder.Revoke(new Revocation(RevocationKind.Serial, authority, 43), "someone whose end isn't known");
        folder.Revoke(new Revocation(RevocationKind.Key, "SHA256:somekey"), "a key", past);
        folder.Revoke(new Revocation(RevocationKind.Certificate, new string('A', 64)), "an old certificate", past);
        await Assert.That(File.ReadAllText(folder.Revoked)).Contains($"the old admin, revoked {now:yyyy-MM-dd}");
        await Assert.That(File.ReadAllText(folder.Revoked)).Contains($"; {StoreTidying.Until(past)}");

        var tidied = folder.Tidy(now);

        // Grants: the expired one, the revoked one, and the one whose credential is revoked go. The one that fell with
        // its authoriser stays, so the authoriser's revocation stays too.
        await Assert.That(tidied.Grants.Count).IsEqualTo(3);
        await Assert.That(tidied.Grants.Any(g => g.StartsWith("A for ", StringComparison.Ordinal) && g.EndsWith($"it expired at {past:yyyy-MM-dd HH:mm}Z", StringComparison.Ordinal))).IsTrue();
        await Assert.That(tidied.Grants.Any(g => g.StartsWith("C for ", StringComparison.Ordinal) && g.Contains("it's revoked", StringComparison.Ordinal))).IsTrue();
        await Assert.That(tidied.Grants.Any(g => g.StartsWith("D for ", StringComparison.Ordinal) && g.Contains("its credential is revoked", StringComparison.Ordinal))).IsTrue();
        var store = TrustStore.Load(folder);
        await Assert.That(store.Grants.Select(g => g.Terms.Permission.Name)).IsEquivalentTo(["B", "E"]);

        // Revocations: the expired grant's (from the grants file), the long-gone credential's and the old certificate's
        // go. The revoked grant's stays until it would have expired, the old admin's while a grant names it, and the
        // key's, the one whose end isn't known, and carol's, which hasn't expired, stay.
        await Assert.That(tidied.Revocations.Count).IsEqualTo(4);
        await Assert.That(store.Revocations.Entries).IsEquivalentTo([
            new Revocation(RevocationKind.Grant, revoked.Terms.Id.ToString()),
            Revocation.Of(carol),
            new Revocation(RevocationKind.Serial, authority, 40),
            new Revocation(RevocationKind.Serial, authority, 43),
            new Revocation(RevocationKind.Key, "SHA256:somekey"),
        ]);
        await Assert.That(tidied.Revocations.Any(r => r.StartsWith("the gone admin", StringComparison.Ordinal))).IsTrue();
        await Assert.That(store.Revocations.WhyRevoked(fallen)).Contains("falls with the credential that authorised it");

        // The files' own comments are kept, each entry's goes with it, and a second tidy finds nothing.
        await Assert.That(File.ReadAllText(folder.Grants)).StartsWith("# Grants recorded by Authorise");
        await Assert.That(File.ReadAllText(folder.Revoked)).StartsWith("# What's revoked:");
        await Assert.That(File.ReadAllText(folder.Revoked)).DoesNotContain("someone long gone");
        var again = folder.Tidy(now);
        await Assert.That(again.Grants.Count + again.Revocations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Store_lines_keep_what_isnt_an_entry_as_it_was()
    {
        var text = "# Header\n# more header\n# first's comment\nfirst\n\n# loose\n\nsecond\n# trailing\n";
        var lines = StoreLines.Parse(text);
        await Assert.That(lines.ToString()).IsEqualTo(text);
        await Assert.That(lines.Entries.Select(l => l.What)).IsEquivalentTo(["first's comment", "second"]);

        var removed = lines.RemoveWhere(line => line.Entry == "first" ? "it had to go" : null);
        await Assert.That(removed).IsEquivalentTo(["first's comment: it had to go"]);
        await Assert.That(lines.ToString()).IsEqualTo("# Header\n# more header\n\n# loose\n\nsecond\n# trailing\n");
        await Assert.That(StoreLines.Parse("no newline").ToString()).IsEqualTo("no newline\n");
        await Assert.That(StoreTidying.UntilIn(["# x, revoked 2026-11-25 10:00Z; needed until 2027-01-02T03:04:05Z"]))
            .IsEqualTo(new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await Assert.That(StoreTidying.Until(new DateTime(2027, 1, 2, 3, 4, 5, 1, DateTimeKind.Utc))).IsEqualTo("needed until 2027-01-02T03:04:06Z");
    }

    [Test]
    public async Task Revocation_lists_in_the_store_revoke_credentials_and_certificates()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        var krl = await File.ReadAllBytesAsync(await MakeKrlFileAsync(lab, "serial: 9\n"));
        using var pki = new TestPki();
        using var alice = pki.Issue("CN=alice", 0x1234);
        using var bob = pki.Issue("CN=bob", 0x5678);
        folder.AddRevocationList("ours.krl", krl);
        folder.AddRevocationList("ca.crl", pki.Crl(alice));

        var store = TrustStore.Load(folder);

        await Assert.That(store.Revocations.Lists).IsEquivalentTo(["ours.krl", "ca.crl"]);
        await Assert.That(store.Revocations.WhyRevoked(await lab.IssueAsync("-I", "alice", "-z", "9")))
            .IsEqualTo("alice's credential is revoked by ours.krl: its serial, 9, is revoked.");
        await Assert.That(store.Revocations.WhyRevoked(alice)).IsEqualTo("The certificate CN=alice, serial 1234, is revoked by ca.crl.");
        await Assert.That(store.Revocations.WhyRevoked(bob)).IsNull();
        await Assert.That(store.Revocations.NextUpdate(pki.Root.SubjectName)).IsNotNull();
        await Assert.That(store.Revocations.OutOfDateAt(DateTime.UtcNow)).IsEmpty();
        await Assert.That(store.Revocations.OutOfDateAt(DateTime.UtcNow.AddDays(8)).Keys).IsEquivalentTo(["ca.crl"]);

        folder.Revoke(new Revocation(RevocationKind.Certificate, bob.GetCertHashString(HashAlgorithmName.SHA256)), "bob");
        await Assert.That(TrustStore.Load(folder).Revocations.WhyRevoked(bob)).StartsWith("The certificate CN=bob, ");
        await Assert.That(() => folder.AddRevocationList("ca.txt", [1])).Throws<FormatException>();
        File.WriteAllBytes(Path.Combine(folder.RevocationLists, "broken.crl"), [1, 2, 3]);
        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("broken.crl");
    }

    [Test]
    public async Task Roots_are_read_from_their_folder_whatever_their_encoding()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var pki = new TestPki();
        using var other = new TestPki("CN=Other Root");
        folder.AddRoot("home", pki.Root.RawData);
        folder.Create();
        File.WriteAllBytes(Path.Combine(folder.Roots, "other.der"), other.Root.RawData);
        File.WriteAllText(Path.Combine(folder.Roots, "notes.txt"), "not a certificate");

        var store = TrustStore.Load(folder);

        await Assert.That(store.Roots.Select(root => root.Name)).IsEquivalentTo(["home", "other"]);
        await Assert.That(store.Roots.Single(root => root.Name == "home").Certificate.Thumbprint).IsEqualTo(pki.Root.Thumbprint);
        await Assert.That(store.RootCollection().Count).IsEqualTo(2);
        await Assert.That(() => folder.AddRoot("home", pki.Root.RawData)).Throws<IOException>();
        await Assert.That(() => folder.AddRoot("../escape", pki.Root.RawData)).Throws<FormatException>();
    }

    [Test]
    public async Task The_permissions_file_is_read_with_its_problems_and_a_line_that_cant_be_read_is_named()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        using var key = SshKeyPair.CreateEd25519("home");
        folder.AddAuthority("home", key.Public, [TrustStore.CredentialNamespace]);
        File.AppendAllText(folder.Permissions, "#permission ReadNotes authority=home\n#permission WriteNotes authority=work requires=ReadNotes\n");

        var store = TrustStore.Load(folder);

        await Assert.That(store.Permissions.All.Select(p => p.Name.Name)).IsEquivalentTo(["ReadNotes", "WriteNotes"]);
        await Assert.That(store.Problems).IsEquivalentTo(["WriteNotes: the authority work isn't in the trust store."]);

        File.AppendAllText(folder.Permissions, "#permission Broken requires=\n");
        await Assert.That(() => TrustStore.Load(folder)).Throws<TrustStoreException>().WithMessageContaining("permissions.meep: Line 7:");
    }

    [Test]
    public async Task Reloading_raises_the_version_for_any_change_and_the_revocation_version_only_when_something_is_taken_away()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        using var one = SshKeyPair.CreateEd25519("one");
        using var two = SshKeyPair.CreateEd25519("two");
        var first = TrustStore.Load(folder);

        var same = first.Reload();
        folder.AddAuthority("one", one.Public, [TrustStore.CredentialNamespace]);
        var added = same.Reload();
        File.AppendAllText(folder.Permissions, "#permission ReadNotes\n");
        var defined = added.Reload();
        folder.Revoke(new Revocation(RevocationKind.Key, two.Public.Fingerprint), "two");
        var revoked = defined.Reload();
        File.WriteAllText(folder.Permissions, "#permission ReadNotes authority=one\n");
        var narrowed = revoked.Reload();
        File.WriteAllText(folder.Authorities, "");
        var removed = narrowed.Reload();

        await Assert.That(same).IsSameReferenceAs(first);
        await Assert.That(new[] { added, defined, revoked, narrowed, removed }.Select(s => s.Version)).IsEquivalentTo([2L, 3L, 4L, 5L, 6L]);
        await Assert.That(new[] { added, defined, revoked, narrowed, removed }.Select(s => s.RevocationVersion)).IsEquivalentTo([1L, 1L, 2L, 3L, 4L]);
    }

    [Test]
    public async Task A_reload_that_cant_be_read_throws_and_the_store_before_it_stands()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        folder.Create();
        var store = TrustStore.Load(folder);
        File.AppendAllText(folder.Grants, "not a grant\n");

        await Assert.That(() => store.Reload()).Throws<TrustStoreException>().WithMessageContaining("grants, line 2");
        await Assert.That(store.Grants).IsEmpty();
    }

    [Test]
    public async Task The_watcher_reloads_when_a_file_changes_and_keeps_the_store_when_it_cant()
    {
        using var home = new TemporaryFolder();
        var folder = TrustFolder.In(home.Path);
        var log = new SharedText();
        using var watcher = new TrustStoreWatcher(folder, line => log.WriteLine(line));
        TrustStore? reloaded = null;
        watcher.Reloaded += store => Volatile.Write(ref reloaded, store);
        using var key = SshKeyPair.CreateEd25519("home");

        folder.AddAuthority("home", key.Public, [TrustStore.CredentialNamespace]);
        await Wait.Until(() => watcher.Current.Authorities.Count == 1);
        // The event follows the swap, and the files may settle in more than one reload.
        await Wait.Until(() => Volatile.Read(ref reloaded)?.Authorities.Count == 1);

        File.AppendAllText(folder.Permissions, "#permission ??\n");
        await Wait.Until(() => log.ToString().Contains("can't be read", StringComparison.Ordinal));
        await Assert.That(watcher.Current.Authorities.Count).IsEqualTo(1);

        File.WriteAllText(folder.Permissions, "#permission ReadNotes authority=nobody\n");
        await Wait.Until(() => watcher.Current.Permissions.All.Count == 1);
        await Wait.Until(() => log.ToString().Contains("ReadNotes: the authority nobody", StringComparison.Ordinal));
    }

    // A grant of these terms, signed by key.
    private static Grant Sign(SshKeyPair key, GrantTerms terms) =>
        Grant.Signed(terms, Unarmor(key.Sign(terms.ToSignedData(), Grant.Namespace).ToArmored()));

    // A grant signed with the lab's certificate authority key, by ssh-keygen.
    private static async Task<Grant> GrantSignedByLabAuthorityAsync(CredentialLab lab, Credential credential)
    {
        var terms = new GrantTerms(credential, PermissionName.Parse("ReadNotes"));
        var data = Path.Combine(lab.Folder, $"terms-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(data, terms.ToSignedData());
        var (code, said) = await SshKeygen.Run(["-Y", "sign", "-n", Grant.Namespace, "-f", lab.AuthorityPublicKeyFile[..^4], data]);
        return code == 0 ? Grant.Signed(terms, Unarmor(await File.ReadAllTextAsync(data + ".sig"))) : throw new InvalidOperationException(said);
    }

    private static async Task<SshRevocationList> MakeKrlAsync(CredentialLab lab, string spec) =>
        SshRevocationList.Parse(await File.ReadAllBytesAsync(await MakeKrlFileAsync(lab, spec)));

    // A KRL made by ssh-keygen -k from a specification, with the lab's authority for serials and key IDs.
    private static async Task<string> MakeKrlFileAsync(CredentialLab lab, string spec)
    {
        var name = Path.Combine(lab.Folder, $"revoked-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(name + ".spec", spec);
        var (code, said) = await SshKeygen.Run(["-q", "-k", "-f", name + ".krl", "-s", lab.AuthorityPublicKeyFile, name + ".spec"]);
        return code == 0 ? name + ".krl" : throw new InvalidOperationException($"ssh-keygen couldn't make a KRL: {said}");
    }

    private static byte[] Unarmor(string armored) =>
        Convert.FromBase64String(string.Concat(armored.Split('\n').Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal))));
}

/// <summary>A root CA that issues certificates and CRLs, for tests.</summary>
internal sealed class TestPki : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestPki(string subject = "CN=Test Root")
    {
        var request = new CertificateRequest(subject, _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>The root, with its private key.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>A leaf certificate issued by the root, with this serial.</summary>
    public X509Certificate2 Issue(string subject, int serial)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [(byte)(serial >> 8), (byte)serial]);
    }

    /// <summary>A CRL from the root revoking these certificates, as DER.</summary>
    public byte[] Crl(params X509Certificate2[] revoked) => Crl(DateTimeOffset.UtcNow.AddDays(7), revoked);

    /// <summary>A CRL from the root revoking these certificates, due to be replaced at <paramref name="nextUpdate"/>, as DER.</summary>
    public byte[] Crl(DateTimeOffset nextUpdate, params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var certificate in revoked)
            builder.AddEntry(certificate);
        return builder.Build(Root, 1, nextUpdate, HashAlgorithmName.SHA256, thisUpdate: nextUpdate.AddDays(-7));
    }

    public void Dispose()
    {
        Root.Dispose();
        _key.Dispose();
    }
}
