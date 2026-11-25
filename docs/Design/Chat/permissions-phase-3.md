# Permissions, phase 3: `Verify`

Written 24 November 2026. Phase 3 of the plan in `permissions.md`: the `Verify` verb, with its three inflections for credentials, certificates and permissions, proof of possession for requests, and the trust store started in the daemon and `meep run`. The work is on the branch `permissions-phase-3`, made from `main` at dc7afec, not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q280, for you to answer inline.

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 853 of 853 (823 before, and 30 new tests, counting each case of a test with arguments). Three full runs, all 853 of 853; none of the known flaky tests failed.
- No test touches the real `~/.meep`: each uses a temporary home folder.
- On strawberry.local (linux-arm64), the new tests pass, 30 of 30, as do phase 2's store tests (15) and the credential tests (11), once the tests' umask is 022. With the Pi's own umask, 002, the store refuses the tests' temporary folders, phase 2's tests included (see "Found in passing" and Q284). I copied a test build to `~/meep-phase3-tests` and ran it there; the installed daemon wasn't touched.

## What was built

### The verb

`Verify` lives in `Meep2.Runtime` (Q266 A), in `src/Meep2.Runtime/Verifying/`, since only the runtime can make a `VerifiedMessage`. The registry gets it from `RuntimeModules.Assembly`, added beside `CoreModules.Assembly` in the daemon, `meep run`, `meep lsp` and the tests. Its three inflections share one parameters record, and each always emits a `VerifiedMessage`: a failed check is a result, never an error.

