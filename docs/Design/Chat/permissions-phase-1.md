# Permissions, phase 1: the message types

Written 24 November 2026. Phase 1 of the plan in `permissions.md`: the message types for credentials, certificates and permissions, in a new `Meep2.SDK.Security` namespace, and the guards against forging a verification. The work is on the branch `permissions-phase-1`, made from `permissions` (itself `main` at 3e17062), not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q266, now answered. None of the answers changed what was built.

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 777 of 777 (703 before, and 74 new tests, counting each case of a test with arguments). Two full runs, both 777 of 777; none of the known flaky tests failed.
- `permissions.md` has an "After your answers" section at the end, saying how each of Q251–Q265 changes the plan, and `VerificationMessage` is now `VerifiedMessage` throughout its plan (Q264). The question itself still says what you were asked.
- `docs/writing-verbs.md` says that plugins are trusted code, and what that means for these types, as the plan promised.

## What was built

All in `src/Meep2.SDK/Security/`:

| Type | What it is |
|---|---|
| `Credential` | A message wrapping an OpenSSH user certificate, read with `Credential.Parse(text)` or `FromBlob(bytes)`: key type and key, key ID, principals, valid-after and valid-before, serial, user or host, critical options, extensions, the permissions among them, whether it permits bearer use, the authority's key, the signed bytes and the signature, and fingerprints of both keys. `InDateAt(time)` says whether it's in date. |
| `GrantTerms` | What a grant gives: its own ID, the credential (by key fingerprint, serial and authority fingerprint), one permission, and two times. `ToSignedData()` gives the bytes an authority signs; `AppliesTo(credential)` says whether it names that credential. |
| `Grant` | A message: terms and an SSH signature of them in the `meep-grant` namespace. Made with `Grant.Signed(terms, signature)`, read with `Parse` or `FromBlob`, written as `meep-grant-v01 base64`. The authority's key is read from the signature. |
| `Certificate` | A message holding an X.509 certificate as DER and the intermediates sent with it, made with `FromDer` or `FromPem`. Subject, issuer, dates, serial and SHA-256 thumbprint are read when it's made, for showing. `ProvenByTls` can only be set by the runtime. |
| `Permission` | A message: a `PermissionName`, the authorities that may confer it, and the permissions it requires. |
| `PermissionName` | A name and an optional scope, `meep.files.write:~/notes/**`. `Parse`, `TryParse`, `IsName`; `Covers(needed)` for holding one permission being enough for another, and `Allows(name, target)` for a gated function's question. |
| `PermissionScope` | The scope: a pattern of `/`-separated parts, with `*`, `?`, `**` and a leading `~`. `Matches(target)` and `Contains(inner)`. |
| `VerifiedMessage` | A message made only by the runtime: `Subject` (what was verified), `Kind`, `Success`, `Failures`, `Permissions`, `CheckedAt`, `TrustStoreVersion`, `RevocationVersion`, `Proof`, and `IsGenuine`. |
| `VerificationFailure` | A `VerificationFailureCode` and a sentence for people. |
| `VerificationKind`, `Proof`, `SshCertificateKind` | Enums: credential, certificate or permission; `None`, `Bearer`, `RequestSignature`, `Connection` or `Tls`; user or host. |

Also internal to the SDK: `SshWireReader` and `SshWireWriter` (SSH's wire format), `SshTime` (SSH's times, with 0 and the largest value meaning "no limit"), and `RuntimeOnly`, which says whether a message, or any of its ancestors, is one only the runtime may make.

The guards:

- **`VerifiedMessage`** is sealed, its one constructor is internal, and its properties are `internal init` or read-only, so nothing outside the SDK, the runtime and the tests can make one or set what it says. `Success` isn't stored: it's true when `Failures` is empty, so the two can't disagree. A copy made with `with` is a new message no `Verify` emitted, and its `IsGenuine` is false; phase 4's gates will count only genuine ones emitted by a `Verify`.
- **`Certificate.ProvenByTls`** has a private setter, set only through the internal `MarkProvenByTls()`, and only before the certificate is emitted. A copy made with `with` loses the mark, so it can't be carried over to another message's family.
- **`Pipeline.Inject`** refuses a message that is a `VerifiedMessage` or a certificate proven by TLS, or that descends from one. Injection is the one way the daemon puts a message into a pipeline from outside.
- **`Credential` and `Grant`** have no public constructors and no setters: their fields are read from the bytes the signature covers, so what `Verify` checks will be what pipelines see.

The tests:

- `PermissionNameTests` (52 with their cases): reading and writing names and scopes, names and scopes refused, covering, containing (and saying no when it can't tell), matching part by part, targets with `.`, `..` or empty parts matching nothing, `~`, `Allows`, and that twenty `**`s against 200 parts takes no time.
- `CredentialTests` (11): a certificate issued by `ssh-keygen -s` at test time, with principals, serial, UTC dates, a critical option, the default extensions and two permissions, one scoped, read field by field; for Ed25519, ECDSA 256 and 384, and RSA, its keys match the `.pub` files and the authority's signature over `SignedData` checks out with `Meep2.Plugins`' `PublicKey`; a changed byte breaks the signature; bearer and host certificates; a `@meep` extension that isn't a permission is refused; reading with or without its type and comment; truncated, padded and non-certificate input refused saying why; and no setters or public constructors. They skip, as the signing tests do, without `ssh-keygen`.
- `SecurityMessageTests` (11): a verification passes only without failures; a copy isn't genuine; reflection finds no public or protected constructor or setter on `VerifiedMessage`; the TLS mark is internal and lost on copying; a running pipeline refuses to have a verification, a copy of one, a message descended from one, or a TLS-proven certificate injected, and takes a plain certificate; `MessageJson` has no method that returns a message; certificates from DER and from PEM with an intermediate; unreadable certificates refused; a grant round-trips as text and its signature (made with `SshKeyPair`) covers its terms and no other; a grant signed in another namespace is refused; a permission reads as its name.

## Decisions I made while building

- **SSH certificates are read in the SDK, and checked outside it.** Reading needs only SSH's wire format, so the SDK has its own small reader rather than depending on `Meep2.Plugins` (which brings BouncyCastle and NuGet, and isn't AOT-compatible). Plugins and, later, scripts can then read credentials, as they'll need to. Checking a signature needs Ed25519, which Meep checks with BouncyCastle, so that stays with `PublicKey.Verify` in `Meep2.Plugins`, for `Verify` to call in phase 3. `Credential` keeps `SignedData`, `Signature` and `AuthorityKey` for exactly that, and the tests check them against `PublicKey`. The cost is two readers of about 50 lines each; neither can reach the other without widening `InternalsVisibleTo`, which would be worse.
- **Permissions are extensions named `name@meep`, and a scope is the extension's value**: `-O extension:ReadNotes@meep -O extension:meep.files.write@meep=~/notes/**`. `ssh-keygen -L` shows them plainly. `permit-bearer@meep` is Meep's own flag (Q253), not a permission. An extension ending `@meep` that isn't a permission name refuses the whole credential, rather than being skipped, so a typo in a permission can't pass unnoticed. Extensions' names are unique, so a certificate can carry only one scope for each permission (Q267).
- **Every OpenSSH certificate type is read**: Ed25519, ECDSA (256, 384, 521), RSA, and the security-key types (`sk-ssh-ed25519`, `sk-ecdsa-sha2-nistp256`). Host certificates are read too, marked `Host`, for `Verify` to refuse as `WrongUsage`. Critical options are kept by name, for `Verify` to refuse any it doesn't understand (`UnknownCriticalOption`, a new code). `Malformed` is new too, for things that can't be read.
- **A grant binds to one certificate**: its key, serial and issuing authority. A credential reissued for the same key doesn't carry its grants over (Q268).
- **A grant is in SSH's wire format, signed as an SSHSIG**, like a certificate, so the same reader serves both, and `ssh-keygen -Y sign -n meep-grant` can sign one by hand. The signature is kept in SSHSIG's binary form rather than armored text, since it goes inside the grant's own base64. The authority's key isn't stored separately: it's read from the signature, so the two can't disagree. The grant's ID is a UUID version 7.
- **`VerifiedMessage.Subject` holds what was verified**, as well as it usually being the parent. A credential can come from a header (`Verify from=msg.Headers["Meep-Credential"]`), when the parent is the request and not the credential.
- **Scopes fail closed.** A target with a `.` or `..` part, or an empty part, matches nothing, and a scope can't contain one. Matching is exact, case and all, so on macOS's case-insensitive disks a path written in another case is refused rather than allowed (Q269). `Contains` answers no when it can't be sure, so phase 5's "you can only pass on what you have" (Q262) may refuse a narrowing that was in fact fine, but never allows a wider one. `~` is the home folder of the process, which for the daemon is yours.
- **The guard against injection is in `Pipeline.Inject`, not `MessageJson`.** `MessageJson` only writes, for people to look at (Q38), and can't read anything back, so there's nothing in it to refuse; the daemon's inject makes only `TextMessage`s and `JsonMessage`s. `Pipeline.Inject` is where every message from outside goes in, so the refusal there covers the daemon and anything else that injects. A test pins that `MessageJson` returns no message, so a reader can't be added without someone seeing it. A `JsonMessage` with `"Success": true` in it is still a `JsonMessage`; gates will test the type.
- **`Certificate`'s details are read when it's made**, so a certificate that can't be read fails at once with a `FormatException`, and conditions such as `msg.Subject == "CN=alice"` work. `ToX509()` gives .NET's own class for `Verify` to build a chain with.
- **No `Verify`, trust store or gates yet**, as the plan says: those are phases 2–4.

