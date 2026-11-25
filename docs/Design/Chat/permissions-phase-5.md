# Permissions, phase 5: `Authorise`, `Revoke` and `Parse`

Written 25 November 2026. Phase 5 of the plan in `permissions.md`: `Authorise` (and `Authorize`), `Revoke`, the `Parse` verb with the reader it shares with `Verify` (Q293), a span for each refusal (Q298), gates that need a kind of proof (Q299), and a trust store that more than one process can change at once. The work is on the branch `permissions-phase-5`, made from `main` at 31cd277, not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q300, for you to answer inline.

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 902 of 902 (881 before, on `main`, and 21 new tests, counting each case of a test with arguments; 898 before the changes after Q300, below). None of the known flaky tests failed.
- No test touches the real `~/.meep`: each uses a temporary home folder.
- **More than one process.** Besides the test with eight writers in one process, I ran the real `meep` CLI from a copy of the build, against a store in a scratch folder: four processes revoking ten keys each and two issuing three credentials each, all at once. The store had all 40 revocations, the serials were 1 to 6 once each, and nothing failed.
- **On strawberry.local** (linux-arm64), with its own umask of 002, every security test passed before the changes after Q300: 194 of 194 (`AuthoriseTests` 12, `GateTests` 26, `VerifyTests` 30, `TrustStoreTests` 15, `TrustCommandTests` 11, `CredentialTests` 11, `SecurityMessageTests` 11, `PrivacyTests` 5, `PermissionDefinitionTests` 21, `PermissionNameTests` 52). The same cross-process run with a linux-arm64 `meep` gave the same result: 40 revocations, serials 1 to 6 once each, no errors, so the lock works with Linux's `flock` too. The test copies were in `~/meep-phase5-*` and are deleted; the installed daemon wasn't touched and is still running. (The first try failed, as SSH had no key loaded; it worked once you started the agent.)
- Final runs, after the changes after Q300: 902 of 902, twice, with no warnings. The changes after Q300 weren't run on the Pi; they touch nothing specific to Linux.

## What was built

### `Parse`, and the reader it shares with `Verify`

`SecurityText`, in the SDK (`Meep2.SDK.Security`), is the one reader of credentials, certificates, permissions and grants written as text:

- `KindOf(text)` tells them apart by form alone, cheaply: `Credential`, `Certificate`, `Permission`, `Grant` or `None`. It's phase 3's rule, moved: PEM or base64 DER is a certificate; a `#permission` line or a name is a permission; an SSH certificate, as a `-cert.pub` file has it or its base64 alone, is a credential, and is recognised before a permission's name. `meep-grant-v01 …` is a grant, which is new.
- `Read(text)` reads whatever it is, or throws a `FormatException` saying what it found: "This is an SSH public key (ssh-ed25519), not a certificate. A credential is a key signed by an authority…", "This is JSON…", or the first word.
- `Read<T>(text)` reads it as one kind, whatever its form.

`Verify` now chooses its inflection with `KindOf` and reads with `Read<T>`; its own copy of the rule is gone, and anything `KindOf` doesn't call a certificate or a permission still goes to `Verify.Credential` as a malformed credential.

`Parse` (`Parse.Text`, in `src/Meep2.Runtime/Verifying/Parse.cs`) reads text from `from=` or the message's own, and emits the typed message, or an error saying what it found. `kind=credential` (or `certificate`, `permission`, `grant`) makes anything else an error, and narrows what the loader expects it to emit, so the module under it need take only that type. A message that's already one of the four is passed on.

### `Authorise` and `Authorize`

One verb with two inflections, chosen by the message's type, so each message meets only its own inflection's gate (Q293). `[Verb]` has a new `Alias`, and the registry lists the verb under both names, each knowing the other (`ModuleRegistration.AliasOf`). Tools and errors use the name written, and `meep lsp` says "**Authorize** (step), another name for **Authorise**".

**`Authorise.Permission`** takes a `Credential`, the one to grant to, and `permission=` (required, with an optional scope, from a constant or the message), and optionally `valid=`, how long the grant lasts. It needs `AuthorisePermission`; `@permission` adds to that (Q263). For each credential:

1. It reads the permission, and refuses a name that isn't one.
2. It checks the authorising credential holds the permission, with a scope at least as wide, unless it holds `AuthoriseAny` (Q262). The authorising credential is whatever the message's family was verified with, as at any gate, so it's the same check as a gate's (`context.WhyNotHeld`), with the pipeline's `#permission` lines and the module's `@proof`. Lacking it is a `PermissionDenied`, so an `@errors` branch answers it as it would any refusal.
3. It checks the credential being granted to as `Verify.Credential` does, without proof of possession: a user's certificate, from a trusted authority, its signature good, in date, no critical options, not revoked. One that fails is an error, and nothing is recorded (Q300).
4. It signs a `Grant` with this machine's authority key, in the `meep-grant` namespace, and checks the grant would count here (that the authority is trusted to sign grants), before recording it in the store's `grants` (Q256). The credential isn't reissued (Q254).
5. It reads the store again at once, so the grant is in force in this pipeline straight away, and emits the grant, a child of the credential.

**`Authorise.Certificate`** takes a `Certificate`, and needs `AuthoriseCertificate` (Q255 A). It refuses one that isn't a CA's (the check `meep trust add-root` makes, now shared as `TrustedRoot.WhyNot`), passes on one trusted already, and otherwise writes it to `roots/` as `name=`, or its common name and the start of its SHA-256 thumbprint (`Department-CA-3f9a12bc`). Any CA will do, an intermediate too (Q275). It emits the certificate.

### `Revoke`

Three inflections, by type, each needing `RevokePermission`:

| Inflection | Takes | Revokes |
|---|---|---|
| Credential | `Credential` | by authority and serial, or with `whole-key=true`, the key and every credential for it |
| Grant | `Grant` | by ID |
| Certificate | `Certificate` | by SHA-256 hash |

It writes to `revoked` unless the line is there already, reads the store again so gates notice at once (Q259), and emits what it revoked. Under `Authorise`, it revokes the grant just made. What a revocation is and how it's described is now one record in `Meep2.Security`, `Revoking`, which `meep trust revoke` uses too.

### Signing grants

`TrustFolder.SignGrant(terms)` reads `authority.key` and signs. `SshKeyPair.FromOpenSsh` reads an unencrypted Ed25519 key in OpenSSH's format, as `meep trust init` makes, and refuses an encrypted key ("encrypted with a passphrase, so Meep can't sign with it unattended") or another kind, so no `ssh-keygen` runs for each grant. Q273's file stays (Q304).

### Gates that need a kind of proof (Q299)

`@proof=signed` on a gate needs its credentials to have been proven: by a signed request, a connection's proof or TLS. `@proof=request`, `@proof=connection` and `@proof=tls` need one kind, and can be written together. A bearer credential is refused: "alice's credential was verified with none, as a bearer credential, and this needs a signed request, a connection's proof or TLS." It's a separate modifier rather than `proof=` after `@permission`, since a word without `@` is a module's parameter, and `Listen`'s own parameter for turning away unsigned requests, once `proof=`, is now `signature=` (Q306).

- It applies to every permission the module needs, its verb's included, and to `Demand` and `Holds` in the module's code.
- It's read into `NodeOptions.Proofs`, and the gate's requirements carry it, so answers kept on messages for a gate with `@proof` and one without never mix.
- `@proof` on a module that isn't a gate is a load error, as is a kind that isn't one of the four. `meep lsp` completes it and describes it on hover.

### A span for each refusal (Q298)

As agreed under Q298. Every refusal, at a gate or by `Demand`, is a short span of its own:

- Named after the module's verb, as `Save.denied` (Q307), with the span the message came from as its parent.
- With the usual tags, plus `meep.permission`, `meep.permission.reasons` and `meep.outcome = denied`, and the `meep.permission.denied` event phase 4 put on the emitting module's span, now on this one. Its status isn't `Error`.
- The `PermissionDenied` it becomes hangs under it, so an `@errors` branch answering with a 403 is its child.
- A refusal straight after a source has one too, under the source's span.
- Nothing is recorded for a message whose trace isn't, so it costs nothing without tracing.

### More than one writer

The daemon, `meep run` and `meep trust` may all change the store. Now:

- **Every change holds a lock**: an exclusive lock on `~/.meep/trust/.lock` (`FileShare.None`, which on macOS and Linux is `flock`), waited for, with a little jitter, for up to ten seconds. A process that dies lets it go, so a crash can't leave the store locked.
- **Every file is written whole and renamed into place**, adding a line included, so a watcher reloading meanwhile never reads half of one, and two writers adding at once lose neither's line. Before, adding a line appended in place.
- **`NextSerial` waits for the lock**, rather than failing at once and saying to try again.
- **The watcher ignores `.lock` and `*.new`**, which aren't the store.
- **`TrustStoreSource.Reload()`** reads the store again now, for the verbs that changed it. A fixed store (`TrustStoreSource.Of`, for tests and embedding) is now read again when asked, rather than never.

### Documentation

- `docs/verbs.md`: `Parse`, `Authorise` and `Revoke` in the summary and the Security section; `@proof` and the refusal spans under Gates; and "A small authorisation system", a worked example of a notes service in which an administrator grants and revokes the right to read the notes over HTTP.
- `docs/writing-verbs.md`: `Alias`, taking security messages typed and reading text with `SecurityText`, and `@proof` applying to `Demand`.
- `docs/troubleshooting.md`: a refusal for lack of proof, `@proof` on a module that isn't a gate, and a new section, "Authorising and revoking", on the load error `Parse` without `kind=` gives, what `Parse` says it found, each error from `Authorise` and `Revoke`, changes a daemon doesn't see yet, and the lock.

### The tests

`AuthoriseTests` (16, four of them added after Q300), with an `AuthoriseLab`: a store with this machine's own authority key (made by `ssh-keygen`, trusted for credentials and grants), an administrator's bearer credential holding the permissions a test asks for, and bob's.

- The reader tells each kind of text apart, reads each, says what it found for a public key, JSON and junk, and chooses `Verify`'s inflections the same way.
- `Parse` emits each type, passes a typed message on, and gives errors for the wrong kind and for a public key.
- The authority key is read from its file and signs grants that verify; an encrypted key, an ECDSA key and junk are refused; with no key, signing says to run `meep trust init`.
- A grant is emitted, recorded, in force in the pipeline's store at once, lasts `valid=`, and counts when bob's credential is verified later from a store read afresh.
- Only what the authoriser holds: a narrower scope is granted; a wider one and another permission are refused with a `PermissionDenied` and counted as denied; `AuthoriseAny` grants anything.
- Without `AuthorisePermission`, refused; `@permission` on `Authorise` adds to it; the inflections take different types and need what they should.
- `Authorize` works, keeps its name, and is an alias of `Authorise` in the registry.
- Errors: a credential from an untrusted authority, a bad permission name, no authority key, and no `permission=`.
- A CA is trusted, after which a leaf it issued verifies; trusting it again writes nothing; a leaf is refused; `AuthoriseCertificate` is needed.
- `Revoke` revokes a grant just made (and the grant is then left out, with a note), a credential (raising the revocation version, and writing nothing the second time), a key, and a certificate; `RevokePermission` is needed.
- The worked example in `verbs.md`, over real HTTP with signed requests: bob is refused the notes and can't grant himself; the administrator grants him `ReadNotes` for 90 days and gets the grant back; bob reads the notes; the administrator revokes the grant; bob is refused again.
- Eight writers at once, each with its own `TrustFolder`, sign, record and revoke 15 times each while a watcher reloads: every grant and revocation is there, no reload fails, and no `.new` file is left.

`GateTests` (4 new, one rewritten): the span test now checks a refusal span after a source and after a module, their parents, tags and event, and that the answering `@errors` module's span is the refusal's child; a gate with `@proof=signed` lets a signed request through and refuses the same credential unsigned; `@proof` mistakes (two cases); and the kinds `@proof` accepts. `LanguageServerTests` (1): `@proof` completed and described, and `Authorize` described as `Authorise`.

## Added after Q300

Added 25 November 2026, after we talked through Q300 in chat. I set out what leaving Q300 as built could expose. Requiring the subject to prove possession when the grant is made would add consent, not security: a grant still needs the key when it's used. The real risk was different: the subject being swapped on the way. You asked for three changes. The fourth, an optional `consent=` for a request-then-approve flow, waits until later.