| Inflection | Checks | Proof |
|---|---|---|
| `Verify.Credential` | A user's SSH certificate: an authority in `authorities` trusted for `meep-credential` issued it, its signature is that authority's, it's in date, it has no critical option, and it isn't revoked (serial, key, authority's key, KRLs). Then the grants that apply: those recorded in the store, and those carried among the message's ancestors, each signed by an authority trusted for `meep-grant`, in date and not revoked. | A signature over the request (below), or with `bearer=true`, a credential carrying `permit-bearer@meep`. Otherwise `NoProofOfPossession`. |
| `Verify.Certificate` | An X.509 chain to any certificate in `roots/`, every certificate on it in date, allowed to do what it does, and not revoked by the `revoked` file or a CRL; and every issuer with a CRL in the store has one in date, or `RevocationUnknown` (Q274). | A TLS handshake (`ProvenByTls`, set in phase 7), or `bearer=true`. |
| `Verify.Permission` | Well defined, always: defined, no looser than its definition, and everything it refers to exists. Met, when the message has a verified credential among its ancestors or `against=credentials` is written: every line of the message's family passes through a genuine, passing, unexpired credential verification that holds it, conferred by an authority it allows, with everything it requires, each with its own definition's authorities. | None. |

Parameters:

| Parameter | For |
|---|---|
| `from` | What to verify, read from the message, such as `msg.Headers["Meep-Credential"]`. Without it, the message itself if it's a `Credential`, `Certificate` or `Permission`, or else its text. |
| `bearer` | Takes a credential without proof, if its issuer allows it too; a certificate without a TLS handshake. |
| `window` | How far from now a request's signature may have been made, either way, and how long its nonce is remembered: 60 s unless set, at most an hour. |
| `audience` | The names a signed request may be addressed to, as `host` or `host:port`. Without it, this machine's names and addresses, on any port. |
| `usage` | What a certificate must be allowed to do: `client` (the default), `server` or `any`. |
| `against` | `auto` (the default), `definition` or `credentials`, for permissions. |

A passing verification expires when the earliest thing it relied on does: the credential, a grant, a certificate on the chain, or the next update of an issuer's CRL (Q259). It records the store's `Version` and `RevocationVersion`. A failed one keeps no permissions, grants or proof, and doesn't expire early, so a pipeline can still answer it.

### Choosing an inflection

Whether `Verify` has a credential, a certificate or a permission depends on the message, or on what `from=` reads from it, and neither the message's type nor the parameters alone can say. So each inflection takes `Message`, and a new SDK attribute, `[ChosenForEachMessage]`, has the loader ask its `WhyNot` for every message rather than once at load. The three divide every message between them, by the form of the text:

- PEM, or base64 that decodes to DER, is a certificate;
- a `#permission` line or a permission's name is a permission;
- anything else, an SSH certificate included, is a credential, and fails as `Malformed` if it isn't one.

An SSH certificate's base64 is recognised before a permission's name, since a long one could look like a name. The choice is cached for the message, since the three ask in turn. `Verify.Permission`, say, can still be named outright.

### Getting credentials in

- `Verify from=msg.Headers["Meep-Credential"]` reads the base64 alone, or `type base64 comment` as a `-cert.pub` file has it.
- Without `from=`, a message with text is read as text, so `Read "~/me.credential"` feeds `Verify bearer=true` directly. That's the "credential reader for files" the plan asked for, without a new verb.
- A `from=` that can't be worked out, such as a header that isn't there, gives a `Malformed` failure, "There's nothing to verify: …", not an error, so a 401 can be sent.

### Proof of possession: request signatures

As Q253's proposal describes. A request is signed if the message, or its nearest ancestor that's an HTTP request, has `Signature-Input` or `Signature`:

```
Meep-Credential: <base64 SSH certificate>
Content-Digest: sha-256=:…:
Signature-Input: meep=("@method" "@target-uri" "@authority" "content-digest");created=1732370000;nonce="r4nd0m";keyid="SHA256:…"
Signature: meep=:<binary SSHSIG, base64>:
```

- The signature base is built as RFC 9421 section 2.5 says, from the components listed, with `@signature-params` written back from what was read. A small reader for RFC 8941's structured fields reads the headers.
- It must cover `@method`, `@target-uri` and `@authority`, and `content-digest` if there's a body. `@scheme`, `@path`, `@query`, `@request-target` and headers may be covered too.
- The signature is an SSHSIG in the `meep-request` namespace by the credential's own key, so `ssh-keygen -Y sign -n meep-request` makes one by hand. A test signs with `ssh-keygen` itself.
- `created` must be within the window of now, either way, and `expires`, if given, not passed. The nonce must be unseen: nonces are kept for the whole process, by credential key and nonce, until their window has passed, at most a million at once.
- The authority must be in the audience.
- `Content-Digest` (`sha-256` or `sha-512`) must match the body.
- Failures: `NoProofOfPossession` (unsigned, or not covering enough, or by another key), `BadSignature`, `StaleSignature`, `Replayed`, `WrongAudience`, `BodyMismatch`.

### Security keys (Q271)

`PublicKey` now reads `sk-ssh-ed25519@openssh.com` and `sk-ecdsa-sha2-nistp256@openssh.com` keys, and checks their signatures. These cover a hash of the application, the flags, the counter and a hash of the data. A signature counts only with the user-presence flag set, as OpenSSH insists. A credential on a YubiKey can then sign requests. Plugins' and pipelines' signatures can be made with one too, since they go through the same `PublicKey`.

### The trust store, started

`PipelineOptions.Trust` is a `TrustStoreSource`. It's either one fixed store, for tests and embedding, or this machine's, kept current by a `TrustStoreWatcher` started the first time it's asked for.

- **The daemon** asks when it starts, so a store that can't be read is logged then. Its pipelines all share one.
- **`meep run`** asks only when a pipeline first verifies something, so other pipelines neither make `~/.meep/trust` nor watch it.
- **A store that can't be read** is logged and tried again when next wanted, ten seconds later at the soonest. Meanwhile every verification fails, saying there's no trust store, as does one in a pipeline given none.

### Smaller things

- **Grants on verifications.** `VerifiedMessage.Grants` (new, internal to set) holds the grants that gave a credential some of its permissions. "Met" needs it, to know which authority conferred each permission.
- **`RevocationUnknown`** joins `VerificationFailureCode` (Q274).
- **`IHttpRequest`** in the SDK: `HttpRequest`'s method, address and headers, with its body through `IHasBody`. `Verify` checks a request with it, as the runtime can't see the core modules' types.
- **`SshSignature.FromBinary` and `ToBinary`** read and write SSHSIG without the armor, as grants and request signatures carry it.
- **Docs.** `docs/verbs.md` has a Security section for `Verify`. `docs/writing-verbs.md` has `IHttpRequest` and `[ChosenForEachMessage]`. The runtime now makes an XML documentation file, so `meep lsp` can describe `Verify` and its parameters.

### The tests

`VerifyTests` (30 with their cases), using a `VerifyLab` (a trust store in a temporary home, an SSH CA from `CredentialLab`, a key for grants and a key for the subject), a `RequestSigner`, a `TestSecurityKey` made in memory, and a `TestCa` that makes roots, intermediates, leaves and CRLs:

- **Credentials.** A bearer credential read from a file, through Meeplang, passes with its own permissions and a recorded grant; it records the store's versions and lapses with the grant. Proof is refused unless both the pipeline and the issuer allow a bearer. There's a failure for each fault: unknown authority, bad signature, expired, not yet valid, revoked, a host's certificate, a critical option. Grants count when recorded or carried, and not when untrusted, out of date or revoked. What can't be read, and a pipeline with no store, are results, not errors.
- **Request signatures.** A signed request passes once and is refused as a replay after, with and without a body. Signing with `ssh-keygen -Y sign` works. There's a failure for each fault: made too long ago or in the future (and passing with a wider window), the wrong audience (and passing with `audience=`), a changed body, a changed method, the wrong namespace, another key, too little covered, a missing header or nonce. A bad signature leaves its nonce unused. A security key passes only when touched. A real `Listen` passes a request signed by `HttpClient`'s headers and refuses one that isn't.
- **Certificates.** A chain through an intermediate passes with `bearer=true` and with a TLS mark, and fails without either. An intermediate in `roots/` is an anchor without its parent. There's a failure for each fault: untrusted root, missing intermediate, expired, not yet valid, the wrong usage (and passing with `usage=`), revoked by CRL and by hash. An out-of-date CRL gives `RevocationUnknown`, and a fresh one bounds the expiry.
- **Permissions.** Well defined, unknown, wider, a directive narrowing, and `against=credentials` with no credentials. Met by a verified credential, through requirements, refused when the credential lacks it or the wrong authority conferred it, and `against=definition`. Every line must carry it: a family that joins an anonymous line isn't met. A copy without the original's ancestry isn't genuine. A revocation after the verification is seen.
- **The rest.** Meeplang chooses all three inflections from files of each kind. What text is decides the inflection, nine cases. Structured fields are read and written back as RFC 9421 signs them. Security-key signatures need a touch. The trust store is read only when first wanted, and a broken one is reported once.

`PluginTests` had one test that relied on security keys being refused. It now uses an `ssh-dss` key, which still is.

## Decisions I made while building

- **One parameters record for all three inflections**, since the loader binds a line's parameters once for inflections that share a record. Each ignores the parameters that aren't for it.
- **`[ChosenForEachMessage]`** rather than inflections with different input types. With `Verify.Credential` taking `Credential` and so on, a credential read from a header would reach none of them. The loader change is one condition. A verb whose inflections between them take every message never fails to choose.
- **The message's own text when there's no `from=`**, so a file reads as a credential, certificate or permission with no new reader. A request's body is text too, so `Verify` with no `from=` under a Listen reads the body. Say so if that's a surprise (Q286).
- **A signed request is checked even with `bearer=true`.** A bad signature fails rather than falling back to bearer use, so a client can't get round a check by sending a broken one.
- **Signature details.** The label is `meep`, or the only label if there's one. `keyid`, if given, must be the credential's key fingerprint. The nonce is used only once the signature is known good, so nobody can use up someone else's nonces. A streamed body fails as `BodyMismatch` rather than being waited for, since `Verify` would have to read the whole body before passing it on (Q253 left it open).
- **The audience is this machine's names and addresses, on any port, unless `audience=` says.** The request's `@authority` comes from its `Host`, which the client chose. So a request signed for another server and passed on here only verifies if the attacker keeps that server's name, and the audience check refuses it. Ports aren't checked by default, since the `Host` port a client sends needn't be the one Meep listens on behind a proxy.
- **Bad grants are left out quietly.** A grant that's untrusted, badly signed, out of date or revoked doesn't fail the credential: it just gives nothing (Q283).
- **A verification expires at its earliest grant's end,** taking the credential's own permissions with it then, since a `VerifiedMessage` can only have one expiry. It still lists every permission until then.
- **Certificates are anchored by Meep.** .NET builds the chain with only `roots/` trusted. Meep then cuts it at the first certificate that's in `roots/`, and counts only what's wrong up to there, ignoring "untrusted root" and "partial chain". .NET never stops at an intermediate itself (see "Found in passing"), so this is how Q275 works on every system. Revocation is never checked online: `RevocationMode` is `NoCheck` and downloads are off, and only the store's lists count.
- **A certificate with `bearer=true` needs no consent from its issuer,** as X.509 has no `permit-bearer@meep` (Q281).
- **A permission written as text narrows its definition,** as a pipeline's `#permission` does: `from="ReadNotes"` means ReadNotes as defined. It keeps the definition's authorities and adds its own requirements. A `Permission` message is checked as it stands, since it claims its own requirements, and one with no authorities where the definition has some is wider.
- **"Met" re-looks at a verification made before the store last took something away:** the credential and its grants are checked for revocation again (Q259). The authority that conferred each permission is always looked up in the store as it is now, so an authority removed since confers nothing.
- **Requirements are held as their own definitions say:** a requirement is met only if conferred by an authority its own definition allows, with its own requirements, recursively.
- **No pipeline credential yet.** The brief listed a pipeline's own credential, proven when it loads, under proof of possession. But Q252's answer says it lands with phase 6, with the `meep.*` permissions, so I left it there.
- **Listen's `signature=required` isn't built.** Q253's answer has the pipeline author mark proof as required on the receiving verbs, and a Listen refuse requests without it. That's Listen's side, which the brief didn't assign to this phase, and its TCP and WebSocket parts are phase 7's. See Q280.

## Found in passing

- **.NET's custom root trust never ends a chain at an intermediate**, on macOS or on linux-arm64. A probe built a leaf's chain with only its issuing intermediate in `CustomTrustStore`, and both gave `PartialChain`. So Meep's own anchoring, above, is what makes an intermediate in `roots/` work, on both.
- **The Pi's umask is 0002** (Debian's user private groups), and its `~/.meep` is 775. Phase 2's store refuses a store whose parent folder is writable by its group, so the trust store on strawberry.local would be refused, with `meep trust init` included. The tests' temporary folders are made with the same umask, so phase 2's store tests fail there too. See Q284.
- **A copy of a `VerifiedMessage` descends from the original**, as every `with` copy's parent is the original. So a line through a copy still reaches the genuine one, and counts, which is right. A copy given other parents, `with { Parents = … }`, doesn't.
- **A `Computed` made in code with `From` can throw anything**, not just `EvaluationException`, as a dictionary lookup does. `Verify` catches any exception from `from=` and reports it as nothing to verify.
- **The runtime's existing doc comments** had 21 methods documenting only some parameters and one `cref` that can't be resolved. Those only warn once a documentation file is made, so `Meep2.Runtime.csproj` silences CS1573 and CS1574 with a note. Filling them in is a small tidy-up of its own.
- **`ssh-keygen -s` will certify a security key's public key without the key present.** The tests use that to issue a credential for a key made in memory.
- **`@target-uri` behind a TLS-terminating proxy:** the client signs `https://…`, but Meep sees `http://…`, so the signature fails. See Q285.

