# Permissions, phase 2: the trust store and `meep trust`

Written 24 November 2026. Phase 2 of the plan in `permissions.md`: the trust store in `~/.meep/trust`, which phases 3–5 check against and write to, and the `meep trust` commands. The work is on the branch `permissions-phase-2`, made from `main` at 4f05ce8, not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q272, now answered. Only Q274 and Q276 changed anything in this phase; the rest are for phases 3, 5 and 6.

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 823 of 823 (777 before, and 46 new tests, counting each case of a test with arguments). Two full runs, both 823 of 823; none of the known flaky tests failed.
- No test touches the real `~/.meep`: each uses a temporary home folder, as the existing tests do.

## What was built

### The store's folder

`~/.meep/trust/`, with folders 700 and files 600, made with `PrivateFolders`:

| File | What it holds |
|---|---|
| `authority.key`, `authority.key.pub` | This machine's own Ed25519 authority key, made by `meep trust init` with `ssh-keygen`, unencrypted (Q273). |
| `authorities` | Keys trusted to issue credentials and sign grants, in `allowed_signers` form: `name namespaces="meep-credential,meep-grant" ssh-ed25519 AAAA…`. `TrustedKeys` and `PublicKey` read it as before. |
| `roots/` | Trusted X.509 roots and CAs, one to a file: `.pem`, `.crt`, `.cer` or `.der`, named after the file. |
| `revoked` | Meep's own list, a line each: `serial <authority's fingerprint> <serial>`, `key <fingerprint>`, `grant <ID>` or `certificate <SHA-256>`, with a comment above saying what and when. |
| `revocation-lists/` | OpenSSH KRLs (`.krl`, from `ssh-keygen -k`) and X.509 CRLs (`.crl`, DER or PEM). |
| `grants` | Grants recorded by `Authorise` (Q256), as `meep-grant-v01 base64`, a line each. Phase 5 writes them. |
| `permissions.meep` | Permission definitions (Q257), one `#permission` line each. |
| `serial` | The last serial `meep trust issue` gave. |

A store others could change is refused, as OpenSSH refuses an `authorized_keys` others can write to: the folder above it, the folder, its subfolders or any file in it writable by group or others. So is an `authority.key` others can read.

### The permissions file

Meeplang-like: a directive a line, `//` comments, blank lines allowed.

```
// Who may read and write the notes.
#permission ReadNotes
#permission WriteNotes:~/notes/** authority=home requires=ReadNotes requires=meep.files.write:~/notes/**
```

- The name may have a scope. `authority=` and `requires=` may each be written any number of times, in any order. A value with spaces goes in double quotes, with `\"` and `\\` inside.
- A name is defined once. A second definition is an error naming the line; a pipeline's own `#permission` narrows instead.
- The same line is what a pipeline will write (phase 4), so it's read in the SDK by `Permission.ParseDirective`, which Meeplang's parser can call, and written back by `Permission.ToDirective()`.

The checks of "well defined" that need no credentials, from the Q257 proposal, are in the SDK too, for phase 3's `Verify.Permission`:

- `Permission.WiderThan(definition)`: the ways a permission is looser than its definition, as `WiderThanDefinition` failures. A scope outside the definition's, an authority the definition doesn't allow, no authorities when the definition names some, or a requirement left out or made narrower.
- `Permission.NarrowedBy(directive)`: a definition as a pipeline's directive narrows it. The directive's name and scope, its authorities if it names any (the definition's if not), and the requirements of both. `WiderThan` then says whether that's in fact no wider.
- `PermissionDefinitions.Check(permission)`: `UnknownPermission` if it isn't defined, `WiderThanDefinition` as above, and `UndefinedRequirement` for an authority not in the store, a requirement not defined, or a chain of requirements back to itself (`A → B → C → A`). A definition's own problems are in `Problems`, shown by `meep trust list` and logged on each reload, and the store still loads.

The fourth check, "in date and not revoked", has nothing to check yet: definitions have no dates and can't be revoked.

### The store

`TrustStore` in `Meep2.Security` (moved there from `Meep2.Plugins` for Q272), read with `TrustStore.Load(folder)` and unchanging once read. Its API is what phases 3–5 need:

| Member | For |
|---|---|
| `Version`, `RevocationVersion` | What a `VerifiedMessage` records, so a gate knows when to look again (Q259). |
| `OwnAuthority`, `IsSetUp` | This machine's authority, which signs grants in phase 5. |
| `Authorities`, `Authority(name)` | The `authorities` file, by name for definitions. |
| `AuthorityOf(credential)`, `AuthorityOf(grant)` | The trusted key that issued a credential (trusted for `meep-credential`) or signed a grant (trusted for `meep-grant`), or null. |
| `Roots`, `RootCollection()` | The roots, and a collection ready for `X509ChainPolicy.CustomTrustStore`. |
| `Revocations.WhyRevoked(credential / grant / certificate)` | A sentence saying why, or null. A credential is checked by serial, its key, its authority's key and the KRLs; a grant by its ID and its signer's key; a certificate by its hash and by the CRLs from its issuer. `NextUpdate(issuer)` says when the CRLs expect to be replaced (Q274). |
| `Grants`, `GrantsFor(credential)` | The grants recorded, and those naming one credential's key, serial and authority. Unchecked: `Verify` checks their signatures. |
| `Permissions` | The definitions, with `Find`, `Check` and `Problems`. |

`TrustFolder` is the writing side: `Create`, `AddAuthority`, `AddRoot`, `Revoke`, `AddRevocationList`, `RecordGrant` (for phase 5) and `NextSerial`. Whole files are written by renaming a new file into place, so a reload never reads half of one.

`SshRevocationList` reads OpenSSH KRLs: certificates revoked by authority and serial (as a list, a range or a bitmap) or by key ID, with any authority when the section names none, and keys revoked whole or by SHA-1 or SHA-256 hash. As OpenSSH does, a certificate is revoked if its key or its authority's key is. CRLs are read with BouncyCastle, already a dependency.

### Reloading

`store.Reload()` reads the folder again. It returns the same store if no file it reads has changed, otherwise a new one with the next `Version`. `RevocationVersion` goes up only when the reload takes something away: a revocation added or changed, or an authority, root, grant or definition removed or changed. Adding one doesn't make anything already verified wrong, so gates needn't look again.

`TrustStoreWatcher` keeps one current, as `PipelineWatcher` does for a pipeline: it loads the store, watches the folder and its subfolders, waits 250 ms for the files to settle, and reloads. A reload that fails is logged, with the reason, and the store before it stays in force. A reload that changed something is logged with its version and any problems, and raises `Reloaded`. Nothing uses it yet: phase 3 starts it in the daemon and in `meep run` along with `Verify`.

### `meep trust`

| Command | What it does |
|---|---|
| `meep trust init [--name <name>]` | Makes the folder and `authority.key` with `ssh-keygen -t ed25519`, and adds the key to `authorities` for credentials and grants, named after the machine unless `--name` says. Run again, it says it's set up already. |
| `meep trust add-authority <name> <type> <key>` or `<name> <ca.pub>` `[--grants]` | Trusts an SSH CA key to issue credentials, and with `--grants`, to sign grants. Refuses a private key, a name in use for another key, and a name with spaces, quotes, `=` or `#`. |
| `meep trust add-root <file> [<name>]` | Trusts a CA certificate, PEM or DER, written to `roots/<name>.pem`. Refuses a certificate that isn't a CA's. Says so if it isn't self-signed (Q275). |
| `meep trust revoke <thing> [--key]` | Works out what it's given: a credential file (revoked by authority and serial, or with `--key` by its key), a `.pub` key, a `SHA256:` fingerprint, a grant ID, a PEM certificate, or a `.krl` or `.crl` file, which is read and copied into `revocation-lists/`. Says if it's revoked already. |
| `meep trust issue <key.pub> [--id] [--principals] [--valid] [--permission]… [--bearer] [--out]` | Issues a credential with `ssh-keygen -s` and this machine's authority, with the next serial. Writes it beside the key as `<name>.credential`, or to `--out`, and refuses to overwrite one. Warns about permissions that aren't defined (Q278). |
| `meep trust list` | Everything in the store, and its problems. |

The tests:

- `PermissionDefinitionTests` (21 with their cases): directives read and written back the same; directives that can't be read refused saying why; narrower permissions pass and wider ones fail saying how, for scope, authorities and requirements; narrowing by a pipeline's directive; definitions found, checked and their problems listed; a chain back to itself; a bad line named by number, and a name defined twice.
- `TrustStoreTests` (15): an empty store; modes 700 and 600; a store others could change, or whose authority key others could read, refused; authorities by name, namespace and what they signed, and `TrustedKeys` reading the same file; two authorities with one name; grants recorded applying to their own credential and not a renewed one; revocation by serial, key, authority's key, grant ID and signer; a credential with no serial; KRLs made by `ssh-keygen -k` revoking by serial list, range, bitmap, key ID, key, hash and authority's key; KRLs and CRLs in the store, and a certificate revoked by hash; roots in PEM and DER; the permissions file and a bad line in it; versions over six reloads; a failed reload leaving the store as it was; the watcher reloading, and keeping the store through a broken file.
- `TrustCommandTests` (10): `init` once, its modes and its key's namespaces; `issue` before and after `init`, with every option, checked against the authority's key, with serials 1 and 2 and no overwriting; `issue` refusing bad options; `add-authority` from a file and the command line, once, by name, with `--grants`; `add-root` taking a CA and refusing a leaf; `revoke` given each kind of thing; `list`; and every command reporting a store that can't be read.

The tests that need `ssh-keygen` skip without it, as the signing tests do.

## Dropping AOT

Added 24 November 2026, after you dropped the AOT-compatibility requirement across Meep2. `IsAotCompatible` is gone from the SDK, Shared, Runtime and Modules projects, the comments in Meeplang's and Plugins' projects and `writing-verbs.md` no longer mention it, and `design-round-3.md` notes the change under the round 2 decision it reverses. Nothing else needed changing: the build has no warnings and the suite still passes, 823 of 823.

I went back over this phase's decisions and questions for anything AOT shaped:

- **Q272 gets simpler.** Option A's one doubt was whether BouncyCastle would keep the runtime AOT-compatible, and B's one advantage was keeping the runtime free of BouncyCastle. Neither matters now, so A is the clear choice, and I've rewritten Q272 to say so.
- **The two SSH wire readers can become one.** Phase 1 gave the SDK its own reader partly because `Meep2.Plugins` "isn't AOT-compatible". The other reason, that the SDK shouldn't depend on `Meep2.Plugins` and its NuGet and BouncyCastle, still holds; but once the signing code moves to `Meep2.Security` (Q272 A), that assembly can use the SDK's reader, and `Meep2.Plugins`' `SshReader` can go. That's part of the same move.
- **Nothing else in this phase was shaped by AOT.** The store, its files, the KRL and CRL readers, the permissions file and the CLI would be the same either way. Q273–Q279 aren't affected.

**The move, done 24 November (Q272 A).** A new project, `src/Meep2.Security`, namespace `Meep2.Security`, holds `PublicKey`, `SshSignature`, `TrustedKeys`, `SshKeyPair`, the allowed-signers reader, and everything in the trust store (`TrustStore`, `TrustFolder`, `TrustStoreWatcher`, `Revocations`, `SshRevocationList`). It references the SDK, Shared and BouncyCastle, and the runtime and `Meep2.Plugins` reference it. `Meep2.Plugins` keeps what's about plugins and pipelines: `PluginSignature` and `PipelineSignature`, and it no longer needs BouncyCastle itself.

- **One SSH wire reader and writer.** The SDK's `SshWireReader` and `SshWireWriter` are public now, with `ReadByte`, `ReadMpint` and `Mpint` added, and `Meep2.Plugins`' `SshReader` and `SshWriter` are gone. That adds two small types to the SDK's public surface. I chose that over giving `Meep2.Security` the SDK's internals, which would have let it make a `VerifiedMessage`. The SDK's reader is stricter than the old one: it refuses text that isn't UTF-8.
- **`TrustedKeys` throws `TrustStoreException`**, which lives beside it now, rather than `PluginException`, which stays with plugins. `PluginOptions.TrustedKeys()` turns it into a `PluginException`, so installing and loading plugins reports it as before. `meep keys` catches the new type.
- **`TrustedKeys.Find(key)` without a namespace is gone**, since it meant "may sign plugins" and `TrustedKeys` no longer knows about plugins. Its two callers pass `PluginSignature.Namespace`.
- **`Meep2.Plugins` sees `Meep2.Security`'s internals,** so `PluginSignature.Sign`, used by tests, can still take an `SshKeyPair`, which stays internal.

Nothing behaves differently. The build has no warnings, and the suite passes, 823 of 823, in two runs.

Outside this phase, two older decisions lose their reason, for you to weigh when they come up:

- **Q115's source generator for writing messages as JSON** was only needed for an AOT build. Writing them by reflection, as now, can stay.
- **The generated module registry and parameter records** (round 2) were for AOT and for keeping reflection off hot paths. The second reason still stands, so I'd keep them where they exist.

## Decisions I made while building

- **The store was first in `Meep2.Plugins`,** beside `PublicKey`, `TrustedKeys` and BouncyCastle, which it needs. Phase 3 puts `Verify` in `Meep2.Runtime` (Q266), which can't reach `Meep2.Plugins`, so after Q272 it moved to `Meep2.Security`, with them (see "Dropping AOT").
- **Authorities say what they may sign with `allowed_signers`' own `namespaces=`.** This machine's authority is trusted for `meep-credential` and `meep-grant`; an authority added with `add-authority` for `meep-credential` only, unless `--grants`. A line with no `namespaces=` is trusted for anything, as `ssh-keygen -Y verify` reads it; `add-authority` always writes one. Lines aren't marked `cert-authority`, since that would make `ssh-keygen` read the first word as the principals a certificate must have, where Meep has the authority's name.
- **Authority names have no spaces, quotes, `=` or `#`,** so a name can be written as `authority=home` and is one word in `allowed_signers`. The plan's example, `"Home CA"`, would be `home-ca` (Q277).
- **Names must be unique** in `authorities`, since definitions refer to authorities by name. Two with one name are an error rather than the first winning.
- **The authority key is an unencrypted OpenSSH key, 600,** made by `ssh-keygen`, so `ssh-keygen -s` can issue with it and a daemon can sign grants without anyone typing a passphrase (Q273).
- **`meep trust issue` writes `<name>.credential`, not `<name>-cert.pub`.** `ssh-keygen -s` writes `-cert.pub` beside the key, which for `~/.ssh/id_ed25519.pub` would overwrite an SSH certificate you might already have, and which `ssh` offers to servers by itself. It signs a copy of the key in a private folder inside the store instead, and deletes the folder after.
- **`issue` passes `-O clear`,** so a credential carries only Meep's extensions, not SSH's `permit-pty` and the rest, and it couldn't be used to log in even where the authority's key is trusted by `sshd`. It's valid for 52 weeks unless `--valid` says (`always` for no end). The key ID is the key's comment, such as `alice@laptop`, unless `--id` says. Serials start at 1, since 0 means none.
- **A revocation list is never checked for a signature.** It can only refuse, so one someone slipped into a store they can write to could do no more than they already could. A KRL's own signature section, which OpenSSH no longer checks either, is skipped.
- **A CRL applies to certificates whose issuer's name is the same bytes as the CRL's.** That's how CAs write them in practice; indirect CRLs, which revoke for other issuers, aren't supported.
- **`RevocationVersion` goes up only when something is taken away,** comparing the new store with the old one, as above. Comparing is cheap and only happens when a file changes.
- **The narrowing rule:** a pipeline's directive replaces the definition's authorities only if it names any, and adds its requirements to the definition's. So `#permission Write:~/notes/**` in a pipeline keeps the definition's authorities and requirements and only narrows the scope, and a directive can't drop a requirement by leaving it out.
- **A requirement narrower in scope is looser,** and so wider than the definition: requiring `meep.files.write:~/notes/**` where the definition requires `meep.files.write` lets more credentials through. A requirement is kept only if one at least as wide is required.
- **A definition may have a scope,** as in `#permission meep.files.write:~/notes/**`, meaning the permission only counts within it. One definition per name, scoped or not.
- **The trust store isn't started by the daemon yet.** Nothing would read it until `Verify`; phase 3 starts the watcher in the daemon and `meep run`.

## Found in passing

- **.NET 11 has no Ed25519.** `System.Security.Cryptography` has no Ed25519 class even now, so checking an authority's signature needs BouncyCastle, through `PublicKey`. That's why where the store lives matters for phase 3 (Q272).
- **`ssh-keygen -k` takes `hash: SHA256:…`, not `sha256:`,** for revoking a key by fingerprint in a KRL specification. The tests use it; `meep trust revoke` takes a fingerprint directly.
- **`ssh-keygen -L` shows Meep's extensions as unknown,** as `WriteNotes@meep UNKNOWN FLAG OPTION`, with a scope's value in hex. Harmless; `meep trust list` could show a credential more readably one day.
- **`ssh-keygen` makes a `.pub` file 644,** so `init` narrows `authority.key.pub` to 600, as the store's files are.
- **`FileShare.None` on macOS and Linux is an advisory lock that fails at once,** rather than waiting. Two `meep trust issue`s at the same moment can't take the same serial; the second says to try again.
- **The watcher's test had a race,** which showed once while applying the answers: it checked the `Reloaded` event as soon as the store had changed, but the event is raised just after the swap, and the files can settle in more than one reload. It now waits for the event. Three full runs after, 823 of 823 each.
- **A chain of requirements back to itself is satisfiable:** a credential holding A, B and C meets `A → B → C → A`. The Q257 proposal counts such a chain as not well defined, and so does this, but it's a style rule rather than a contradiction. Say if you'd rather allow it.

## Questions

**Q272.** Phase 3 puts `Verify` in `Meep2.Runtime` (Q266 A), but the trust store and signature checking (`PublicKey`, with BouncyCastle for Ed25519) are in `Meep2.Plugins`, which depends on `Meep2.Meeplang`, which depends on the runtime, so the runtime can't reach them. Which way?
- A: Move the signing code (`PublicKey`, `SshSignature`, `TrustedKeys`, `SshKeyPair`) and the trust store into a new `Meep2.Security` assembly, with BouncyCastle, that the runtime and `Meep2.Plugins` both reference. It would read SSH's wire format with the SDK's reader, so `Meep2.Plugins`' copy goes.
- B: The runtime defines a small interface for what `Verify` needs (find an authority, check a signature, revocations, grants, definitions, versions), and the daemon and `meep run` hand it the trust store. One more layer, now with no benefit to pay for it, since keeping BouncyCastle out of the runtime no longer matters.
- C: Put `Verify` in `Meep2.Plugins` after all, and give it the narrow way to make verifications that Q266 C gives the Listens.

*Revised 24 November, after AOT was dropped.* I recommend A: it's mostly moving files, it leaves one place for everything about keys, and the doubt I had about it (whether BouncyCastle kept the runtime AOT-compatible) is gone. I can do it on this branch, before phase 3.

**Answer:** A, given in chat on 24 November.

Done: `Meep2.Security` holds the signing code and the trust store, with one SSH reader, the SDK's. See "Dropping AOT".

**Q273.** This machine's authority key is an unencrypted file, 600, so the daemon can sign grants unattended. Anyone who can read it as you can issue credentials. Is that enough for now, or should `meep trust init` also offer to keep the key in ssh-agent, or on a security key, with the daemon signing through the agent? I'd keep the file now, and add an agent option when phase 5 signs grants, since that's when the daemon first uses the key.

**Answer:** Keep the file now.

Done: nothing to change. An agent option can come with phase 5, if it's wanted then.

**Q274.** A CRL says when it'll next be updated. Once that's passed, should `Verify.Certificate` (phase 3) fail certificates from that issuer, as not knowing whether they're revoked (a new code, `RevocationUnknown`), or use the old CRL anyway? Failing is safer, and what browsers do for must-staple; using it keeps things working when someone forgets to fetch a new one. I recommend failing, and showing stale CRLs in `meep trust list`.

**Answer:** Fail them.

Done: phase 3's `Verify.Certificate` will fail a certificate whose issuer's CRL is out of date, with a new code, `RevocationUnknown`. For now, `Revocations.OutOfDateAt(when)` names the CRLs that are out of date, and `meep trust list` marks each one, as in `the list old.crl, out of date since 2026-11-23 10:00Z: certificates from its issuer will fail until it's replaced`. A test of the store and the `list` test cover it.

**Q275.** Q255 says `AuthoriseCertificate` trusts a new CA *or* root. `roots/` takes any CA certificate, including one that isn't self-signed (an intermediate), and `add-root` says when it isn't. But .NET's `X509Chain` with custom root trust expects the chain to end at a certificate in the custom store, and I'm not sure every platform accepts an intermediate there as the anchor. Should phase 3 treat any certificate in `roots/` as an anchor, checking the chain itself if .NET won't, or should `add-root` refuse CAs that aren't self-signed? I'd treat any as an anchor, since trusting one department's CA without its parent is a real need.

**Answer:** Yes, trust any in `roots/`

Done: nothing to change now. Phase 3 treats every certificate in `roots/` as an anchor, checking the chain itself where .NET's custom root trust won't end at an intermediate. `add-root` still says when a CA isn't self-signed, which is useful to know.

**Q276.** Grants are trusted if an authority trusted for `meep-grant` signed them: this machine's own, and any added with `add-authority --grants`, such as another Meep's, so a grant from another machine can be checked (Q256's last sentence). Is that right, or should a recorded grant only count if this machine signed it, with other machines' grants counting only where they're carried?

**Answer:** Don't know yet. Let's keep it the way it is for now. Can you create a file at docs/Design/ToDo.md with a note to revisit this in the future?

Done: it stays as built, and `docs/Design/ToDo.md` has a note to decide it once grants from other machines are in use (phases 5 and 7).

**Q277.** Authority names can't have spaces, quotes, `=` or `#` (so `home-ca`, not `"Home CA"`), since they're one word in `allowed_signers` and in `authority=`. Allowing quoted names would mean quoting them in both places. Is one word enough?

**Answer:** One word is enough.

Done: nothing to change.

**Q278.** `meep trust issue --permission Name` warns, but goes ahead, when `Name` isn't defined in `permissions.meep`, since you might define it afterwards. Should it refuse instead? I'd keep the warning.

**Answer:** Keep the warning.

Done: nothing to change.

**Q279.** Meep's own `meep.*` permissions (phase 6): should Meep define them itself, so `permissions.meep` needn't list them and can only narrow them (add authorities or requirements), or must they be listed there like any other, so an empty file means no `meep.*` permission is well defined? I'd have Meep define them with no requirements, and let the file narrow them.

**Answer:** Let Meep define them.

Done: nothing to change now. In phase 6, Meep defines its `meep.*` permissions with no requirements, and a `#permission meep.…` line in `permissions.meep` narrows Meep's definition, as a pipeline's does, rather than being a second definition.
