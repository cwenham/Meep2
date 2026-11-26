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

Look at the result's `Notes`. A grant that applies to the credential is left out, with a note saying why, if its signer isn't trusted to sign grants (`meep-grant` in `authorities`), its signature is bad, it's out of date or revoked, or the credential that authorised it has been revoked ("falls with the credential that authorised it"). A grant falls with its authoriser's credential even when it wasn't revoked itself; `Authorise` it again with a credential that's still good.

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
- **"alice's credential was verified with none, as a bearer credential, and this needs a signed request, a connection's proof or TLS."** The gate has `@proof=signed` (or another `@proof`), and the credential was only presented: `Verify bearer=true` took it without proof that whoever sent it holds its key. Sign the request with the credential's key, or take `@proof` off if a bearer credential is enough here.
- **"There's no trust store to check against…"** The pipeline wasn't given one, or `~/.meep/trust` can't be read. See [The trust store won't load](#the-trust-store-wont-load-can-be-changed-by-other-users).

### A pipeline won't start: "… can't start: it needs …"

A source with `@permission`, or a source verb that requires a permission, needs the pipeline's own credential to hold the permission before it starts. A pipeline's own credential holds only Meep's permissions (`meep.…`), so a source gated on any other name can't start at all: move the `@permission` to the module below the source, so each message is checked there. A source gated on one of Meep's starts if the pipeline holds it; see [Meep's own permissions](#meeps-own-permissions) for why it might not.

### A request is refused with 401 before it reaches the pipeline

Its `Listen` has `signature=required`, and the request had no `Signature-Input`, `Signature` or `Meep-Credential` header; the reply names the ones missing. Sign the request with the credential's key, as [Verify](verbs.md#verify) describes; `ssh-keygen -Y sign -n meep-request` makes the signature by hand. A request with all three still has to pass `Verify`.

### A pipeline won't load: "@proof … isn't a gate"

`@proof` says how a gate's credentials must have been proven, so it only means something on a module that needs a permission. Give the module `@permission` too, or take `@proof` off.

## Authorising and revoking

`Authorise` and `Revoke` change this machine's trust store, so they need permissions of their own: `AuthorisePermission` to grant a permission, `AuthoriseCertificate` to trust an X.509 CA, `AuthoriseAuthority` to trust an SSH CA's key, and `RevokePermission` to revoke. No authority can be called `pipeline`, which is kept for pipelines' own credentials. A refusal for lack of one is a `PermissionDenied`, as at any gate; see [Gates and refusals](#gates-and-refusals). Other problems are errors from the module, saying why.

### A pipeline won't load: "Certificate can reach Authorise, but Authorise doesn't take it"

`Parse` without `kind=` may emit a credential, a certificate, a permission, a grant or an authority's key, and the module under it takes only some of them: `Authorise permission=…` takes credentials, `Revoke` takes credentials, grants and certificates. Say what you're parsing: `Parse kind=credential`, `Parse kind=certificate`, `Parse kind=grant` or `Parse kind=authority`. Text that turns out to be something else then becomes an error rather than a message nothing takes.

### `Parse` says "This is an SSH public key, not a certificate"

`Parse kind=credential` was given a public key, as a `.pub` file has it. Without `kind=`, `Parse` reads one as an `AuthorityKey`, for `Authorise.Authority` to trust as an SSH CA; that's only right if you meant to trust it to issue credentials. A credential is a key signed by an authority, as `meep trust issue` or `ssh-keygen -s` makes one: the `.credential` or `-cert.pub` file, not the `.pub` beside it. `Parse` names what it found when it can: a public key, JSON (say which part to read with `from=`), or the first word of the text.

### `Authorise` or `Revoke` refuses a credential "verified with none, as a bearer credential"

`Authorise` and `Revoke` change what this machine trusts, so the credential allowing it must have been proven by a signed request, a connection or TLS, as if `@proof=signed` were written on them. A credential read from a file and verified with `bearer=true` was only presented. Send a signed request instead, or, if a bearer credential is enough here, as for a pipeline only you can run, write `@proof=any` on the module.

### `Authorise` says "It took the credential to grant to from the request's X-Subject header, which the request's signature doesn't cover"

The pipeline read what to grant, or to whom, or what to revoke, from a header the client didn't list in its `Signature-Input`. Anyone between the client and Meep could have changed that header without breaking the signature, so `Authorise` and `Revoke` refuse it. Read it from the body instead (`from=fam.Listen.Text`), which `content-digest` covers, or have the client sign that header too.

### `Authorise` refuses with "Only a credential holding AuthoriseAny may grant AuthorisePermission"

`AuthorisePermission`, `AuthoriseAny`, `AuthoriseCertificate`, `AuthoriseAuthority` and `RevokePermission` change what this machine trusts, so only a credential holding `AuthoriseAny` may grant them. Otherwise anyone who could authorise could make more authorisers. Have a credential holding `AuthoriseAny` grant it, or issue the credential with it (`meep trust issue … --permission AuthorisePermission`).

### `Authorise` refuses with "passes on only what the authorising credential holds"

The credential that verified the request can grant only permissions it holds itself, with a scope no wider than its own. Grant something it holds, or a narrower scope of it, or give the authorising credential `AuthoriseAny`, which lets it grant anything.

### `Authorise` says "grants only to credentials this machine trusts"

The credential being granted to didn't pass the checks `Verify` makes, apart from proof of possession: it was issued by an authority this machine doesn't trust, its signature is bad, it's out of date or revoked, or it's a host's certificate. The rest of the message says which. A grant to a credential nothing here would accept would never count, so nothing is recorded. Trust its issuer with `meep trust add-authority`, or issue a new credential.

### `Authorise` says there's no authority key, or "didn't record the grant, since it wouldn't count here"

Grants are signed with this machine's authority key, `~/.meep/trust/authority.key`, which `meep trust init` makes. Run it if there's none. A key that's there but encrypted with a passphrase, or that isn't Ed25519, can't be used unattended; make a new one with `meep trust init` in a fresh store, or ask about signing through ssh-agent.

If the key is there but `authorities` doesn't trust it to sign grants (`meep-grant`), a grant signed with it would be left out whenever it's checked, so `Authorise` doesn't record it. `meep trust init` adds it for both credentials and grants; put it back with `meep trust add-authority <name> ~/.meep/trust/authority.key.pub --grants`.

### `Authorise` says "There's an authority called … already, with another key"

`Authorise.Authority` names the key it trusts in `authorities`, and definitions refer to authorities by that name (`authority=`), so a name can't be given to a second key. Give it another with `name=`. Without `name=`, the key's comment is used if it's one word, or `authority-` and the start of its fingerprint. The same key trusted already is passed on, whatever `name=` says.

### A grant or revocation isn't seen by the daemon

The pipeline that ran `Authorise` or `Revoke` sees the change at once. A daemon or `meep run` elsewhere on this machine sees it when its watcher notices the store's files change, usually within a second; it logs `[trust] reloaded`. If it says the store "can't be read, so it stays as it was", fix what it names.

### `Revoke` says a credential "has no serial"

A credential issued without a serial (serial 0) can't be told apart from others by its authority, so it can only be revoked by its key, which revokes every credential for that key: `Revoke whole-key=true`.

### "The trust store … has been being changed by something else for 10 seconds"

Every change to the trust store, from the daemon, `meep run` or `meep trust`, holds a lock on `~/.meep/trust/.lock` while it writes, and the others wait for it. One waited ten seconds and gave up, so something held the lock that long: look for a stuck `meep` process. The lock goes when the process holding it ends, so a crash can't leave the store locked.

### `grants` and `revoked` keep growing

Neither file is tidied as it's used, so expired grants and old revocations stay, and each is read again on every reload. `meep trust tidy` takes out what's no longer needed and lists each line it took out, with the reason:

- A grant goes once it has expired, been revoked, or its credential has been revoked.
- A revocation goes once what it revokes has expired, as the comment above it says ("needed until …"). For a grant, it can also go once the grant recorded in `grants` has expired. Until then it stays, since a copy of a grant can still be presented.
- A key's revocation stays for ever, as does any revocation written without an end date, such as one written before tidying was added.
- A credential's revocation also stays while a grant in `grants` names that credential as its authoriser, so the grant stays fallen.

`meep trust tidy` holds the store's lock while it works, and a running daemon sees the change as it sees any other.

## Meep's own permissions

Reading and writing files, listening and sending on the network are gated by Meep's own permissions (`meep.files.read`, `meep.files.write`, `meep.network.listen`, `meep.network.send`). A pipeline's own credential holds them unless it came from somewhere nobody vouches for, or starts with less with `#holds`. A refusal is a `PermissionDenied` from the module, as at any gate; a source, such as `Read` or `Listen`, emits one and stops, and the pipeline's log says "… stopped". See [Meep's own permissions](verbs.md#meeps-own-permissions) for which verb needs which.

### "This pipeline has no credential of its own, as …, so it doesn't hold meep.files.write"

Nobody vouches for the pipeline, so it can compute but not touch the machine. The reason says why:

- **"its file is on this machine, but it can be changed by others"**, or "the folder … can be changed by others", or "… belongs to someone else": someone other than you could have written the pipeline, so it doesn't run with your rights. The file must be yours, and it and every folder above it, up to the root, must be yours or root's, with nobody else able to change them. `chmod go-w` what the message names, or move the pipeline somewhere of your own. On Debian and its relatives, a file or folder your private group can write to is fine, as for the trust store, and so is a sticky folder such as `/tmp`, where only a file's owner can replace it.
- **"it includes …, and …"**: the same, for a file the pipeline `#include`s from this machine.
- **"whatever built it didn't say who vouches for it"**: a pipeline built in code, as a test or an embedding host builds one, holds nothing unless its `PipelineOptions.Credential` says, as `PipelineCredential.Owner(…)`.

### "This pipeline's own credential, the owner's (…), doesn't hold meep.files.write for /some/path"

The pipeline starts with less: its `#holds` line doesn't list the permission, or lists it with a scope that doesn't reach this path. Add it to `#holds`, or widen the scope. A scope is matched against the path's real form, with links followed, so a link inside an allowed folder that leads outside it is refused; so is a link whose target doesn't exist yet, since writing through it would create the target outside. The scope's own fixed start is read the same way, so `/tmp/**` on a Mac is `/private/tmp/**`, and a scope written in another case than the disk has matches on a Mac's disk, which ignores case. A folder that doesn't exist yet is matched as written, case and all.

### "… doesn't hold meep.network.send for localhost:8080"

A network scope is matched against the host as the address writes it, not what it resolves to, so `meep.network.send:127.0.0.1` doesn't cover `http://localhost/`, and the other way round. A scope without a port, such as `meep.network.send:*.example.com`, allows any port. Each redirect Fetch or Send follows must be allowed too, so a redirect to another host can be refused even when the first address was fine. A Listen on port 0, which takes any free port, is matched as `host:0`.

### "… is in Meep's own folder, …, which no meep.files permission reaches"

The file is in `~/.meep` (or `MEEP_HOME`), perhaps through a link, and not in its `trees` folder. No pipeline may read or write there, however its permissions are scoped, so that the trust store, plugins, and the daemon's settings and tokens change only through `meep trust`, `Authorise`, `Revoke` and Meep itself. Keep the pipeline's files somewhere else. A tree in `~/.meep/trees` is fine, and needs no permission.

### "… may send only to public addresses there" or "… may listen only on private addresses"

The pipeline, or the message, holds `meep.network.send:public` (or `:private`), not the host by name, and the host resolved only to addresses of the other kind when the connection was made. That's the point of the scope: a request can't steer a Fetch at the home network by naming something that resolves there. Allow the host by name (`meep.network.send:api.example.com`), or use the other keyword if it's meant to reach private addresses. `0.0.0.0` and `::`, every address at once, are in neither.

### "This pipeline's own credential, …'s, no longer counts: its key, …, is revoked."

The pipeline is signed by a key that's been revoked in the trust store, so it holds nothing, from the moment the store reloads, for pipelines already running too. Have it signed again by a key you trust, or take the revocation out if it was a mistake.

### "… stopped, as the pipeline no longer holds …"

A source, such as a Listen, was running when the trust store changed, or the pipeline was changed while it ran, and the pipeline no longer holds what the source needed when it started: a narrower definition in `permissions.meep`, a revoked signer, or a narrower `#holds`. It emits a `PermissionDenied` and stops; it isn't started again on its own. Undo the change, or restart the pipeline once it holds what it needs.

### "#holds lists …, but the pipeline's own credential doesn't hold it"

A note on the pipeline's verification, not a refusal: `#holds` can only keep what the credential has. The pipeline's credential holds nothing if nobody vouches for it, and never a name people define, such as `ReadNotes`; `#holds` refuses those when the pipeline loads.

### "… requires WriteNotes" or "… but only home may confer it, and not pipelines"

`permissions.meep` narrows one of Meep's permissions, as in `#permission meep.files.write requires=WriteNotes` or `authority=home`. The pipeline's own credential holds Meep's permissions as conferred by the reserved authority `pipeline`, and holds no names people define. So with such a line, only a message whose line passes through a verified credential holding all of it, such as a request from someone allowed, can do what it gates. That's the point of the line. To let pipelines too, add `authority=pipeline` beside the other authorities; to drop a requirement for them, take the line out.

### "Meep has no permission called meep.file.read"

Names starting `meep.` are kept for Meep's own permissions, so a name there that isn't one is almost certainly misspelt. Meep has `meep.files.read`, `meep.files.write`, `meep.network.listen`, `meep.network.send`, `meep.scripts.run`, `meep.daemon.observe`, `meep.daemon.inject` and `meep.daemon.control`. `meep lsp` warns about one, `meep trust list` reports one in `permissions.meep`, and a gate needing one refuses everything.

### A message sent in with the daemon's inject is refused

A message sent in from outside doesn't descend from the pipeline's own credential, so it holds none of Meep's permissions, and a Save, Fetch or Send it reaches refuses it. Until clients present credentials of their own (phase 7), send it into a module that doesn't touch the machine, or have a source make it.

### Every module that touches the machine refuses: "This machine's trust store can't be read"

There's a trust store, but Meep won't read it, usually because something in it can be changed by others; the log says which. It might narrow Meep's own permissions, so nothing holds them until it can be read. This is deliberate (Q312): a store someone else could change mustn't be able to fail open, so a broken store stops every Read, Save, Fetch, Send and Listen until it's fixed. See [The trust store won't load](#the-trust-store-wont-load-can-be-changed-by-other-users). Without a store at all, as before `meep trust init`, Meep's permissions work as Meep defines them.