- **What a signed request names must be covered by its signature.** `SignedParts.WhyNotCovered` finds the nearest signed HTTP request among the message's ancestors. It looks for what `Authorise` or `Revoke` acts on in that request's covered parts: the body when `content-digest` is covered, the address when `@target-uri` or `@query` is, each header listed in `Signature-Input`, and `Meep-Credential`, which the signature binds. It refuses only text found in an uncovered header and nowhere covered. The error names the header: "It took the credential to grant to from the request's X-Subject header, which the request's signature doesn't cover, so it could have been changed on the way. Read it from the body, or have the client sign x-subject too."
  - It checks the credential granted to, the permission (unless `permission=` is a constant), the certificate to trust, and whatever `Revoke` is given.
  - Base64 is matched ignoring white space, so PEM is found; a permission's name must be a header's whole value.
  - Text found nowhere in the request, such as a file the pipeline read, isn't affected. Nor is an unsigned request, which `Verify` and the gate deal with.
  - `RequestSignature.Covered(request)` lists the components a signature covers.
- **`Authorise` and `Revoke` need proof by default.** A new SDK attribute, `[DefaultProof(…)]`, gives an inflection's gate the kinds of proof it needs unless the line's `@proof` says otherwise. `Authorise` and `Revoke` declare a signed request, a connection or TLS, so a bearer credential can't change the trust store. A line's own `@proof` replaces the default rather than adding to it, and the new `@proof=any` accepts a bearer credential, for a pipeline that reads an administrator's credential from a file. `Demand` and `Holds` in the module's code use the same proof as its gate.
- **Only `AuthoriseAny` grants the permissions over the trust store.** `AuthorisePermission`, `AuthoriseAny`, `AuthoriseCertificate` and `RevokePermission` (`SecurityPermissions.OverTrust`) can be granted only by a credential holding `AuthoriseAny`. Without it, the refusal is a `PermissionDenied` needing `AuthoriseAny`: "Only a credential holding AuthoriseAny may grant AuthorisePermission, which changes what this machine trusts: …".
- **Docs.** `verbs.md` says all three under `Authorise` and `Revoke`, explains `@proof=any` and verbs' defaults under Gates, and drops `@proof=signed` from the worked example, as it's now the default. `writing-verbs.md` has `[DefaultProof]` and the rule about covered parts. `troubleshooting.md` has a section for each new refusal. `meep lsp`'s hover for `@proof` mentions `any` and the default.
- **Tests** (4 new in `AuthoriseTests`):
  - A bearer administrator is refused by `Authorise` and `Revoke` with the proof reason, passes with `@proof=any`, and a line's `@proof=tls` replaces the default.
  - Each of the four permissions is refused without `AuthoriseAny` and granted with it.
  - `SignedParts` accepts text in the body, a covered header, `Meep-Credential`, the query, PEM-wrapped base64, or nowhere in the request. It refuses text in an uncovered header, and a permission's name as an uncovered header's whole value, and has nothing to say about an unsigned request.
  - Over real HTTP, `Authorise` refuses a credential read from an uncovered `X-Subject` header, records nothing, and grants once the client signs that header.
  - The existing tests with a bearer administrator now write `@proof=any`.

## Added after Q306

Added 25 November 2026, after we talked through Q306 in chat.

