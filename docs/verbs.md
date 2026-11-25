# Verbs and inflections

The verbs Meeplang has so far, as of 24 November 2026, from the `[Verb]` attributes in `src/Meep2.Modules`, and `Verify`, which the runtime provides itself (`src/Meep2.Runtime/Verifying`), since only the runtime may make a verification. A verb with more than one inflection has a class for each. The runtime picks one by the message's type or by the parameters (usually the address's scheme), and `Verb.Inflection` names one directly, as in `Fetch.HTTP`.

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
| WriteLine | — | Passthrough | outlet (declared) |
| Save | — | Passthrough | outlet (declared) |
| Send | Reply, Broadcast, HTTP, TCP, WebSocket | Passthrough | outlet (declared) |

18 verbs, 28 inflections in all.

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

### Listen
Receives messages from the network, each carrying a lease so `Send` can reply. Chosen by the address's scheme. All three can be reconfigured while running.

| Inflection | Addresses | Emits | Parameters |
|---|---|---|---|
| HTTP | `http://`, `https://` | `HttpRequest` | `certificate`, `key`, `max-size`, `stream`, `default-status`, `default-reply`, `proof` |
| TCP | `tcp://` | `TcpMessage`; `TcpConnected`/`TcpDisconnected` with `connections` | `end`, `length`, `connections`, `max-size` |
| WebSocket | `ws://`, `wss://` | `WebSocketText`, `WebSocketBinary`; `WebSocketConnected`/`WebSocketDisconnected` with `connections` | `certificate`, `key`, `origins`, `connections`, `max-size`, `ping`, `idle-timeout`, `max-queued` |

- Positional: `url`
- Example: `Listen "ws://0.0.0.0:8080/chat/" as Room`
- `proof=required` on an HTTP Listen refuses, with 401, a request that has no `Signature-Input`, `Signature` and `Meep-Credential`, before it reaches the pipeline, and counts it as `denied`. It only keeps anonymous requests out; `Verify` still checks the signature and the credential. `proof=optional`, the default, lets every request in.
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
- A tree stays in memory while any Learn, Classify or Forget uses it. When the last lets go, as when its pipelines end, it's saved (unless every Learn had `save-every=0`) and leaves memory, to be loaded from its file when it's next named. A tree kept in memory only, with `save-every=0`, is gone once no pipeline uses it.

### Classify
Emits a `ClassifiedMessage` with the class and score a named tree gives each message.

- Positional: `tree`
- Parameters: `text`, `file`, `min-training`, `against`
- Example: `Classify "mail" against="Ham"`

### Forget
Makes a named tree forget everything, or one class, then passes the message on.

- Positional: `tree`
- Parameters: `class`, `file`
- Example: `Forget "topics" class="Science"`

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

### Gates: `@permission`
Not a verb, but a modifier any module can have: `@permission=Name` makes the module a gate. A message reaches it only if every line of its family, back to where it began, passes through a `Verify` of a credential that passed and holds the permission, conferred by an authority the permission's definition allows, with everything the definition requires. A message made by joining others, as `Zip` and `Batch` do, needs the permission on every line it came from.

```
Listen "http://0.0.0.0:8080/notes/" proof=required
    Verify from=msg.Headers["Meep-Credential"]
        Send text="Welcome." @permission=ReadNotes
            Send error-status=403 error-reply="{msg.Error}" @errors
```

- Write it more than once to need several; all are needed. A scope goes in quotes: `@permission="meep.files.write:~/notes/**"`, and the credential must hold a scope at least as wide.
- A permission defined in the trust store's `permissions.meep` must be held as its definition says. One that isn't defined can still be needed: any trusted authority confers it, and it requires nothing, unless the pipeline's own `#permission` line narrows it. `meep lsp` warns about a name the store doesn't define, in case it's misspelt.
- A refused message becomes a `PermissionDenied` error from the module, with the message as its parent, saying which permissions it lacked and why (`Needed`, `Reasons`). It follows the error policy as other errors do, so an `@errors` module can answer it. One nothing handles is logged, but refusals never count towards `stop-pipeline`, so unwelcome requests can't stop a pipeline. An error refused at a gate passes the module by instead, still looking for a module that takes errors.
- A refused message goes no further along that path, even past a passthrough module or `Distribute`.
- Each module counts what it refused as `denied`, in `meep` and Synesthesia (a Listen with `proof=required` counts its 401s there too), and the OpenTelemetry metric `meep.module.messages` has `meep.event = denied`. A refusal is also an event, `meep.permission.denied`, on the span of the module that emitted the message.
- A verb can require a permission itself, which no pipeline can remove; `@permission` adds to it. `meep lsp` shows what a module needs when you hover over `@permission`, and the graph lists it as `permissions`, so a drawing can put a lock on it.
- On a source, `@permission` means the pipeline must hold the permission for the source to start. Pipelines don't carry credentials of their own yet, so for now a gated source stops its pipeline starting.
- A credential revoked after it was verified is refused at the next gate, which looks again whenever the trust store has changed since.
- A gate's answer is kept on the message, so later gates needing the same permission, for it or the messages made from it, don't walk its family again.

A pipeline can narrow a definition for its own gates and `Verify`s with a `#permission` line at the top, before its modules, written as in `permissions.meep`: a narrower scope, only some of the authorities, or more requirements. It can't widen one; a gate that needs a permission a pipeline widens refuses every message, saying how.

```
#permission WriteNotes:~/notes/inbox/** authority=home requires=ReadNotes
```

When a refusal's reason isn't clear, see [troubleshooting.md](troubleshooting.md#a-message-is-refused-with-permissiondenied).

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

## Not counted here

- `Parse`, `Build` and `Host` in `src/Meep2.Meeplang/Hosting/HostingModules.cs` are modules the host uses to run pipelines, not verbs written in Meeplang.
- `Shout` in `tests/Meep2.TestPlugin` is a test plugin's verb.
