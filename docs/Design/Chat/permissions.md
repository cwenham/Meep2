# Credentials, certificates and permissions

Written 23 November 2026, on the branch `permissions` from `main` at 3e17062. A plan for making credentials, certificates and permissions messages in Meeplang, checked by a `Verify` verb, granted by an `Authorise` verb, and required by `@permission=Name`. Nothing is built yet. Questions are numbered from Q251; please answer inline.

## The idea, as I understand it

- Credentials, certificates and permissions are messages. They can be made, named, passed around and verified freely, and mean nothing until a message reaches something gated.
- There are no users. The message is the user: a message may reach a gated module if a successful verification of a credential is among its ancestors (fam), and that credential carries the permission the module needs.
- `Verify` checks a credential, certificate or permission and emits a `VerifiedMessage` saying whether it passed, with every reason it didn't.
- `Authorise` adds a permission to a credential, and is itself gated by `AuthorisePermission`, which can't be removed. It can also add certificates, gated by `AuthoriseCertificate`.
- Gates come in two kinds: modules written with `@permission=Name`, and things inside Meep that are restricted, such as reading and writing files.
- Systems of authorisation are built from these pieces in Meeplang, not built into Meep.

## What's already there to build on

- **Signing:** Ed25519 SSH signatures (`Meep2.Plugins/Signatures`: `SshSignature`, `PublicKey`, `TrustedKeys`, `allowed_signers` with namespaces), used for plugins, pipelines and pending commits. BouncyCastle is already a dependency.
- **X.509:** only loading a Listen's server certificate (`Meep2.Modules/Certificates.cs`). .NET's `X509Chain` with `X509ChainTrustMode.CustomRootTrust` does browser-style chain building against roots we choose, and `CertificateRevocationListBuilder.Load` reads CRLs, so we can check revocation ourselves without relying on each OS's revocation behaviour.
- **Inflections by message type:** `Verify` fits this exactly: `Verify.Credential`, `Verify.Certificate` and `Verify.Permission`, chosen by what arrives.
- **Modifiers:** `@buffer`, `@bypass`, `@errors` and the rest are read in `OptionsBuilder.With` into `NodeOptions`, which the runtime consults when delivering. `@permission` is one more.
- **Errors:** a message no inflection takes already becomes an error that follows the error policy. A refused message can do the same.
- **Expiry (goal 2):** a message expires no later than its parents. A `VerifiedMessage` that expires when the credential does makes a verification lapse by itself, with no new machinery.
- **Daemon capabilities:** `observe`, `inject`, `control` on tokens (Q40, Q63). These become permissions later (phase 6).

## The plan

One branch per phase, each with its own `docs/Design/Chat/permissions-phase-N.md` for what was built, decisions and new questions, as before.

### Phase 1. The message types (SDK)

A new namespace, `Meep2.SDK.Security`:

| Type | Holds |
|---|---|
| `Credential` | a subject name, the subject's public key, permission names, valid-from and valid-until, a serial, the issuing authority's key and its signature |
| `Grant` | adds a permission to a credential after it was issued: the credential's serial and key, the permission, validity, and the authority's signature (see Q254) |
| `Certificate` | an X.509 certificate (DER) and the intermediates sent with it |
| `Permission` | a name, and what it requires: which authorities may confer it, and optionally other permissions a credential must also have |
| `VerifiedMessage` | what was verified (as its parent), its kind, `Success`, `Failures`, the permissions it established, when it was checked, against which trust store, and which revocation list (by version) |
| `VerificationFailure` | a code (`Expired`, `NotYetValid`, `UnknownAuthority`, `BadSignature`, `Revoked`, `ChainIncomplete`, `UntrustedRoot`, `WrongUsage`, `UnknownPermission`, `MissingRequirement`, `NoProofOfPossession`, …) and a sentence for people |

**Forgery is the main risk.** If anything other than `Verify` can make a `VerifiedMessage` with `Success = true`, the whole scheme falls. So:

- Its constructor is internal to the SDK, reachable only by the runtime's `Verify` (through `InternalsVisibleTo`, as the runtime already is for stamping).
- A gate accepts one only if it was emitted by a `Verify` module of the same running pipeline (or of a pipeline the daemon vouches for), checked by the `ModuleInfo` reference the runtime stamps, not by a name.
- `MessageJson` never reads one in: injected messages, WebSocket messages and messages from other machines can't carry a verification, only the things to verify. A remote Meep re-verifies.
- Embedded scripts (the other session's work) can read these types but not construct them.
- Plugins run in the same process and could get round any of this with reflection; plugins are already trusted code (signed or pinned by SHA), so the trust boundary stays the process. I'll write that down in `writing-verbs.md`.

### Phase 2. The trust store and `meep trust`

`~/.meep/trust/`, kept to the user (700 folders, 600 files, as S7 settled):

- `authority.key`: the daemon's own Ed25519 authority key, made by `meep trust init`. `Authorise` signs with it.
- `authorities`: keys trusted to issue credentials, in `allowed_signers` form with a `meep-credential` namespace, so `TrustedKeys` reads it unchanged.
- `roots/`: root CA certificates trusted for `Verify.Certificate`.
- `revoked`: revoked credential serials and keys, and CRL files for certificates.
- `permissions.meep`: permission definitions (Q257).

The store is loaded once and reloaded when its files change, as `PipelineWatcher` does, bumping a version number that verifications record.

CLI: `meep trust init`, `meep trust add-authority`, `meep trust add-root`, `meep trust revoke`, `meep trust issue` (make a credential for a key, for bootstrapping), `meep trust list`.

### Phase 3. `Verify`

Three inflections, chosen by message type, all emitting `VerifiedMessage` and never an error for a failed check (a failure is a result, not a fault):

- `Verify.Credential`: is it signed by an authority in the store, within its dates, not revoked; which `Grant`s apply; and proof of possession (Q253).
- `Verify.Certificate`: `X509Chain` with custom root trust to a root in `roots/`, every certificate in date, key usage right, none revoked by the CRLs in the store. Reasons come from `X509ChainStatus`, translated.
- `Verify.Permission`: the permission is defined, and its requirements are met by the credentials verified among the message's ancestors (Q257).

Getting credentials into a pipeline: a `text=` or `from=` parameter reads one from part of a message, as in `Verify from=msg.Headers["Meep-Credential"]`, and a Listen with client certificates (mTLS) attaches the client's certificate. Plus a `Credential` reader for files, so `Read "~/me.credential"` works.

A `VerifiedMessage` expires when the earliest thing it relied on does.

### Phase 4. `@permission` gates

- `@permission=Name` (several allowed, all required) is read into `NodeOptions.Permissions`.
- The runtime checks before delivering: walk the message's fam for successful, genuine `VerifiedMessage`s whose established permissions include each name, and check the revocation list hasn't changed under it (Q259). Only gated modules pay; the answer is cached on the message for each gate.
- A refused message becomes a `PermissionDenied` error carrying the reasons, following the module's error policy, and is counted in the module's stats and in telemetry, so refusals are visible in `meep` and Synesthesia (Q258).
- `@permission` on a source means the pipeline must hold it to start (Q252). Combined messages are covered by Q261.
- An SDK attribute, `[RequiresPermission("Name")]`, lets a verb (built-in or plugin) declare a permission that can't be removed, and `context.Demand("Name", message)` checks one in code for finer gates, such as Fetch.File only outside its `root`.
- The loader shows gates in `meep lsp` hovers and in the graph, so a drawing can put a lock on gated modules.
- `Listen.HTTP proof=required` refuses, with 401, a request that has no `Signature-Input`, `Signature` and `Meep-Credential`, before it reaches the pipeline; `Verify` does the real check (Q280, answered in phase 3's doc). The TCP and WebSocket challenge waits for phase 7.

### Phase 5. `Authorise`

- `Authorise.Permission`: given a credential (verified, among the message's ancestors) and a permission name, emits a `Grant` signed by the daemon's authority key. `[RequiresPermission("AuthorisePermission")]`.
- `Authorise.Certificate`: `[RequiresPermission("AuthoriseCertificate")]`; what it does depends on Q255.
- Whether the grant is also saved, so it's in force after a restart, is Q256.
- A `Revoke` verb, gated by `RevokePermission`, to match (Q262).
- Added 25 November, from phase 4's answers:
  - A `Parse` verb turns text into a `Credential`, `Certificate` or `Permission`, or an error if it's none of them, using one reader in the SDK that `Verify` shares. `Authorise` and `Revoke` take typed messages only, so their inflections are chosen by type, before the gate (Q293).
  - A refusal at a gate gets a short span of its own, named after the gated module, so every refusal is in the trace, including one straight after a source (Q298).
  - Gates that need a kind of proof, as in `@permission=meep.files.write proof=signed`, so a bearer credential can't reach them (Q299).

### Phase 6. Meep's own restricted functions

Built-in permissions for what Meep does on the machine, under a `meep.` prefix so they can't clash with names people make:

| Permission | Gates |
|---|---|
| `meep.files.read` | Read, Fetch.File, Learn/Classify with `file=` |
| `meep.files.write` | Save, Learn's saved trees, Forget |
| `meep.network.listen` | every Listen |
| `meep.network.send` | Fetch.HTTP, Send.HTTP/TCP/WebSocket |
| `meep.scripts.run` | embedded scripts (with the scripting session) |
| `meep.daemon.observe`, `.inject`, `.control` | today's token capabilities |

Turning these on everywhere would break every existing pipeline, since a source's messages have no ancestors and so no credentials. My proposal is in Q252: a pipeline carries a credential of its own, so its sources' messages start with one.

### Phase 7. The daemon and Synesthesia

- Tokens become credentials (or carry one), and capabilities become `meep.daemon.*` permissions. A client's requests are verified as messages are.
- Injected messages get the injecting client's verification as an ancestor, so a dashboard can inject only what its credential allows.
- Mutual TLS for remote clients (Q63's later step), with client certificates checked by `Verify.Certificate`.
- Synesthesia presents a credential when it connects and signs its commands.
- A `meep-names` setting in the daemon's settings lists the names this host answers to, such as a public DNS name or a proxy's, and `Verify`'s `audience=` adds to it (Q282).

## Ideas while planning

- **Attenuation, as macaroons do.** Let anyone holding a credential narrow it without an authority, by adding signed caveats ("only `meep.files.read`", "only under `~/notes`", "only until 5 pm"). A script could then be handed a credential that can do less than its pipeline's. A nice experiment after phase 5.
- **Scoped permissions.** `meep.files.write:~/notes/**` rather than all files (Q260).
- **Verification as audit.** Every `Verify` and every refusal as a span event with its reasons gives an audit trail through the OpenTelemetry already in place, for free.
- **OpenSSH certificates for credentials.** An SSH user certificate already is "a key, signed by a CA key, with names, dates, a serial and extensions", revoked with KRLs, and `ssh-keygen -s` can issue them today. We already read and write SSH formats. See Q251.

## Questions

**Q251.** What should a credential be underneath?
- A: Meep's own signed record (Ed25519, canonical JSON or CBOR), like our pipeline signatures. Simplest, and exactly fits our fields.
- B: OpenSSH certificates (`ssh-ed25519-cert-v01`): names as principals, permissions as extensions, revocation by KRL, and `ssh-keygen` can already issue and inspect them.
- C: X.509 for both credentials and certificates, a credential being a certificate signed directly by an authority.

I lean to B, because the tools exist and we already handle SSH formats, with `Grant`s as Meep's own signed records.

**Answer:** B

**Q252.** Messages from sources have no ancestors, so they carry no credentials. How should they get one?
- A: Every pipeline has a credential: the key it's signed with (goal 7), or for an unsigned pipeline from your own disk, the daemon owner's; a pipeline from a URL with no signature gets none. Every message a source emits descends from the pipeline's verification.
- B: Sources carry none, and `meep.*` permissions are only enforced for pipelines that opt in, such as with `#restricted`.
- C: Both: A, and a pipeline can be told to start with less with a directive.

I recommend C. It also answers Q40 ("pipelines are granted specific permissions") with the same mechanism.

**Answer:** C

**Q253.** Proof of possession. A credential is public, like a certificate: anyone who has a copy can present it. Should `Verify.Credential` require the message to prove it holds the credential's private key (a signature over the request, as in HTTP Message Signatures, RFC 9421, or mTLS at the Listen), or accept bearer credentials too, perhaps only when marked as such? I recommend requiring proof for anything arriving from outside, and bearer credentials only inside a process.

*Proposal, added 23 November after Q251 (B: SSH certificates).* A credential's key can stay in ssh-agent, the Secure Enclave or a YubiKey; the sender proves it holds the key by signing something fresh with it, and `Verify.Credential` checks that signature as well as the certificate. The signature binds **this message** (method, address, body digest), **now** (a creation time and a nonce, so it can't be replayed) and **here** (the server it's for, so it can't be passed on).

- **HTTP, the daemon's API and Synesthesia:** HTTP Message Signatures (RFC 9421) with a body digest (RFC 9530):
  ```
  Meep-Credential: <base64 SSH certificate>
  Content-Digest: sha-256=:…:
  Signature-Input: meep=("@method" "@target-uri" "@authority" "content-digest");created=1732370000;nonce="r4nd0m";keyid="SHA256:…"
  Signature: meep=:…:
  ```
  `Listen.HTTP` checks nothing and passes the headers on. `Verify.Credential` checks the certificate against the authorities and revocations, the signature with the certificate's key, that `created` is within a window (about 60 s) and the nonce unseen within it, and that `@authority` is this Listen's own address. The signature is an SSHSIG in a `meep-request` namespace rather than raw Ed25519, so `SshSignature` is reused and `ssh-keygen -Y sign` can make one by hand; a `meep request` command, and Synesthesia, do it for you. New failure reasons: `NoProofOfPossession`, `StaleSignature`, `Replayed`, `WrongAudience`, `BodyMismatch`. A streamed body (`stream=true`) can't be checked against its digest until it has all arrived, so `Verify` waits for it or refuses it.
- **TCP and WebSocket:** a challenge when connecting rather than a signature on every message. The Listen sends a nonce; the client answers with its certificate and a signature over the nonce, the Listen's address and, under TLS, the TLS exporter value (RFC 9266), which ties the proof to that TLS session so it can't be relayed. The proof is kept with the connection's lease, and `Verify.Credential` accepts messages carrying that lease. Without TLS (`ws://`, `tcp://`), someone in the middle could take over a connection after the handshake, and the verification says so. Signing every message is the alternative for links with few messages.
- **Mutual TLS:** the handshake is the proof. A Listen that requires client certificates attaches the peer's certificate as a `Certificate` message marked as proven by TLS, a mark only the runtime can set, as with `VerifiedMessage`. This is for X.509 and `Verify.Certificate`. (Not built yet: Listen.HTTP and Listen.WebSocket only present a server certificate, through Kestrel's `UseHttps`, and the daemon is reached by its local socket and tokens.)
- **Inside the process:** a pipeline's own credential (Q252, C) is proven when it loads: by the pipeline's signature if it's signed, or for your own unsigned file, by the file being yours and the daemon running as you, the peer check its socket already makes. A credential read from a file is a bearer credential, allowed only when the `Verify` says `bearer=true`, so it shows in the pipeline, and the certificate carries a `permit-bearer@meep` extension, so its issuer agreed. Otherwise it fails with `NoProofOfPossession`.
- **What gates see:** `VerifiedMessage.Proof` is `None`, `Bearer`, `RequestSignature`, `Connection` or `Tls`. A gate could later ask for more, as in `@permission=meep.files.write proof=signed`; I'd leave that for later unless you want it from the start.
- **Costs:** each Ed25519 check takes tens of microseconds, fine for each request but the reason streams use a challenge at connect; and the nonce store holds every nonce for the length of the window.

**Answer:** Let's go with your proposal, where the pipeline author can mark it as required in the parameters for the message receiving verbs.

**Q254.** Messages don't change, so "adding a permission to a credential" makes something new. Should `Authorise` issue a separate `Grant` that refers to the credential (my recommendation: no reissuing, and grants can be revoked alone), or a whole new credential with the permission added?

**Answer:** No reissuing, and grants can be revoked alone

**Q255.** What does "add certificates" with `AuthoriseCertificate` mean?
- A: Trust a new CA or root, adding it to the trust store.
- B: Issue a certificate: sign a certificate request with the daemon's CA.
- C: Something else.

**Answer:** A

**Q256.** Does a `Grant` from `Authorise` take effect only where it's carried (it's a message, and only counts where it's among a message's ancestors or presented with the credential), or is it also recorded in the trust store so it applies whenever that credential is verified later? Recording it is what most people would expect "authorise" to mean; carrying it keeps everything as messages.

**Answer:** Recorded in the trust store.

**Q257.** "Verification of a permission would be against a list of permissions and their required credentials." Where does that list live, and what does a definition say? My guess: `permissions.meep` in the trust store, and `#permission` directives in a pipeline that can only narrow, never widen, something like
```
#permission ReadNotes requires authority="Home CA" and permission=meep.files.read
```
And does `Verify.Permission` succeed when the permission is well defined, or only when the message's own credentials meet it?

*Proposal, added 24 November.* A permission is **well defined** when the `Permission` message is sound in itself, whoever is asking:

1. Its name is in the trust store's permissions file, or narrows one that is, through a pipeline's directive.
2. It claims no looser requirements than the definition: narrowing passes, widening fails.
3. Everything it refers to exists: every authority it names is trusted, every permission it requires is defined, and no permission requires itself through a chain (A needs B needs A).
4. It's in date and not revoked, if definitions carry dates or can be revoked.

A permission is **met** when, beyond that, the credentials verified among the message's ancestors satisfy its requirements: whether this message would pass a `@permission=Name` gate, asked ahead of time. Well defined suits checking a definition before `Authorise` records a grant of it, checking a `Permission` that arrived from outside, and letting `meep lsp` flag an unknown name in `@permission=…`. Met suits deciding in a pipeline, such as answering a 403 before doing expensive work rather than letting the message fail at the gate.

`Verify.Permission` always checks that a permission is well defined, and checks that it's met when the message has verified credentials among its ancestors or when `against=credentials` is written. The failure reasons tell the two apart: `UnknownPermission`, `WiderThanDefinition` and `UndefinedRequirement` for a permission that isn't well defined, `MissingRequirement` for one that isn't met.

**Answer:** In a file in the trust store with directives that can narrow permission downstream. For the second half, `Verify.Permission`, go with the proposal above (agreed in chat, 24 November).

**Q258.** When a message is refused at a gate, should it become a `PermissionDenied` error that follows the error policy (my recommendation, so `@errors` branches can answer a 403), be dropped quietly, or skip the module as a Where does?

**Answer:** It should become a PermissionDenied error.

**Q259.** A verification is made at one moment. If the credential is revoked after it, should gates further down still let the message through? I recommend no: each verification records the revocation list's version, and a gate looks again only when the list has changed since, which costs nothing in the usual case.

**Answer:** No.

**Q260.** Should permissions be plain names only, or may they have a scope, as in `meep.files.write:~/notes/**`, which gated functions match against what they're about to touch?

**Answer:** They can have scope.

**Q261.** Messages with several parents, from Zip or Batch: if one parent descends from a verified credential and another came from an anonymous HTTP request, does the combination have the permission? Any parent's verification counting would let an anonymous message borrow another's rights by being zipped with it. I recommend that every parent line must carry it, the safer reading, with the first parent's line enough only when a verb says so.

**Answer:** Every parent line must carry it.

**Q262.** Should there be a `Revoke` verb (gated by `RevokePermission`), and may `Authorise` grant a permission the authorising credential doesn't hold itself? I recommend `Revoke`, and that you can only pass on what you have, unless your credential holds `AuthoriseAny`.

**Answer:** There should be a Revoke verb, and you can only pass on what you have unless you have AuthoriseAny.

**Q263.** Writing `@permission=Other` on `Authorise`: an error, because its permission is fixed, or both required? I recommend both required: a pipeline can add gates but never remove one.

**Answer:** Both required.

**Q264.** Names: you used both `VerificationMessage` and `VerifiedMessage`. Which? And Meep's spelling for the verb: `Authorise` as you wrote it, with `Authorize` accepted too?

**Answer:** VerifiedMessage. I think we should also accept both spellings of Authorise for Americans.

**Q265.** Order of work. I'd do phases 1–4 first (types, store, Verify, gates), since `@permission` and scripts need only those, then 5 (Authorise), then 6 (Meep's own functions) and 7 (the daemon) later. Does the scripting session need `meep.scripts.run` sooner than that?