## Questions

**Q280.** Listen's side of Q253: the author marks proof as required, and a Listen refuses requests without it before the pipeline. A Listen can't verify fully, as it has no trust store and `Meep2.Modules` can't make verifications. Which?
- A: `Listen.HTTP signature=required` refuses, with 401, requests that have no `Signature-Input`, `Signature` and `Meep-Credential`. `Verify` does the real check. Cheap, and stops anonymous traffic at the door.
- B: The same, but Listen also checks the signature and freshness, through a narrow service the runtime gives it (as Q266 C does for the TLS mark).
- C: Leave it to `Verify` and `@permission` gates.

I'd do A, in phase 4 beside the gates, with the TCP and WebSocket challenge in phase 7.

**Answer:** A in phase 4.

**Q281.** An X.509 certificate with `bearer=true` is taken without proof, on the pipeline author's word alone, since X.509 has no `permit-bearer@meep`. Is that enough, or should the certificate also have to carry something, such as a policy OID of Meep's, for its issuer to agree? I'd leave it, since `Verify.Certificate` will mostly see certificates proven by mutual TLS once phase 7 is done.

**Answer:** Leave it.

**Q282.** The default audience is this machine's names and addresses, which won't include a public DNS name or a name a proxy answers to. Then every Verify behind them needs `audience=`. Should the daemon's settings have a host-wide list, as `meep-hosts` is for origins, with `audience=` adding to it? I'd add a `meep-names` setting when the daemon's own API starts verifying requests (phase 7).