## Found in passing

- **Any message can be given new parents with `with { Parents = … }`**, since `Parents` is `init` on `Message`. That's why a copy of a TLS-proven certificate loses its mark, and why phase 4's gates must count only genuine `VerifiedMessage`s emitted by a `Verify` of the same pipeline, not any found among the ancestors.
- **`Verify` and an mTLS Listen will be in `Meep2.Modules`, which can't reach the SDK's internals.** Only `Meep2.Runtime`, the tests and the benchmarks can. Q266 asks how they should make verifications.
- **`PublicKey` in `Meep2.Plugins` can't check security-key (`sk-`) signatures.** A credential whose key is on a security key reads fine, but can't prove possession until it can (Q271). This doesn't affect an authority's signature unless the authority's key is a security key.

## Questions

**Q266.** `VerifiedMessage` can only be made inside the SDK, the runtime and the tests. `Verify` will be a verb, and verbs live in `Meep2.Modules`, which can't. How should `Verify` make one?
- A: `Verify`'s three inflections live in `Meep2.Runtime`, which already can, and are registered as built-in verbs from there.
- B: `Meep2.Modules` gets `InternalsVisibleTo` from the SDK. Simple, but then every built-in verb could make a verification, and the SDK's other internals (stamping) are open to it too.
- C: The runtime gives verbs marked as verifiers, in Meep's own assemblies, a narrow object for making verifications (and for marking a certificate as proven by TLS, for the Listens), through `ModuleContext`.

I'd do A for `Verify`, and C, narrowed to the one call, for the Listens' TLS mark in phase 7.

**Answer:** A and C as you suggest.

Done: nothing to change now. Phase 3 puts `Verify`'s inflections in `Meep2.Runtime`; phase 7 gives the Listens a narrow way, through `ModuleContext`, to mark a certificate as proven by TLS.

**Q267.** A certificate can carry only one scope for each permission, since extension names must be unique: `meep.files.write@meep=~/notes/**` can't sit beside `meep.files.write@meep=~/drafts/**`. Is one enough, with grants for more, or should the value allow several scopes, such as separated by spaces? I'd leave it at one until someone needs more.

**Answer:** Leave it at one for now.

Done: nothing to change.

**Q268.** A grant binds to one certificate (key, serial and authority), so when a credential is reissued, say at renewal, its grants don't carry over and need granting again. Should a grant bind to the key and authority only, so it outlives renewals until its own end date or revocation? Binding to the certificate is simpler to reason about (a revoked serial takes its grants with it); binding to the key is less work for whoever renews. I'd keep the certificate.

**Answer:** Bind to the certificate.

Done: nothing to change; `GrantTerms` already names the key, serial and authority.

**Q269.** Scopes match exactly, case and all, but macOS's disks usually aren't case-sensitive, so `~/Notes/a.md` is refused by `meep.files.write:~/notes/**` though it's the same file. It fails safe, but would surprise people. I'd have each gated function in phase 6 turn a path into its real form before matching (full, with symbolic links followed and in the case it has on disk), and keep scope matching exact. Agreed?

**Answer:** Yes.

Done: nothing to change now. Phase 6's gated functions will turn a path into its real form before matching it.

**Q270.** A copy of a `VerifiedMessage` made with `with` keeps everything, including `Success`, but has `IsGenuine` false, and gates will ignore it. Should copying one throw instead, so no one is misled by a copy that looks like it passed? Throwing is clearer, but a verb that copies whatever arrives, generically, would then fail on a verification. I'd keep copies allowed, and show `IsGenuine` in taps (it already shows, as a public property).

**Answer:** Keep copies allowed.

Done: nothing to change.

**Q271.** A credential whose key is on a security key (`sk-ssh-ed25519`, as a YubiKey makes with `ssh-keygen -t ed25519-sk`) reads, but `PublicKey` can't check its signatures yet, so it can't prove possession (Q253). Add `sk-` signatures to `PublicKey` in phase 3, or later? They're a little different (they sign a hash of the application, flags and a counter as well). I'd add them in phase 3, since keys that never leave a device are what proof of possession is for.

**Answer:** Add in phase 3.

Done: nothing to change now; it's on phase 3's list.