# Troubleshooting

Problems people are likely to meet, what causes them, and how to fix them. Started 24 November 2026, with credentials and verification. For what each verb does, see [verbs.md](verbs.md).

## Credentials and verification

`Verify` never stops a pipeline when a check fails. It emits a `VerifiedMessage` whose `Failures` give a code and a reason for each thing wrong, and whose `Notes` say what else it found, such as a grant left out. Tap the `Verify` to see them.

### A credential fails with `UnknownCriticalOption`

Meep refuses any credential with a critical option, because a critical option is a restriction, and Meep can't honour one it doesn't act on. That includes OpenSSH's own:

- `source-address`, which limits a certificate to some addresses, such as the home network;
- `force-command`, which limits it to one command;
- `verify-required`, which needs a security key's PIN as well as a touch.

`ssh-keygen -s` adds these when it's given `-O source-address=…`, `-O force-command=…` or `-O verify-required`. Issue the credential again without them. A permission or a grant is the way to limit what a Meep credential can do.

Meep may honour `source-address` one day; ask if you need it.

### The trust store won't load: "can be changed by other users"

Meep, like OpenSSH, won't trust a store that anyone else could change. `~/.meep`, `~/.meep/trust` and everything in it must be writable by you alone.

- Run `meep trust init`. It narrows `~/.meep` and `~/.meep/trust` to 700 if others can reach into them, and says so.
- Or fix the file named in the message with `chmod go-w`.

On Debian, Raspberry Pi OS and their relatives, each user has a group of their own and a umask of 002, so new folders are 775. Meep accepts that, as Debian's OpenSSH does, when the group is yours and has no other members. If you share your group with anyone, it doesn't.

### A signed request fails with `BadSignature` behind a proxy

A proxy that ends TLS passes the request on as `http://…`, but the client signed `https://…`, so `@target-uri` doesn't match. Have the client sign `"@path" "@query"` in place of `"@target-uri"`. Meep accepts either, with `@method` and `@authority`.

### A signed request fails with `WrongAudience`

The request was addressed to a name Meep doesn't know as its own. By default that's this machine's host name and addresses, and `localhost`. A public DNS name, or the name a proxy answers to, needs `audience=`, as in `Verify from=msg.Headers["Meep-Credential"] audience="notes.example.com"`.

### A signed request fails with `StaleSignature`

It was signed more than `window` (60 s by default) before or after Meep checked it. The clocks of the client and this machine probably differ: check both keep time with NTP. `window=` can be up to an hour, though a longer window gives a replay longer to work.

### A signed request fails with `Replayed`

Its nonce has been seen within the window. A client must make a new nonce for every request, including a retry. Meep remembers nonces in memory only, so a restart forgets them.

### A signed request fails with `BodyMismatch`

The body isn't the one its `Content-Digest` describes, or the request was received with `stream=true`, which hands the body on before it can be checked. Listen without `stream=true` to verify requests.

### A certificate fails with `RevocationUnknown`

A CRL in `~/.meep/trust/revocation-lists` is past its next update, so Meep can't tell whether the certificate has been revoked since. Fetch the issuer's current CRL and add it with `meep trust revoke`.

### A permission is missing from a credential that should have it

Look at the result's `Notes`. A grant that applies to the credential is left out, with a note saying why, if its signer isn't trusted to sign grants (`meep-grant` in `authorities`), its signature is bad, or it's out of date or revoked.

## Gates and refusals

A module with `@permission`, or a verb that requires a permission, refuses a message that doesn't hold it. The message becomes a `PermissionDenied` error from that module, which says which permissions it lacked (`Needed`) and why, one reason for each (`Reasons`). Its parent is the message refused. Tap the module, or give it an `@errors` branch, to see them; one nothing handles is logged as an unhandled error. The module's `denied` count says how many it has refused.

### A message is refused with `PermissionDenied`

Read the reason for each permission it lacked:

- **"Nothing on one of the lines of this message's family is a verified credential."** The message, or one of the messages it was made from, didn't come through a `Verify` of a credential. Put a `Verify` above the gate. If the message was made by joining others, as `Zip` and `Batch` do, every one of them needs it: one from an anonymous request is enough to refuse the lot.
- **"alice's credential doesn't hold ReadNotes."** The credential passed, but it doesn't carry the permission, and no grant gives it. If `meep lsp` also warns that ReadNotes isn't defined, check its spelling in `@permission` against the credential's. Issue it again with the permission (`meep trust issue … --permission ReadNotes`), or record a grant. Check the verification's `Notes` for a grant left out.
- **"…doesn't hold WriteNotes:~/**."** The credential holds the permission, but for a narrower scope than the gate needs. A credential's scope must be at least as wide as the gate's.
- **"…doesn't hold WriteNotes for ~/secrets/key."** A module checked in code that the message may touch this file, and the credential's scope doesn't reach it.
- **"…holds ReadNotes, conferred by ca, but only granter may confer it."** The permission's definition, or this pipeline's own `#permission` line, allows only some authorities, and another one issued the credential or signed the grant.
- **"ReadNotes requires Other: …"** The definition says holding the permission needs another, and the credential lacks that one.
- **"alice's credential didn't verify: …"** There was a `Verify`, but the credential failed it; the rest of the reason is `Verify`'s. See [Credentials and verification](#credentials-and-verification).
- **"The verification of alice's credential has expired."** The credential, or a grant it relied on, ran out after it was verified. Messages made from it usually expire with it; this one was kept alive by `@expires` or `@accept-expired`.
- **"alice's credential, serial 7, is revoked."** (or its key, or its authority's key) It was revoked after it was verified. A gate looks again whenever the trust store has changed.
- **"ReadNotes isn't well defined here: …"** The pipeline's own `#permission` line for it is wider than the definition (another authority, a wider scope, or a requirement left out), or the definition refers to something that isn't there. A pipeline can only narrow a definition.
- **"There's no trust store to check against…"** The pipeline wasn't given one, or `~/.meep/trust` can't be read. See [The trust store won't load](#the-trust-store-wont-load-can-be-changed-by-other-users).

### A pipeline won't start: "… can't start: it needs …"

A source with `@permission`, or a source verb that requires a permission, needs the pipeline to hold the permission before it starts. Pipelines don't carry credentials of their own yet, so for now such a source can't start at all. Move the `@permission` to the module below the source.

### A request is refused with 401 before it reaches the pipeline

Its `Listen` has `proof=required`, and the request had no `Signature-Input`, `Signature` or `Meep-Credential` header; the reply names the ones missing. Sign the request with the credential's key, as [Verify](verbs.md#verify) describes; `ssh-keygen -Y sign -n meep-request` makes the signature by hand. A request with all three still has to pass `Verify`.