**Answer:** Add `meep-names` in phase 7.

**Q283.** A grant that's untrusted, badly signed, out of date or revoked is left out without a word. Should the `VerifiedMessage` say which grants were left out and why, in a list of notes that aren't failures, so someone wondering why a permission is missing can see? I'd add it; it costs little, and taps would show it.

**Answer:** Yes.

**Q284.** On Debian and its relatives, the umask is usually 002 and each user has a group of their own, so `~/.meep` is made 775. Phase 2's check, like stock OpenSSH's, refuses a store whose folder or parent is writable by its group. So the trust store doesn't load on strawberry.local as it stands. Which?
- A: When Meep makes `~/.meep` or the trust folder, it makes them 700, and `meep trust init` narrows an existing `~/.meep` to 700, saying so.
- B: Accept a folder writable by its group when that group is the user's own and has no other members, as Debian's OpenSSH does.
- C: Leave it, and make the message say how to fix it (`chmod 700 ~/.meep`).

I'd do A and B together. Separately, the tests' `TemporaryFolder` should make its folders 700, so the store tests pass under any umask; that's a two-line change to test code I'd make with whichever you choose.

**Answer:** A and B, then.

**Q285.** A signature must cover `@target-uri`. Behind a proxy that ends TLS, the client signs `https://example.com/notes`, but Meep sees `http://…`, so it fails. Should `@path` and `@query`, with `@authority`, be accepted in place of `@target-uri`? That covers everything but the scheme, which TLS to the proxy covers anyway. I'd accept either.