- **`Listen`'s `proof=` is now `signature=`.** `signature=required` turns away, with 401, a request without `Signature-Input`, `Signature` and `Meep-Credential`; `signature=optional` is still the default. It checks only that the headers are there, before `Verify`, while `@proof` checks at a gate how a verified credential was proven. The old name made the two look like one thing. In code, `HttpListenParameters.Proof` is now `Signature`, of the enum `RequiredSignature` (once `RequiredProof`). Nothing reads the old name, since none of this is released.
- **Earlier reports corrected.** `proof=signed` written as a parameter after `@permission` is now `@proof=signed` in `permissions.md` (the phase plan and the proposal under Q253) and in phase 4's Q299. `proof=required` on `Listen` is now `signature=required` in `permissions.md` and the reports for phases 3 and 4. The mistake was in my proposal under Q253, which phase 4's Q299 and the plan then copied.
- **Docs.** `verbs.md` (Listen's parameters, the worked example and the gate notes), `troubleshooting.md` and the Synesthesia handover note say `signature=required`.
- **Tests.** The pipelines in `GateTests` and `AuthoriseTests` say `signature=required`. No test was added: the same tests cover the renamed parameter.
- **Later.** Phase 4 left room for a `connection` value, for phase 7's challenge over TCP and WebSockets. Under the name `signature=` that would read oddly, so phase 7 may want a parameter of its own for it.

## Decisions I made while building

- **`Authorise.Permission` takes the credential being granted to, and `permission=` names what to grant.** The plan's "given a credential and a permission name" fits a typed `Credential` with a parameter, and it keeps the two inflections on different types. The other way round, taking a `Permission` and finding the credential among the ancestors, would have to guess which of two credentials in the family (the authoriser's and the subject's) was meant.
- **The credential granted to is checked by `Authorise` itself, not required to have passed a `Verify`.** Its holder isn't the one making the request, so nothing could prove possession of its key, and a `Verify` would fail it unless it allowed bearer use (Q300).
- **"What you hold" is a gate's question,** so the definition's authorities and requirements, the pipeline's `#permission` lines and `@proof` all apply, and the refusal is a `PermissionDenied`. Q289's rule applies too: `AuthoriseAny`, undefined, is conferred by any trusted authority.
- **A grant isn't recorded unless it would count here.** If this machine's authority isn't trusted for `meep-grant`, `Authorise` says so rather than recording a grant `Verify` would leave out.
- **Grants aren't capped by the authoriser's own dates** (Q301). A grant lasts `valid=`, or as long as the credential granted to.
- **`Parse kind=` narrows what the loader expects** through the existing `[Emits(When = …)]` switches, with four read-only `bool` properties on its parameters, so the loader needed no change (Q302).
- **`Parse` reads grants too.** Q293 named three kinds; a grant is the fourth thing `Revoke` takes, and a client holding one has it as text.
- **`Revoke`'s parameter is `whole-key=true`,** not `by=key`: `by` is a Meeplang keyword.
- **`Revoke` and `Authorise.Certificate` emit their input,** as `Forget` does, rather than a new message type, so a `Send` under them can answer with what was done. `Authorise.Permission` emits the grant, the new thing.
- **SSH CA keys aren't trusted by `Authorise` yet.** The plan's "or `authorities` for an SSH CA key" needs a message type for a bare public key, which doesn't exist (Q303).
- **`@proof` is a modifier of its own,** written as above (Q306).
- **Refusal spans are named after the verb** (Q307).
- **The lock is a file lock, not a lock in memory,** since the writers are separate processes; changes in one process take the same lock, since `flock` is held by an open file, not by a process.

## Found in passing

- **`Parse` without `kind=` above `Authorise` doesn't load.** The loader refuses a module that can be sent types it doesn't take, and `Parse` may emit four. So `kind=` is needed in practice; the docs and troubleshooting say so (Q302).
- **The store's privacy check could fail while another process wrote.** `CheckPrivate` lists the folder and then looks at each file; a `.new` file renamed into place between the two made it throw `FileNotFoundException`, failing the reload. The eight-writer test found it. It now skips a file that's gone.
- **`by` is a keyword,** as noted, and gives "Write by without =" if used as a parameter.
- **A test sink takes errors too.** `Sink` in tests takes every message, errors included, so a test expecting only successes needs `@no-errors` on it.
- **Two thumbprints.** The SDK's `Certificate.Thumbprint` is SHA-256, as the `revoked` file uses, but .NET's `X509Certificate2.Thumbprint` is SHA-1. `TrustStore.Roots` and `meep trust add-root` compare roots by .NET's, which is consistent with itself; I used the SHA-256 one for names.
- **Q298's answer isn't written in phase 4's doc**, though phase 4's "After your answers" and this brief say it was agreed in chat. I built it as the proposal says and left the doc alone.
- **`meep trust init` writes `authority.key` through `ssh-keygen`,** outside the lock. Two `init`s at once are unlikely, and the second says the store is set up.
- **Grants and revocations only grow** (Q305).

## Questions

**Q300.** `Authorise.Permission` checks the credential it grants to itself (trusted authority, signature, dates, revocation), without proof of possession, since its holder isn't the one asking. The alternative is to require a passing `Verify` of it among the message's ancestors, which would need `bearer=true` and a credential that permits bearer use, so most credentials couldn't be granted to. Keep the check in `Authorise`?

**Answer:** Keep as built, and with the changes outlined below as per our discussion now having been completed.

*Discussed in chat, 25 November.* Kept as built, with three changes made to close the risks it raised: what a signed request names must be covered by its signature, `Authorise` and `Revoke` need proof by default, and only `AuthoriseAny` grants the permissions over the trust store. See "Added after Q300". An optional `consent=` for a request-then-approve flow is left for later.

**Q301.** A grant doesn't record who authorised it, and isn't bounded by the authoriser's credential: if the administrator's credential is revoked or expires, the grants they made stay. Should grants be tied to their authoriser? Options:
- A: Leave them as they are. They're signed by this machine, and can be revoked one by one.
- B: Write who authorised each grant (key ID and fingerprint) in the comment above its line in `grants`, for people reading the file, at no other cost.
- C: Put the authoriser's fingerprint and serial in the signed terms (a `meep-grant-v02`), and have `Verify` leave a grant out when its authoriser's credential is revoked.

I'd do B now, and C only if someone needs revocation to cascade.

**Answer:** C, but only to one level.

**Q302.** `Parse` without `kind=` above `Authorise` or `Revoke` doesn't load, since `Parse` may emit types they don't take. Keep `kind=` as the way, or:
- A: Keep it, as built: the error says what can reach the module, and troubleshooting says to add `kind=`.
- B: Give `Parse` an inflection for each kind, `Parse.Credential` and so on, so the line names what it makes.
- C: Have the loader accept a module that takes only some of what an untyped `Parse` emits, sending the rest past as errors.

I'd keep A; B is more to learn for the same thing, and C weakens a check that catches real mistakes.

**Answer:** A

**Q303.** The plan's Q255 notes say `Authorise.Certificate` could also trust an SSH CA key, by adding it to `authorities`. There's no message type for a bare public key, so it isn't built. Add one (say `AuthorityKey`, which `Parse` would read from a `.pub` line) and an `Authorise.Authority` inflection, needing a new `AuthoriseAuthority` permission so that trusting X.509 roots and trusting credential issuers can be given apart? I'd add it in phase 6, with Meep's own permissions, or when someone needs it.

**Answer:** Add in phase 6.

**Q304.** Q273 left the authority key a file, with an agent option to come "if signing grants makes it worth raising". Grants are signed in the process, from the unencrypted file. An encrypted key, or one only in ssh-agent or on a security key, can't sign grants. Add signing through ssh-agent now, or later? I'd leave it: the daemon signs unattended, so an agent would have to be running for it anyway, and a file kept to the user is what `ssh-keygen -s` uses too.

**Answer:** Leave it for later. I'll be revisiting this in the future to explore options.

**Q305.** `grants` and `revoked` only grow: expired grants, and revocations of credentials that have since expired, stay forever, and each is read and checked on every reload. Add a `meep trust tidy` that removes expired grants and revocations no longer needed (keeping a revocation until what it revokes has expired), or leave the files alone? I'd add it when a store gets big enough to notice, and not let `Authorise` or `Revoke` tidy as they go, since a store that changes only when asked is easier to reason about.

**Answer:** Add a `meep trust tidy` command.

**Q306.** `@proof` is its own modifier, `@permission=ReadNotes @proof=signed`, and applies to every permission the module needs. Q253's proposal wrote `@permission=Name proof=signed`, which Meeplang would read as a parameter of the module, and `Listen` had a `proof=` already (now `signature=`). A permission that needs proof only on some modules would need two modules. Is a modifier for the whole module right, or should proof be part of each `@permission`'s value, as in `@permission="ReadNotes proof=signed"`?

**Answer:** A modifier for the whole module, as built. Correct the earlier reports that wrote `proof=signed` as a parameter, and rename `Listen`'s `proof=` to `signature=`.

*Discussed in chat, 25 November.* `@proof` was already its own modifier, and nothing outside the design reports wrote `proof=signed`, so no pipeline or test changed. Modifiers start with `@`, so `@proof` can't clash with a parameter. But `Listen proof=required` looked like the same thing while meaning something else, so it's renamed. See "Added after Q306".

**Q307.** Refusal spans are named after the module's verb and inflection, `Save.denied` or `Fetch.File.denied`, as handling spans are, to keep span names few; the module's own name (`as Archive`) is in the `meep.module` tag. Is that right, or should the name be the module's own?

**Answer:** That's right.

## After your answers

Written 25 November 2026, after your answers. Q302 and Q307 stand as built, and Q306 is covered under "Added after Q306". The build has no warnings, and the whole suite passes: 908 of 908 (902 before, and 6 new tests). These changes weren't run on strawberry.local; nothing in them is specific to Linux.

- **Q301 (C, one level).** New grants are `meep-grant-v02`. Their signed terms end with the credentials that authorised them (`GrantTerms.AuthorisedBy`), each named by its key's fingerprint, serial and authority's fingerprint (a new SDK record, `CredentialName`).
  - `Authorise` names every credential through which the message holds `AuthorisePermission`, one for each line of its family, as a gate finds them. `PermissionLines.Holders` walks the lines for this, and `ModuleContext.HeldBy` reaches it from a module.
  - `Revocations.WhyRevoked(grant)` now says a grant "falls with the credential that authorised it" when one of those credentials is revoked, by serial, key, authority's key or a KRL. So `Verify` leaves the grant out, and gates refuse it at once through the revocation version, as for any revocation.
  - A KRL can now be asked about a credential named only by fingerprints (`SshRevocationList.WhyRevoked(CredentialName)`). It finds a key listed whole or by its SHA-256 hash, but not one listed by its SHA-1 hash, nor a credential revoked by its key ID, which a name doesn't carry.
  - It goes one level only. A grant doesn't fall when its authoriser's credential expires or loses `AuthorisePermission`. Grants made by someone a grant was given to don't fall with that grant.
  - `meep-grant-v01` grants still read, verify and print as v01, since their signature covers v01 bytes. They name no authoriser. v01 terms can't be given authorisers.
  - `GrantTerms` compares its authorisers by value, so two terms read from the same text are equal.
- **Q302 (A).** As built.
- **Q303.** Added to phase 6 in `permissions.md`: an `AuthorityKey` message, `Authorise.Authority`, and an `AuthoriseAuthority` permission.
- **Q304.** Left for later, with a note in `ToDo.md`.
- **Q305.** `meep trust tidy` takes out of `grants` and `revoked` what's no longer needed, holding the lock, and lists each line it took out with the reason (`TrustFolder.Tidy`; the rules are in `StoreTidying`).
  - A grant goes once it has expired, been revoked by its ID, or its credential has been revoked.
  - A revocation goes once what it revokes has expired. A revocation now says so in its comment, as `needed until 2027-03-01T00:00:00Z`, written from the credential's, grant's or certificate's own end date (`Revoking.Until`). For a grant revoked by its ID, `meep trust revoke` looks the grant up to find that date.
  - A grant's revocation also goes once the grant recorded in `grants` has expired.
  - A key's revocation stays for ever, as does one without an end date, which includes every revocation written before this change.
  - A credential's revocation also stays while a grant still in `grants` names that credential as its authoriser. Such a grant stays too, so a grant that fell with its authoriser can't come back once the authoriser's credential expires and its revocation is tidied.
  - The files' own comments, and any you wrote, are kept. Each entry goes with the one comment line Meep wrote above it (`StoreLines`).
- **Q307.** As built.
- **Tests** (6 new):
  - `SecurityMessageTests`: a v02 grant round-trips with its authoriser, and a v01 grant still reads, signs and prints as v01.
  - `AuthoriseTests`: `Authorise` names the administrator. Revoking another credential changes nothing. Revoking the administrator's credential takes the grant with it, in `Verify` and at a gate, for a verification made before.
  - `TrustStoreTests`: a KRL revokes a credential named by fingerprints. Tidying takes out and keeps what the rules say. `StoreLines` writes back what isn't an entry as it was.
  - `TrustCommandTests`: `meep trust tidy`'s output, and revoking a recorded grant by its ID writes when the revocation is needed until.
- **Docs.**
  - `verbs.md`: that a grant falls with its authoriser, one level only. The worked example now shows `meep-grant-v02`.
  - `troubleshooting.md`: a grant left out because its authoriser was revoked, and a new section on `meep trust tidy`.
  - `meep`'s help lists `meep trust tidy`.

### Found in passing

- **A copy of a grant can come back in another way.** A grant signed on another machine, by an authority trusted here for grants, may name an authoriser that only this machine revoked. If it isn't recorded in this store's `grants`, nothing pins that authoriser's revocation. Once the authoriser's credential expires and `meep trust tidy` takes out its revocation, a copy of the grant carried in a message would count again. It's the same question as Q276, which grants count, in the to-do list. I left it until grants from other machines are in use.
- **Revocations written before this change have no end date,** so `meep trust tidy` never takes them out. Revoke the same thing again to get one, after first deleting its line by hand.
