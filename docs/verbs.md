# Verbs and inflections

The verbs Meeplang has so far, as of 26 November 2026, from the `[Verb]` attributes in `src/Meep2.Modules`, and the security verbs `Verify`, `Parse`, `Authorise` and `Revoke`, which the runtime provides itself (`src/Meep2.Runtime/Verifying`), since only the runtime may make a verification, and the others work with the trust store beside it. A verb with more than one inflection has a class for each. The runtime picks one by the message's type or by the parameters (usually the address's scheme), and `Verb.Inflection` names one directly, as in `Fetch.HTTP`.

Parameters are written in kebab case (`max-size`, `save-every`). The positional parameter, written straight after the verb without a name, is shown in the Positional column.

## Summary

| Verb | Inflections | Kind | Role |
|---|---|---|---|
| Interval | — | Source | source |
| Read | — | Source | source |
| Listen | HTTP, TCP, WebSocket | Source | inlet (declared) |
| Fetch | HTTP, File | Inflection | step |
| Extract | — | Inflection | step |
| Enumerate | — | Inflection | step |
| Where | — | Filter (emits its input) | filter (declared) |
| Tap | — | Emits its input | tap (declared) |
| Batch | — | Inflection | step |
| Zip | — | Inflection | step |
| Distribute | — | Routes its input | router |
| Learn | — | Emits its input | step |
| Classify | — | Inflection | step |
| Forget | — | Emits its input | step |
| Verify | Credential, Certificate, Permission | Inflection | step |
| Parse | Text | Inflection | step |
| Authorise (or Authorize) | Permission, Certificate, Authority | Inflection | step |
| Revoke | Credential, Grant, Certificate | Emits its input | step |
| WriteLine | — | Passthrough | outlet (declared) |
| Save | — | Passthrough | outlet (declared) |
| Send | Reply, Broadcast, HTTP, TCP, WebSocket | Passthrough | outlet (declared) |

21 verbs, 35 inflections in all, counting `Authorise` and `Authorize` once.