**Answer:** Either.

**Q286.** Without `from=`, `Verify` reads the message's text, so `Read "~/me.credential"` works directly, but a `Verify` straight under a `Listen` reads the request's body. Keep that, or require `from=` for anything that isn't a `Credential`, `Certificate` or `Permission`, with a `Read` that emits a `Credential` for `.credential` files? I'd keep it: it's one rule, and a body that isn't a credential fails as `Malformed`, saying what it found.

**Answer:** Keep as-is.

**Q287.** Nonces are remembered in memory, so after a daemon restart, a request signed in the last minute could be replayed once. Accept that, or keep the nonces on disk? I'd accept it: the window is short, and a restart is visible in the log.

**Answer:** Accept. It might be something to toggle in settings later, but not now.

**Q288.** Every critical option fails a credential, including OpenSSH's `source-address`, which Meep could honour against the request's address: a credential that only works from the home network. Add it, in phase 4 or later? I'd add it later, when someone wants it.

**Answer:** Maybe add it later, but I'll ask for it specifically if it turns out to be necessary. This needs to be noted in a troubleshooting guide, though.

## After your answers

Added 24 November 2026. What each answer changed. Still on `permissions-phase-3`, not committed.

- The build has no warnings, and the whole suite passes: 856 of 856 (853 before, and 3 new tests; several existing ones gained checks).
- On strawberry.local, with its own umask of 002 this time, the security tests pass: 72 of 72 (Verify, the credential, trust store, trust command and privacy tests). The test copy is deleted again, and the installed daemon wasn't touched.
- **Q280 (A in phase 4).** Added to phase 4 in `permissions.md`.
- **Q281.** Left as it is.
- **Q282.** `meep-names` added to phase 7 in `permissions.md`.
- **Q283 (yes).** `VerifiedMessage.Notes` lists what was found that isn't a failure: for now, each grant that applies to the credential but was left out, with why, as in "The grant … expired at 17:00 UTC on 3 March 2026. So it doesn't give WriteNotes." A failed verification keeps its notes, though not its permissions or grants.
- **Q284 (A and B).**
  - A: `PrivateFolders.Create` already made missing folders 700, `~/.meep` included, but a Git source's cache folder was made with `Directory.CreateDirectory`, which could make `~/.meep` at the umask's mode. It now uses `PrivateFolders.Create`. `meep trust init` narrows `~/.meep` and `~/.meep/trust` to 700 if others can reach into them, before it loads the store, and says so, so it's the fix for a store that won't load. `PrivateFolders.Narrow` does this, and says whether it had to.
  - B: `PrivateFolders.OthersCanWrite` accepts a group-writable file or folder, on Linux, when it's the user's and its group has exactly one member, the user, by primary group or by being listed: Debian's OpenSSH rule. `UserPrivateGroups` asks the system (`stat` through .NET's own native layer, as `FileKinds` does, and `getgrgid` and `getpwent` from libc), and treats anything it can't find out as shared. On macOS, where a user's group is `staff`, nothing changes. This also applies to plugin signers' `allowed_signers`, which use the same check.
  - The tests' `TemporaryFolder` makes its folders 700. Two tests that used a group-writable file to show refusal now use an other-writable one, since a group-writable file is accepted on the Pi. A new test checks the group rule against what `id` and `getent` say, and another that `meep trust init` narrows a 777 `~/.meep`.
  - The Pi's own `~/.meep` (775, group `cwenham` with no other members) would now load as it is.
- **Q285 (either).** A signature may cover `@path` and `@query` in place of `@target-uri`, with `@method` and `@authority` as before. One of `@path` or `@query` alone isn't enough. Tested with a request signed at `https://…` and seen at `http://…`.
- **Q286, Q287.** Kept as they are.
- **Q288.** Not added. `docs/troubleshooting.md` is new: it explains `UnknownCriticalOption` and `source-address`, and the other failures someone is likely to meet (a store that won't load, a proxy, the audience, clocks, replays, bodies, out-of-date CRLs and missing grants). `docs/verbs.md` links to it from `Verify`, and mentions `@path`, `@query` and `Notes`.