**Answer:** No, those phases are fine.

## After your answers

Added 24 November 2026. How each answer changes the plan above. Where the plan and this section differ, this section wins.

- **Q251 (B).** A credential is an OpenSSH user certificate (`ssh-ed25519-cert-v01@openssh.com`, and the ECDSA and RSA kinds, which have the same fields). Its key ID and principals name the subject, its permissions are extensions, and its CA key is the issuing authority. Revocation uses KRLs, which `ssh-keygen -k` makes, alongside our own list of revoked serials and grants. `ssh-keygen -s` issues credentials and `ssh-keygen -L` shows them, so `meep trust issue` (phase 2) is a convenience rather than the only way. A `Grant` isn't an SSH certificate: it's Meep's own signed record, signed as an SSHSIG in a `meep-grant` namespace so `SshSignature` is reused.
- **Q252 (C).** Every pipeline has a credential of its own, proven when it loads (by its signature, or for your own unsigned file, by the daemon running as you), and every message a source emits descends from that verification. A directive makes a pipeline start with less, such as `#permissions meep.files.read`. This lands with phase 6, when the `meep.*` permissions are enforced; phases 1–5 don't need it.
- **Q253.** The proposal stands, with one change: the pipeline author marks proof as required in the parameters of the verbs that receive messages (`Listen`'s family), rather than `Verify` deciding alone. A `Listen` with proof required refuses requests without it before they reach the pipeline. `VerifiedMessage.Proof` records which kind was used: `None`, `Bearer`, `RequestSignature`, `Connection` or `Tls`.
- **Q254.** `Authorise` issues a separate `Grant` that names the credential, and never reissues the credential. A grant has an ID of its own, so it can be revoked alone.
- **Q255 (A).** `Authorise.Certificate` adds a trusted CA or root to the trust store (`roots/`, or `authorities` for an SSH CA key). It doesn't issue certificates.
- **Q256.** A grant is recorded in the trust store as well as emitted, so it applies whenever its credential is verified later, after a restart too. Phase 2's store gets a `grants` file; phase 5 writes to it. A grant carried as a message still counts where it's present, so one from another machine can be checked without being recorded.
- **Q257.** Definitions live in the trust store's `permissions.meep`, and `#permission` directives in a pipeline can only narrow one. `Verify.Permission` follows the proposal: it always checks a permission is **well defined**, and checks it's **met** when the message has verified credentials among its ancestors or `against=credentials` is written. The failure codes `WiderThanDefinition` and `UndefinedRequirement` join the list.
- **Q258.** A message refused at a gate becomes a `PermissionDenied` error carrying the reasons, following the module's error policy, as phase 4 says.
- **Q259.** A later revocation stops a message at the next gate. Each `VerifiedMessage` records the revocation list's version, and a gate looks again only when the list has changed since.
- **Q260.** A permission may have a scope after a colon, as in `meep.files.write:~/notes/**`. A gated function matches the scope against what it's about to touch. A permission without a scope covers every scope. Phase 1 has the type for this.
- **Q261.** A message with several parents has a permission only if every parent line carries it. There's no exception for a first parent; a verb that wants one would have to ask in a later question.
- **Q262.** There's a `Revoke` verb, gated by `RevokePermission`, in phase 5. `Authorise` can only pass on a permission the authorising credential holds itself, with a scope no wider than its own, unless the credential holds `AuthoriseAny`.
- **Q263.** `@permission=Other` on `Authorise` adds a gate: both permissions are needed. A pipeline can add gates but never remove one.
- **Q264.** The type is `VerifiedMessage`, renamed throughout this plan. The verb is `Authorise`, and `Authorize` is accepted as the same verb (an alias in the registry, so `meep lsp` and errors use the name that was written).
- **Q265.** The order stands: phases 1–4, then 5, then 6 and 7. `meep.scripts.run` waits for phase 6.