Verbs that touch the machine need Meep's own permissions, which every pipeline you run holds unless it starts with less: see [Meep's own permissions](#meeps-own-permissions).

The role says what a verb does with messages, so tools can show it without knowing its name: a source makes messages itself, an inlet receives them from outside, a filter passes on some of what it receives, an outlet sends messages out of the pipeline, a tap receives another module's messages without being indented under it, a router spreads messages over copies of a branch, and a step is anything else. Sources, routers and steps are worked out from the class, and the rest declared with `[Role]`. See [Roles](writing-verbs.md#roles) in the guide to writing verbs.

## Sources

### Interval
Emits a `Tick` straight away, then every period, until stopped or until it has emitted `count` ticks. Can be reconfigured while running.

- Positional: `period`
- Parameters: `count`, `count-start`, `payload`
- Example: `Interval 500ms count=3`

### Read
Reads a text file once and emits it as `FileText`.

- Positional: `path`
- Example: `Read "~/notes.txt"`
- Needs: `meep.files.read` for the file, held by the pipeline. Without it, Read emits a `PermissionDenied` and stops.

### Listen
Receives messages from the network, each carrying a lease so `Send` can reply. Chosen by the address's scheme. All three can be reconfigured while running.

| Inflection | Addresses | Emits | Parameters |
|---|---|---|---|
| HTTP | `http://`, `https://` | `HttpRequest` | `certificate`, `key`, `max-size`, `stream`, `default-status`, `default-reply`, `signature` |
| TCP | `tcp://` | `TcpMessage`; `TcpConnected`/`TcpDisconnected` with `connections` | `end`, `length`, `connections`, `max-size` |
| WebSocket | `ws://`, `wss://` | `WebSocketText`, `WebSocketBinary`; `WebSocketConnected`/`WebSocketDisconnected` with `connections` | `certificate`, `key`, `origins`, `connections`, `max-size`, `ping`, `idle-timeout`, `max-queued` |

- Positional: `url`
- Example: `Listen "ws://0.0.0.0:8080/chat/" as Room`
- Needs: `meep.network.listen` for its host and port, as written (`0.0.0.0:8080`), held by the pipeline, and for https, `meep.files.read` for its certificate and key. Without them, the Listen emits a `PermissionDenied` and stops, before it binds.
- `signature=required` on an HTTP Listen refuses, with 401, a request that has no `Signature-Input`, `Signature` and `Meep-Credential`, before it reaches the pipeline, and counts it as `denied`. It only keeps anonymous requests out; `Verify` still checks the signature and the credential. `signature=optional`, the default, lets every request in.
- HTTP and WebSocket Listens tell a client that asks with `Meep-Host: ?` the daemon's ID only if it's on this machine or a private address, unless the daemon's settings have `meep-host-public = true` (or `meep run` or `meep daemon` is given `--meep-host-public`). An origin a client gives is counted as it says; it's a claim, not proof.

## Getting and reshaping data

### Fetch
Fetches something for each message it receives.

| Inflection | Addresses | Emits | Parameters |
|---|---|---|---|
| HTTP | `http://`, `https://` (GET) | `HttpResponse` | `timeout`, `stream`, `max-size` |
| File | file paths, `file://` | `FileText` | `timeout`, `max-size`, `root`, `any-path` |

- Positional: `url`
- Example: `Fetch "https://api.example.com/stops/{msg.StopId}"`
- Needs: `meep.network.send` for the host and port (HTTP), checked again for every redirect it follows, or `meep.files.read` for the file (File). A message that doesn't hold it becomes a `PermissionDenied`.
- What a `{msg…}` hole gives can't make a file path absolute, start it with `~`, climb out of its folder with `..`, or be part of a `file://` address, unless `any-path=true`. So `Fetch "~/stops/{msg.Name}.json"` reads only files in `~/stops`, whatever the message says. Holes that don't read the message, such as constants, aren't limited.
- `root=` keeps every path inside a folder: relative paths start from it, and a path that leads anywhere else becomes an error. A Fetch with `root=` only reads files.
- HTTP Fetches (and Sends) tell where a message comes from, or ask whether the other end is Meep, only to hosts the daemon's `meep-hosts` names, this machine, and hosts on private addresses. An API on the internet hears nothing about the pipeline calling it. Private means this machine, 10/8, 172.16/12, 192.168/16, link-local, and IPv6's unique-local range; a name is private if every address it has is. The range carriers share, 100.64/10, isn't, though Tailscale gives its machines addresses there: name those in `meep-hosts`, or for `meep run`, with `--meep-hosts`, as in `--meep-hosts studio.local,10.0.0.5:8080`.

### Extract
Reads a message's text as JSON and emits a `JsonMessage` for each value a JSONPath query matches.

- Positional: `path`
- Example: `Extract "$.departures[*]"`

### Enumerate
Emits an item from a book in the library for each message it receives, as a `BookItem`, or every item with `all=true`. Without `index` it steps through the book in order; the index counts from 0 and wraps round both ways. Book names are matched ignoring case. Built-in books: Lorem Ipsum, Common Passwords, Common Last Names, Countries, ISO 3166 Countries, ISO 3166 Codes, US States, US Abbreviations, Reindeer, Encodings. Plugins add more with `[LibraryBook]`. With `all=true`, the loader warns if a module after it has too small a buffer for the book.

- Positional: `book`
- Parameters: `index`, `all`
- Example: `Enumerate "Countries" index=msg.Count`

## Flow

### Where
Passes on only the messages a condition is true for. Messages the condition doesn't fit skip it.

- Positional: `condition`
- Example: `Where msg.minutesAway < 30`

### Tap
Receives everything another named module emits, as if indented under it.

- Positional: `from` (a module name)
- Example: `Tap Schedule @errors`

### Batch
Gathers messages into a `Batched` message: so many at a time, or whatever came within a while of the first.

- Positional: `size`
- Parameters: `within`
- Example: `Batch 100 within=1s`

### Zip
Pairs a message from its first input with one from its second, as a `Zipped` message, in order or by a shared key.

- Parameters: `by`, `limit`, `within`
- Example: `Zip by msg.OrderId`

### Distribute
Spreads messages across copies of the branch below it; messages with the same key take the same copy.

- Positional: `copies`
- Parameters: `by`
- Example: `Distribute 4 by msg.CustomerId`

## Classification

### Learn
Teaches a named naive Bayes tree that each message belongs to a class, then passes it on.

- Positional: `tree`
- Parameters: `class` (required), `text`, `file`, `save-every`
- Example: `Learn "topics" class="Science"`
- Needs: `meep.files.read` for the tree's file when `file=` names one, and `meep.files.write` for it when it saves (`save-every` above 0, the default 5), for each message it learns from. A tree in its own place in `~/.meep/trees` needs neither.
- A tree stays in memory while any Learn, Classify or Forget uses it. When the last lets go, as when its pipelines end, it's saved (unless every Learn had `save-every=0`) and leaves memory, to be loaded from its file when it's next named. A tree kept in memory only, with `save-every=0`, is gone once no pipeline uses it.

### Classify
Emits a `ClassifiedMessage` with the class and score a named tree gives each message.

- Positional: `tree`
- Parameters: `text`, `file`, `min-training`, `against`
- Example: `Classify "mail" against="Ham"`
- Needs: `meep.files.read` for the tree's file when `file=` names one.

### Forget
Makes a named tree forget everything, or one class, then passes the message on.

- Positional: `tree`
- Parameters: `class`, `file`
- Example: `Forget "topics" class="Science"`
- Needs: `meep.files.write` for the tree's file when `file=` names one, since it saves at once; nothing for a tree in its own place.

## Security

### Verify
Checks a credential, a certificate or a permission against this machine's trust store (`~/.meep/trust`, kept by `meep trust`) and emits a `VerifiedMessage` saying whether it passed, every reason it didn't, and the permissions it established. A failed check is a result, not an error, so a pipeline can answer it, such as with a 401. Only Verify can make a `VerifiedMessage`, and a gate (`@permission`, below) counts only one Verify made.

The inflection is chosen for each message by what it's given: the message itself if it's a `Credential`, `Certificate` or `Permission`; otherwise the text `from=` reads from it, or the message's own text. PEM, or DER in base64, is a certificate; a permission's name or a `#permission` line is a permission; anything else is read as a credential, and fails as `Malformed` if it isn't one.

| Inflection | Checks | Proof of possession |
|---|---|---|
| Credential | An OpenSSH user certificate: issued and signed by an authority in `authorities`, in date, no unknown critical options, not revoked (the `revoked` file and KRLs). Its permissions are its `name@meep` extensions, and the grants that apply to it, recorded or carried among the message's ancestors. | A request signed with the credential's key (HTTP Message Signatures, below), or with `bearer=true`, a credential whose issuer allows it with `permit-bearer@meep`. |
| Certificate | An X.509 chain to a certificate in `roots/` (any of them is an anchor, an intermediate too), every certificate in date and allowed to do what it does (`usage=`), none revoked by the `revoked` file or CRLs, and every issuer's CRL in date. | A TLS handshake (mutual TLS, phase 7), or `bearer=true`. |
| Permission | That it's well defined by `permissions.meep`, and, when the message has verified credentials among its ancestors or `against=credentials`, that they meet it: every line of the message's family passes through a verified credential holding it, conferred by an authority it allows, with everything it requires. A name written as text narrows the definition, as a pipeline's `#permission` does. | None needed. |

- Parameters: `from`, `bearer`, `window`, `audience`, `usage` (`client`, `server` or `any`), `against` (`auto`, `definition` or `credentials`)
- Examples: `Verify from=msg.Headers["Meep-Credential"]`, `Verify bearer=true` under `Read "~/me.credential"`, `Verify from="ReadNotes"`
- A passing verification expires when the earliest thing it relied on does: the credential, a grant, a certificate on the chain, or a CRL that's due. It records the trust store's version and revocation version.
- A signed request carries `Meep-Credential` (the certificate's base64), `Content-Digest` (RFC 9530, needed when there's a body), and `Signature-Input` and `Signature` (RFC 9421) labelled `meep`, covering at least `@method`, `@authority`, and `@target-uri` or `@path` and `@query` (for behind a proxy that ends TLS, where the scheme changes), with `created`, `nonce` and optionally `keyid`. The signature is an SSH signature of the signature base in the `meep-request` namespace, as `ssh-keygen -Y sign -n meep-request` makes, by the credential's key; a security key's (`sk-`) needs a touch. It must be made within `window` of now (60 s by default), its nonce unseen in that time, and addressed to one of this machine's names, or one in `audience=`. A streamed body (`stream=true` on Listen) can't be checked, and fails.
- A result also has `Notes`: what was found that isn't a failure but might explain a missing permission, such as a grant left out and why.
- When a check fails and the reason isn't clear, see [troubleshooting.md](troubleshooting.md).

### Parse
Reads text as a credential, certificate, permission, grant or authority's key, and emits it as that message, for the verbs that take only typed messages: `Authorise` and `Revoke`. Text that's none of them becomes an error saying what it found, such as "This is an SSH public key, not a certificate." It reads with the same reader as `Verify`, so the two never disagree about what text is.

- Parameters: `from` (the text, read from the message; without it, the message's own text), `kind` (`any`, the default, or `credential`, `certificate`, `permission`, `grant` or `authority`: anything else becomes an error)
- An SSH public key, as a `.pub` file has it (`ssh-ed25519 AAAA… comment`), is an `AuthorityKey`, for `Authorise.Authority`. Read as a credential, it's an error saying it's a public key, not a certificate.
- With `kind=`, it emits only that type, so the modules under it need take only that type. Without it, the loader expects all five, and a module under it that takes fewer, such as `Authorise permission=…`, which takes credentials, is an error when the pipeline loads.
- A message that's already one of the five is passed on as it is. A credential it emits is only read, not checked: `Verify` checks one.
- Examples: `Parse kind=credential from=fam.Listen.Text`, `Fetch "~/bob.credential"` then `Parse kind=credential`

### Authorise
Grants a permission to a credential, or trusts a certificate authority, X.509 or SSH, in this machine's trust store. `Authorize` is the same verb; tools and errors use the name you wrote. The inflection is chosen by the message's type, so put a `Parse kind=…` above it.

| Inflection | Takes | Needs | Emits | Parameters |
|---|---|---|---|---|
| Permission | `Credential`: the credential to grant to | `AuthorisePermission` | `Grant` | `permission` (required), `valid` |
| Certificate | `Certificate`: a CA's certificate | `AuthoriseCertificate` | the certificate | `name` |
| Authority | `AuthorityKey`: an SSH certificate authority's public key | `AuthoriseAuthority` | the key | `name`, `grants` |

- `Authorise.Permission` signs a `Grant` with this machine's authority key (`~/.meep/trust/authority.key`, made by `meep trust init`) and records it in the trust store's `grants`, so it applies whenever the credential is verified, here or after a restart. It emits the grant too, so it can be sent back or carried. The credential isn't reissued, and the grant can be revoked alone.
- `permission=` is the permission to grant, with a scope if it has one: `permission="WriteNotes"`, `permission="meep.files.write:~/notes/**"`, or from the message, as in `permission=fam.Listen.Query['permission']`. `valid=` is how long the grant lasts, as in `valid=30d`; without it, as long as the credential.
- Whoever authorises is the credential the message's family was verified with, as at any gate. It must have been proven, by a signed request, a connection or TLS, not only presented: `Authorise` and `Revoke` act as if `@proof=signed` were written on them. A line's own `@proof` replaces that, and `@proof=any` accepts a bearer credential, as when an administrator's credential is read from a file.
- It can pass on only what it holds itself, with a scope no wider, unless it holds `AuthoriseAny`; otherwise the message is refused with a `PermissionDenied`. The permissions over the trust store itself, `AuthorisePermission`, `AuthoriseAny`, `AuthoriseCertificate`, `AuthoriseAuthority` and `RevokePermission`, can be granted only by a credential holding `AuthoriseAny`, so an authoriser can't make more authorisers.
- What it grants, and to whom, must come from a part of the signed request the signature covers: the body (covered by `content-digest`), the address (`@target-uri` or `@query`), or a header the client listed in `Signature-Input`. Read from a header the signature doesn't cover, it could have been changed on the way, so `Authorise` refuses it with an error naming the header. What didn't come from the request, such as a file the pipeline reads, isn't affected.
- The credential granted to must be one this machine trusts: issued by a trusted authority, in date and not revoked. If not, it's an error saying why, and nothing is recorded.
- The grant names the credential that authorised it, and falls when that credential is revoked: `Verify` leaves it out, and gates refuse it at once. That goes one level only. The grant doesn't fall when the authoriser's credential expires or loses `AuthorisePermission`, and grants made by someone a grant was given to don't fall with that grant.
- `Authorise.Certificate` adds a CA's certificate to `roots/`, so certificates that chain to it verify. Any CA will do, an intermediate too. `name=` is what to call its file; without it, its common name and the start of its thumbprint. One trusted already is passed on without writing it again.
- `Authorise.Authority` trusts an SSH CA's key to issue credentials, adding it to `authorities` as `meep trust add-authority` does, and with `grants=true`, to sign grants too. `name=` is the name definitions use in `authority=`; without it, the key's comment if that's one word, or `authority-` and the start of its fingerprint. A key trusted already is passed on; a name in use for another key is an error. It needs `AuthoriseAuthority`, which `AuthoriseCertificate` doesn't give, so trusting X.509 roots and trusting credential issuers can be given apart. Put `Parse kind=authority` above it.
- `@permission` on `Authorise` adds to what it needs: both are needed. No pipeline can remove what it needs.
- What it changes is in force at once, in the pipeline that changed it, and in a running daemon or `meep run` as soon as their watcher notices, within about a second.
- Examples: `Authorise permission="WriteNotes" valid=30d`, `Authorize permission=fam.Listen.Query['permission']`, `Authorise name="department"` under `Parse kind=authority`

### Revoke
Revokes a credential, a grant or a certificate in this machine's trust store, and emits what it revoked. Gates notice at once: a message verified before the revocation is refused at the next gate it reaches. It needs `RevokePermission`, whatever it's given, and `@permission` adds to that. As with `Authorise`, the credential holding it must have been proven unless the line's `@proof` says otherwise, and what to revoke must come from a covered part of a signed request.

| Inflection | Takes | Revokes by |
|---|---|---|
| Credential | `Credential` | its authority and serial, or with `whole-key=true`, its key and so every credential for it |
| Grant | `Grant` | its ID, leaving its credential and other grants alone |
| Certificate | `Certificate` | its SHA-256 hash |

- Parameters: `whole-key` (Credential only)
- Something revoked already is passed on without writing it again.
- Under `Authorise`, it revokes the grant just made. Otherwise put a `Parse kind=…` above it.

### A small authorisation system

These verbs, `Verify` and gates are the pieces; Meep has no users or accounts of its own. Here is a notes service whose administrator lets others read the notes, and can take that back. The trust store's `permissions.meep` defines the permission:

```
#permission ReadNotes
```

The administrator's credential holds `AuthorisePermission`, `RevokePermission` and `ReadNotes` (`meep trust issue admin.pub --permission AuthorisePermission --permission RevokePermission --permission ReadNotes`). Everyone else's holds none of them until the administrator grants `ReadNotes`.

```
// Reading the notes needs ReadNotes.
Listen "http://0.0.0.0:8080/notes/" signature=required
    Verify from=msg.Headers["Meep-Credential"]
        Fetch "~/notes/today.txt" @permission=ReadNotes
            Send error-status=403 error-reply="{msg.Error}"

// Granting: the administrator posts someone's credential to /grants/?permission=ReadNotes, and gets the grant back.
Listen "http://0.0.0.0:8081/grants/" signature=required
    Verify from=msg.Headers["Meep-Credential"]
        Parse kind=credential from=fam.Listen.Text
            Authorise permission=fam.Listen.Query['permission'] valid=90d
                Send error-status=403 error-reply="{msg.Error}"

// Revoking: the administrator posts the grant, as granting sent it back.
Listen "http://0.0.0.0:8082/revoke/" signature=required
    Verify from=msg.Headers["Meep-Credential"]
        Parse kind=grant from=fam.Listen.Text
            Revoke
                Send text="Revoked." error-status=403 error-reply="{msg.Error}"
```

- Anyone who can reach these ports can try, but only a signed request from a credential the store trusts gets past `Verify`, and only one holding the right permission gets past each gate. A refusal, or anything else that goes wrong, is answered with 403 and the reason. `Authorise` and `Revoke` also need the administrator's credential proven, not only presented, and take the credential and grant from the body, which the signature covers.
- Each `Send` answers what reaches it: the note's text, the grant (as `meep-grant-v02 …`), or "Revoked.". Errors from `Parse` pass `Authorise` and `Revoke` by, since those take only typed messages, and reach the `Send`.
- The administrator can grant `ReadNotes` because their own credential holds it. To let them grant anything, give them `AuthoriseAny` instead.
- A grant lasts 90 days here. Revoking it takes effect at the next gate, even for a request already under way.
- To make it stricter, narrow in the pipeline: `#permission ReadNotes authority=home` at the top would accept `ReadNotes` only from the `home` authority, whoever else the store trusts.

### Gates: `@permission`
Not a verb, but a modifier any module can have: `@permission=Name` makes the module a gate. A message reaches it only if every line of its family, back to where it began, passes through a `Verify` of a credential that passed and holds the permission, conferred by an authority the permission's definition allows, with everything the definition requires. A message made by joining others, as `Zip` and `Batch` do, needs the permission on every line it came from.

```
Listen "http://0.0.0.0:8080/notes/" signature=required
    Verify from=msg.Headers["Meep-Credential"]
        Send text="Welcome." @permission=ReadNotes
            Send error-status=403 error-reply="{msg.Error}" @errors
```

- Write it more than once to need several; all are needed. A scope goes in quotes: `@permission="meep.files.write:~/notes/**"`, and the credential must hold a scope at least as wide.
- A permission defined in the trust store's `permissions.meep` must be held as its definition says. One that isn't defined can still be needed: any trusted authority confers it, and it requires nothing, unless the pipeline's own `#permission` line narrows it. `meep lsp` warns about a name the store doesn't define, in case it's misspelt.
- A refused message becomes a `PermissionDenied` error from the module, with the message as its parent, saying which permissions it lacked and why (`Needed`, `Reasons`). It follows the error policy as other errors do, so an `@errors` module can answer it. One nothing handles is logged, but refusals never count towards `stop-pipeline`, so unwelcome requests can't stop a pipeline. An error refused at a gate passes the module by instead, still looking for a module that takes errors.
- A refused message goes no further along that path, even past a passthrough module or `Distribute`.
- Each module counts what it refused as `denied`, in `meep` and Synesthesia (a Listen with `signature=required` counts its 401s there too), and the OpenTelemetry metric `meep.module.messages` has `meep.event = denied`. Each refusal is also a short span of its own, named after the module's verb, as in `Save.denied`, under the span the message came from, with the permissions, the reasons and `meep.outcome = denied`, and an event, `meep.permission.denied`, saying the same. Its status isn't an error, since a refusal isn't a fault. The `PermissionDenied` it becomes hangs under it, so an `@errors` branch answering with a 403 is its child. A refusal straight after a source has a span too.
- `@proof=signed` on a gate needs its credentials to have been proven, not only presented: by a signed request, a connection's proof, or TLS. A bearer credential (`Verify bearer=true`, taken without proof) is refused, saying so, and so is the pipeline's own credential. `@proof=request`, `@proof=connection`, `@proof=tls` or `@proof=pipeline` (the pipeline's own) needs that kind; write more than one to accept any of them. It applies to every permission the module needs, its verb's included, and to its checks in code. On a module that isn't a gate it's an error.
- A verb can need proof by default, as `Authorise` and `Revoke` need `signed`. A line's own `@proof` replaces the default, and `@proof=any` accepts a bearer credential.
- A verb can require a permission itself, which no pipeline can remove; `@permission` adds to it. `meep lsp` shows what a module needs when you hover over `@permission`, and the graph lists it as `permissions`, so a drawing can put a lock on it.
- On a source, `@permission` means the pipeline's own credential must hold the permission for the source to start. It holds only Meep's own permissions, so a source gated on any other stops its pipeline starting: gate the module under it instead.
- A credential revoked after it was verified is refused at the next gate, which looks again whenever the trust store has changed since.
- A gate's answer is kept on the message, so later gates needing the same permission, for it or the messages made from it, don't walk its family again.

A pipeline can narrow a definition for its own gates and `Verify`s with a `#permission` line at the top, before its modules, written as in `permissions.meep`: a narrower scope, only some of the authorities, or more requirements. It can't widen one; a gate that needs a permission a pipeline widens refuses every message, saying how.

```
#permission WriteNotes:~/notes/inbox/** authority=home requires=ReadNotes
```

When a refusal's reason isn't clear, see [troubleshooting.md](troubleshooting.md#a-message-is-refused-with-permissiondenied).

### Meep's own permissions
What Meep does on the machine is gated by permissions Meep defines itself, under `meep.`, a prefix kept for them. They're defined without a trust store, with no requirements; a `#permission meep.…` line in `permissions.meep`, or in a pipeline, narrows them as it narrows any other.

| Permission | Needed by | Scope matched against |
|---|---|---|
| `meep.files.read` | Read, Fetch.File, Learn and Classify with `file=` | the file's real path |
| `meep.files.write` | Save, Learn when it saves, Forget | the file's real path |
| `meep.network.listen` | every Listen | its host and port, as written |
| `meep.network.send` | Fetch.HTTP, and Send.HTTP, TCP and WebSocket (not a reply, nor a broadcast to a Listen's clients) | the host and port it sends to, and each redirect's |
| `meep.scripts.run` | embedded scripts, when Meep has them | — |
| `meep.daemon.observe`, `.inject`, `.control` | the daemon's clients, in phase 7 | — |

**A pipeline's own credential.** Every message a source emits descends from a verification the runtime makes when the pipeline starts, of the pipeline's own credential. So the messages hold what the pipeline does, and a gate, or a check in a verb, finds it on every line. Which credential a pipeline gets depends on where it came from:

- yours, as the owner, for a file on this machine that's yours alone, standard input, text sent to the daemon, an unsigned pipeline from a URL on this machine in developer mode (you turned it on), or a URL or Git address that pins its content (`#sha256=`, a commit's full SHA). A file is yours alone when it belongs to you, nobody else can change it, and every folder above it, up to the root, belongs to you or root and nobody else can change it either, as OpenSSH asks of `authorized_keys`; a sticky folder such as `/tmp` may be anyone's. Every file it `#include`s from this machine must be yours alone too;
- its signer's, for one signed by a key you trust, unless that key has been revoked in the trust store (`meep trust revoke`), which takes the credential from pipelines already running as well;
- none, for one nobody vouches for, such as a file, or an include, someone else could have written or changed. It can compute, but not touch files or the network. A pipeline built in code holds none unless the code that builds it says who vouches for it.

The owner's and a signer's hold every `meep.files.*`, `meep.network.*` and `meep.scripts.run` permission, unscoped, but never a name people define: a gate on `ReadNotes` still needs a verified credential of its own, even though a Listen's requests descend from the pipeline too. A message sent in from outside, through the daemon's inject, doesn't descend from the pipeline's credential, so it holds nothing, and Meep's own verbs refuse it, until injecting clients present credentials of their own (phase 7).

`meep trust init` isn't needed for any of this: Meep's permissions and a pipeline's credential work without a trust store. But a store that's there and can't be read, such as one others could change, holds everything back, Meep's own permissions included, until it's fixed, since it might narrow them.

**Meep's own folder.** No `meep.files.*` permission reaches `~/.meep` (or `MEEP_HOME`), however it's scoped, except its `trees` folder: the trust store, plugins, and the daemon's settings and tokens change only through `meep trust`, `Authorise`, `Revoke` and Meep itself. A path is judged by its real form, so a link into `~/.meep` from elsewhere is refused too. A tree in its own place, in `~/.meep/trees`, needs no permission at all.

**A pipeline's permissions and `authority=`.** A pipeline's own credential confers Meep's permissions as a reserved authority, `pipeline`, which needs no trust store, and which no authority in one can be called. A definition with no `authority=` allows pipelines. `#permission meep.files.write authority=home` in `permissions.meep` allows only messages whose line passes through a credential `home` conferred it to, and not pipelines on their own; `authority=home authority=pipeline` allows both.

**Running sources are checked again.** When the trust store changes, or a live change gives the pipeline new `#holds` or `#permission` lines, each running source is checked again for what it needed to start and what it checked then, such as a Listen's address. One that no longer holds it emits a `PermissionDenied`, the log says "… stopped, as the pipeline no longer holds …", and it stops.

**Starting with less.** `#holds` at the top of a pipeline, before its modules, keeps only what it lists of what its credential holds, with scopes as narrow as you like; `#holds none` keeps nothing. Lines add up. What it lists but the credential doesn't hold is noted on the verification, not held.

```
#holds meep.files.read "meep.files.write:~/notes/**" meep.network.send:api.example.com:443
```

**Scopes.** A file scope is matched against the path's real form: whole, with symbolic links and `..` followed, and in the case it has on disk, so `~/Notes/a.md` matches `~/notes/**` on a Mac's disk, and a link inside `~/notes` that leads elsewhere doesn't. The scope's fixed start, up to its first wildcard, is read the same way, so `/tmp/**` on a Mac means `/private/tmp/**`. A network scope is a host and port, as in `*.example.com:443`; without a port, as in `meep.network.send:*.example.com`, any port. The host is matched as the address writes it, in lower case and its ASCII form, not the addresses it resolves to, so `localhost` and `127.0.0.1` are different. Listening on every network is written `0.0.0.0` or `[::]`, and `*:8080` matches any host on that port.

**Public and private addresses.** `meep.network.send:public` allows sending only to addresses on the internet, and `meep.network.send:private` only to this machine and private networks (loopback, 10/8, 172.16/12, 192.168/16, link-local, 100.64/10, and IPv6's unique-local and link-local ranges), with a port after them if you like, as in `public:443`. They're checked against the address actually connected to, not the name: a name is resolved as the connection is made, and only addresses of the right kind are tried, so a request can't steer a Fetch at the router by naming something that resolves there. A connection limited this way is never shared with one that isn't. An address written as one, such as `127.0.0.1`, is checked when it's named. `0.0.0.0` and `::` are neither. For a Listen, `meep.network.listen:private` allows binding only to a private address.

**Gates on them.** A verb checks Meep's permissions in its own code, against what it's about to touch, so only scoped ones cost more than a kept answer. `meep lsp` shows what each verb checks when you hover over it, as in "Checks meep.files.write, on each file it writes". `@permission=meep.files.write` on any module works too, and `@proof=signed` there means only a proven request may cause it, not the pipeline itself.

## Output

### WriteLine
Writes a line for each message to standard output.

- Positional: `text`
- Example: `WriteLine "{msg.route} in {msg.minutesAway} min"`

### Save
Saves each message's text to a file, appending by default.

- Positional: `path`
- Parameters: `mode` (`Append` or `Overwrite`), `root`, `any-path`
- Example: `Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl"`
- Needs: `meep.files.write` for the file, for each message. A message that doesn't hold it isn't saved, becomes a `PermissionDenied`, and carries on downstream all the same, as every message through a passthrough does.
- What a `{msg…}` hole gives can't make the path absolute, start it with `~` or climb out with `..`, unless `any-path=true`, so behind a Listen, `Save "~/inbox/{msg.Query['name']}.txt"` only writes in `~/inbox`. Holes that don't read the message, such as constants, aren't limited.
- `root=` keeps every path inside a folder: relative paths start from it, and a path that leads anywhere else becomes an error, as in `Save "{msg.Name}.txt" root="~/inbox"`. It goes by the path as written, so a link inside the folder that points out of it is followed.

### Send
Sends messages on. Every Send is a passthrough: messages carry on downstream before the send has finished. Chosen by whether there's an address, and its scheme.

| Inflection | Positional | Emits | Parameters |
|---|---|---|---|
| Reply | none: replies through the message's lease | — | `text`, `status`, `content-type`, `error-reply`, `error-status`, `details`, `end`, `final` |
| Broadcast | `to`: a Listen.WebSocket's name | — | `text` |
| HTTP | `url`: `http://`, `https://` (POST) | `HttpResponse` | `text`, `content-type`, `timeout`, `max-size` |
| TCP | `url`: `tcp://` | `TcpMessage`; `TcpConnected`/`TcpDisconnected` with `connections` | `text`, `end`, `length`, `timeout`, `tries`, `retry-delay`, `connections`, `max-size` |
| WebSocket | `url`: `ws://`, `wss://` | `WebSocketText`, `WebSocketBinary`; `WebSocketConnected`/`WebSocketDisconnected` with `connections` | `text`, `timeout`, `tries`, `retry-delay`, `connections`, `max-size`, `ping`, `max-queued` |

- Examples: `Send text="Hello"`, `Send "Room"`, `Send "https://api.example.com/claims"`
- Needs: `meep.network.send` for the host and port, for HTTP (and each redirect), TCP and WebSocket. Replies and broadcasts need nothing: they answer connections a Listen accepted.

## Not counted here

- `Parse`, `Build` and `Host` in `src/Meep2.Meeplang/Hosting/HostingModules.cs` are modules the host uses to run pipelines, not verbs written in Meeplang.
- `Shout` in `tests/Meep2.TestPlugin` is a test plugin's verb.
