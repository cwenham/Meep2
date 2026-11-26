# Permissions, phase 6: Meep's own permissions and a pipeline's own credential

Written 26 November 2026. Phase 6 of the plan in `permissions.md`: the `meep.*` permissions on what Meep does on the machine, scopes matched against real paths and network endpoints, a pipeline's own credential (Q252 C) with a directive to start with less, and trusting SSH CA keys from a pipeline (Q303). The work is on the branch `permissions-phase-6`, made from `main` at 10fd98d, not committed, merged or pushed. This covers what was built, the decisions I made while building, what I found in passing, and questions numbered from Q308, for you to answer inline.

Partway through, you said breaking changes are fine: Meep2 is pre-release, with no installs or pipelines in use, so security and getting the design right come before compatibility. I'd first chosen four things to keep existing pipelines working unchanged; I changed all four to the stricter choice (see "What changes for existing pipelines").

## Results

- The build has no warnings, with warnings as errors.
- The whole suite passes: 939 of 939 (908 before, on `main`, and 31 new tests, counting each case of a test with arguments), in three full runs of the finished work, with none of the known flaky tests failing.
- On `main`, before any change, `Listen_takes_https_with_a_certificate` failed in two runs in a row with "connection refused", in the scratch worktree under `/private/tmp`. It has passed in every run on the branch since. It isn't one of the three known flaky tests; see "Found in passing".
- No test touches the real `~/.meep`: each uses a temporary home folder.
- **Performance**, against `main` built in a scratch worktree, alternating: on this Mac, no difference beyond noise for a pipeline that checks nothing, and a check of an unscoped permission costs well under a microsecond. On strawberry.local, a pipeline that checks nothing is 4–12% slower, and Save 6–9 µs (14–20%) slower a message, of which the check itself measures about 2 µs. A `#holds` scoped by path adds about 30 µs a Save on the Mac and 15–20 µs on the Pi. Details below.
- **On strawberry.local** (linux-arm64, its own umask of 002), the whole suite from a published copy: 894 of 939, and the same 45 tests fail with `main`'s own copy (863 of 908), all needing repository files a published copy doesn't have (plugin builds, `examples/`). All 31 new tests pass there. The Pi's run is what showed that Linux's temporary folder, `/tmp`, is shared, so a sticky folder is now accepted (below). The test and benchmark copies, in `~/meep-phase6-*`, are deleted; the installed daemon wasn't touched and is still running.

## What changes for existing pipelines

- **Your own unsigned file still runs with your rights**, holding every `meep.files.*`, `meep.network.*` and `meep.scripts.run` permission, but only if the file and its folder belong to you and nobody else can change them. Otherwise it gets no credential, and can't read, write, listen or send. All the examples qualify as they are.
- **`meep trust init` isn't required.** Meep's permissions are defined without a trust store, and a pipeline's credential doesn't need one. But a store that exists and can't be read, as one others could change, now holds back Meep's own permissions too, so every Read, Save, Fetch, Send and Listen is refused until it's fixed (Q312). Before, only `Verify` and gates on names people define were affected.
- **A pinned URL or Git pipeline runs with your rights; a signed one with its signer's**, which today holds the same permissions. An unsigned pipeline from a local URL in developer mode runs with none (Q309).
- **A message sent in with the daemon's inject holds nothing**, so a Save, Fetch or Send it reaches refuses it, until clients present credentials of their own in phase 7 (Q311).
- **A pipeline built in code holds nothing** unless the code says who vouches for it (`PipelineOptions.Credential`). Meep's own hosts (the daemon, `meep run`, their bootstrap and reload pipelines) and the test helpers say; an embedding host must too.
- **A source gated on a name people define can never start**, since a pipeline's credential holds only Meep's permissions (it couldn't start before either, for want of any credential).
- **`Parse` without `kind=` reads a `.pub` line as an `AuthorityKey`**, where it was an error.
- **A `meep.` name that isn't Meep's** is refused at a gate, warned about by `meep lsp` and `meep trust issue`, and a problem in `permissions.meep`.

## What was built

### Meep's own permissions

`MeepPermissions` in the SDK defines eight, under the `meep.` prefix, which Meep keeps for itself:

| Permission | Needed by | Scope matched against |
|---|---|---|
| `meep.files.read` | Read, Fetch.File, Learn and Classify with `file=` | the file's real path |
| `meep.files.write` | Save, Learn when it saves, Forget | the file's real path |
| `meep.network.listen` | every Listen | its host and port, as written |
| `meep.network.send` | Fetch.HTTP, Send.HTTP, Send.TCP, Send.WebSocket, and every redirect they follow | the host and port |
| `meep.scripts.run` | embedded scripts, when Meep has them | — |
| `meep.daemon.observe`, `.inject`, `.control` | the daemon's clients, from phase 7 | — |

- **Defined without a trust store (Q279).** `PermissionDefinitions.Find` falls back to Meep's own definition, with no requirements, so they're well defined with no `permissions.meep`, no store, or a store that can't be read. A `#permission meep.…` line in `permissions.meep`, or a pipeline's, narrows Meep's definition as it narrows any other. A line for a `meep.` name that isn't Meep's is listed as a problem; a gate needing one refuses everything, saying "Meep has no permission called meep.file.read …", and `meep lsp` and `meep trust issue` warn about it.
- **Checked in code, against the target.** The verbs call two new methods on `ModuleContext`, rather than having a fixed `[RequiresPermission]`, since a scope has to be matched against what they touch:
  - `DemandFile(permission, message, path)`, which matches a scope against the path's real form.
  - `DemandEndpoint(permission, message, host, port)`, which matches `host:port`.
  - A source passes `null` for the message, meaning the pipeline's own credential. A refusal in a source becomes a `PermissionDenied` from it, counted as denied, with a notice ("Read #1 stopped, as it needs meep.files.read: …"), and the source stops instead of being restarted.
- **Where each check is.** Read and the three Listens check before reading or binding. Save, Fetch.File and the tree verbs check for each message. Fetch.HTTP and Send.HTTP check the address before preparing a body, and each redirect before following it. Send.TCP and Send.WebSocket check the address before joining a connection. Replies and broadcasts need nothing: they answer connections a Listen accepted.
- **Trees.** Learn and Classify need `meep.files.read` when `file=` names the tree's file; a tree in its own place in `~/.meep/trees` is read without asking, as the plan says. Saving a tree is writing a file, wherever it is: Learn needs `meep.files.write` when `save-every` is above 0 (the default 5), and Forget always, for each message.
- **`meep.scripts.run`** is defined, and `writing-verbs.md` gives the contract for the verb that will run scripts: `[RequiresPermission(MeepPermissions.ScriptsRun)]`, scripts' messages descend from the message they ran for, and scripts may read the security types but never construct a `VerifiedMessage` or mark a certificate. There's no embedded scripting in the repository yet, so that's the hook.
- **`meep.daemon.*`** are defined only. Mapping tokens to them is phase 7.

### A pipeline's own credential (Q252 C)

- **`PipelineCredential`** (SDK) is a message describing who vouches for a pipeline: `Vouch` (`Owner`, `Signer` or `Nobody`), `Holder`, `KeyFingerprint` for a signer, `Why`, and `Permissions`, which are Meep's own for the owner and a signer (`MeepPermissions.ForPipelines`: files, network and scripts, not the daemon's), and none for nobody. It's made with `PipelineCredential.Owner(why)`, `SignedBy(signer, fingerprint)` or `None(why)`. Anyone can make one; it gives nothing until the runtime verifies it.
- **`PipelineOptions.Credential`**, none unless set, so a host that forgets gives a pipeline nothing rather than everything, and **`PipelineOptions.Holds`**, from `#holds`.
- **The pipeline's verification.** When a pipeline is made, and again when a live change gives it new options, the runtime makes a `VerifiedMessage` of its credential: `Kind` `Pipeline`, `Proof` `Pipeline` (both new enum values), its permissions narrowed by `#holds`, no expiry, and emitted by a module info of its own, `Pipeline` number 0 (`Pipeline.Credential`).
- **Every message descends from it.** `Stamper` gives a new message with no parents the message being handled, as before, or now the pipeline's verification when there's none to hand: everything a source emits, and what a module emits from outside handling, such as a server's reply on a connection Send opened. A message sent in with `Pipeline.Inject` doesn't: it comes from outside, so it holds nothing (Q311).
- **Gates count it for Meep's permissions only.** `PermissionLines` accepts a genuine, emitted verification of kind `Pipeline`, but only for a `meep.` name. For any other, it's passed by as a message that isn't a verified credential, so a gate on `ReadNotes` still needs a `Verify`, and a Listen's anonymous requests, which descend from the pipeline too, are refused as before (Q308). The permissions it holds are conferred by this machine's own authority, if the store has one, for a definition's `authority=` (Q316).
- **Where it comes from**, set by the daemon and `meep run` through `PipelineText.Credential`, which `Build` puts into the pipeline's options:
  - a file on this machine, if it and its folder belong to the user Meep runs as and nobody else can change them (`PrivateFolders.WhyNotUsersAlone`, the trust store's rule, with Debian's private groups), standard input, or text sent to the daemon: the owner's;
  - a file someone else owns or could change: none, saying which (Q320);
  - a URL or Git file signed by a key you trust, or in a Git commit signed by one: its signer's;
  - a URL pinned by `#sha256=`, or a Git file at a pinned commit: the owner's, since the owner chose that content (Q309);
  - an unsigned URL on this machine in developer mode: none (Q309).
- **Gated sources (Q290 revisited).** A source with `@permission`, or whose verb requires a permission, starts if the pipeline's own verification passes that gate, and otherwise stops the pipeline starting, saying why. A source gated on a name that isn't Meep's can never start, since the pipeline's credential holds only Meep's; the error says to gate the module under it instead.

### Starting with less: `#holds`

```
#holds meep.files.read "meep.files.write:~/notes/**" meep.network.send:api.example.com:443
#holds none
```

- At the left margin, in the pipeline's own file, before its modules, as `#permission` is. Lines add up.
- It lists only Meep's own permissions, each with a scope if it has one; anything else is a load error saying why. `#holds none` keeps nothing.
- The pipeline's verification keeps what's listed that its credential covers (`PermissionName.Covers`). What's listed but not covered is a note on the verification, not held: "#holds lists meep.files.read, but this pipeline has no credential of its own …".
- `meep lsp` completes and describes it. I named it `#holds` rather than the plan's `#permissions` (Q310).

### Scopes against real targets

- **Files (Q269).** `RealPaths.Of(path)` (in `Meep2.Shared`) gives a path's real form: `~` read as the home folder, whole, symbolic links followed, `.` and `..` resolved, and the case the disk has. The part that exists goes through the system's `realpath`, which on macOS also gives the case on disk. The rest, which doesn't exist yet, is added as written, except that a dangling link at its start is followed, since writing through it would create its target elsewhere. A scope's fixed start, its parts up to the first wildcard, is read in real form too, so `/tmp/**` on a Mac means `/private/tmp/**`, a scope written through a link matches what's behind it, and `~/Notes/**` matches `~/notes` on a disk that ignores case. Matching is then exact, as before.
- **Only when needed.** A file target is kept as written until a scope is matched against it (`LazyFile`). The check first asks the question a gate would, without the target, whose answer is kept on messages; only if that fails, such as for a pipeline whose `#holds` is scoped, is the target matched. So a pipeline holding an unscoped permission makes no system call to check one.
- **Network endpoints.** Proposed and built: a scope is one part, `host:port`, matched with the usual `*` and `?`, as in `meep.network.send:*.example.com:443`. Without a port, as in `meep.network.send:*.example.com`, any port. The target is the host as the address writes it, in lower case, in its ASCII (IDNA) form, without a trailing dot, with IPv6 in brackets (`Peers.Endpoint`'s form), then its port. Listening on every network is written as the Listen has it, `0.0.0.0` or `[::]`, and `*:8080` matches any. The host isn't resolved: `localhost` and `127.0.0.1` are different (Q313).

### Trusting SSH CA keys from a pipeline (Q303)

- **`AuthorityKey`** (SDK): an SSH public key as a `.pub` file has it, with `KeyType`, `Key`, `Comment` and `Fingerprint`. It reads every type Meep can check signatures with; a certificate, a key whose bytes don't match its type, or one it can't read is refused, saying why.
- **`SecurityText`** reads a `.pub` line as `SecurityTextKind.AuthorityKey`, so `Parse` emits one, and `Parse kind=authority` asks for one. Read as a credential, as `Verify` and `Parse kind=credential` do, it's still the error "This is an SSH public key …, not a certificate".
- **`Authorise.Authority`** takes an `AuthorityKey`, needs the new **`AuthoriseAuthority`** permission, and, like the other two, signed proof by default. It adds the key to `authorities` for `meep-credential`, and with `grants=true` for `meep-grant` too, as `meep trust add-authority` does, then reads the store again so it's in force at once. `name=` names it; without it, the key's comment if that's one word, or `authority-` and the start of its fingerprint. A key trusted already is passed on; a name in use for another key is an error. What it trusts must come from a covered part of a signed request, as for the others.
- `AuthoriseAuthority` joins `SecurityPermissions.OverTrust`, so only a credential holding `AuthoriseAny` may grant it.

### Tools

- `meep lsp` warns about a `meep.` name that isn't Meep's, with or without a trust store, and no longer warns that Meep's own aren't defined. `#holds` is completed and described, and the hover for `@proof` mentions `pipeline`.
- `@proof=pipeline` accepts only the pipeline's own credential; `@proof=signed` refuses it, so `@permission=meep.files.write @proof=signed` means only a proven request may cause a write. `@proof=any` includes it.
- `meep trust issue` no longer warns that Meep's own permissions aren't defined, and warns about a misspelt `meep.` name.
- `TrustStoreSource.Existing`: the store if there is one, without making one, for gates, since every pipeline's verbs now ask. `meep run` looks for `~/.meep/trust` at most every ten seconds until it's there. `TrustStoreSource.CantBeRead` says when there's a store that can't be read, and then every requirement, Meep's own included, is "This machine's trust store can't be read …" (Q312).

### Documentation

- `docs/verbs.md`: a "Needs:" line on each verb that touches the machine, `Authorise.Authority`, `Parse kind=authority`, `@proof=pipeline`, and a section, "Meep's own permissions", on the table above, a pipeline's credential, `#holds` and how scopes match.
- `docs/writing-verbs.md`: `DemandFile` and `DemandEndpoint`, when a verb must check, how a source checks, the contract for scripts, and `AuthorityKey` and `PipelineCredential` among what a plugin may make.
- `docs/troubleshooting.md`: the gated-source section rewritten, `Authorise.Authority`'s errors, and a new section, "Meep's own permissions", with a part for each refusal and note.
- `docs/Design/synesthesia-handover.md`: what phase 6 changes in what the daemon sends, chiefly the pipeline's verification at the top of every message's family.

### The tests

`PipelineCredentialTests` (28 with their cases):

- An owner's pipeline reads and writes without a trust store, and its messages descend from a genuine verification of kind `Pipeline`, with Meep's permissions and no expiry.
- A pipeline nobody vouches for computes, but Save refuses each message and Read is refused once and stops, counted as denied and not restarted.
- `#holds` scoped to a folder lets Save write inside it, not outside, and refuses Read. `#holds none`; five mistakes in `#holds` lines; a note for what's listed but not held.
- A gated source starts when the pipeline holds what it needs, and not when nobody vouches for it.
- A message injected into a Save is refused, and doesn't descend from the pipeline's credential.
- An unsigned file earns the owner's credential, and not when others can write to it or its folder; options that don't say hold nothing.
- A trust store that can't be read refuses Meep's own permissions, and logs why.
- Real paths: a link out of an allowed folder and a dangling link are refused, `..` that stays inside is allowed; `RealPaths.Of` follows links, resolves `..`, takes the case on disk where the disk ignores case, and a scope written through a link matches.
- Network scopes, eleven cases: case, ports and none, a trailing dot, IPv6, IDNA, every network, and a name against an address.
- Fetch through a redirect to another host, Fetch and Send.HTTP to a host outside the scope, and Send.TCP and Send.WebSocket, all refused; a Listen whose address is outside its scope is refused before it binds.
- Trees: Learn that saves and Forget need write, Learn that doesn't save and Classify need only read.
- Meep's definitions without a store, a misspelt one in `permissions.meep`; a store requiring `WriteNotes` for `meep.files.write`, which refuses the pipeline alone and lets alice's verified request write; `authority=ca`, which the pipeline's own credential can't meet; a gate on a misspelt name.
- `@proof=signed` refuses the pipeline's own credential, `@proof=pipeline` accepts it.
- `AuthorityKey` read from a `.pub` line, and refused for a certificate and four kinds of junk.

Elsewhere: `AuthoriseTests` has `Authorise.Authority` trusting a department's CA, after which its credentials verify; trusted again, a name clash, a name from the comment with `grants=true`, and refused with only `AuthoriseCertificate`. `SourceTests` checks that a signed URL gets its signer's credential, a pinned one the owner's, and an unsigned one in developer mode none, and that `meep run` of the last can't save a file while the pinned one can. `LanguageServerTests` checks the warning for a misspelt `meep.` name and the hover for `#holds`.

The test helpers that build pipelines (`TestRun`, and the plugin tests' bootstrap) now vouch for them as their owner, as `meep run` does for your own file, and the benchmark does the same. Seven existing tests changed, each for a change this phase makes on purpose:
- `GateTests`' gated-source test (two cases), for the new message when a source is gated on a name that isn't Meep's;
- a test each in `ParserTests` and `GateTests` whose expected message lists the directives, which now include `#holds`;
- three in `AuthoriseTests`: two because a `.pub` line is now an `AuthorityKey` rather than an error (one now also checks that `Parse` emits it), and one because `Authorise` has three inflections;
- `TrustCommandTests`' test of `meep trust issue`, which no longer warns that `meep.files.write` isn't defined, and now checks the warnings for an undefined name and a misspelt `meep.` one.

## Performance

Measured on this Mac (Intel Xeon W-2140B, 16 threads, x64) and on strawberry.local (Raspberry Pi 4, 4 cores, arm64), each against `main` in a scratch worktree, alternating. `MeepPermissionCost` is a new stopwatch benchmark, `dotnet run -c Release -- meep-permissions`; `main` ran the same file without the parts that need this branch. Per message, end to end, best of five:

| | Mac, `main` | Mac, branch | Pi, `main` | Pi, branch |
|---|---|---|---|---|
| a source and four steps, no gates | 4.2–4.6 µs, 510–518 B | 3.6–4.4 µs, 518–535 B | 4.6–4.9 µs, 523–546 B | 4.8–5.8 µs, 525–549 B |
| a source and a step that only checks `meep.files.write` | — | 1.5 µs, 602 B | — | 4.7–5.0 µs, 602 B |
| a source and Save, appending | 137–156 µs, 2,605 B | 93–144 µs, 3,069 B | 43.6–46.4 µs, 2,468 B | 51.5–59.0 µs, 2,933 B |
| the same, `#holds` scoped to Save's folder | — | 108 µs, 5,981 B (with Save at 72–78 µs in the same runs) | — | 70–72 µs, 5,214 B |

- **Pipelines that check nothing** pay for every message from a source having a parent now, the pipeline's verification: one more step in stamping it. On the Mac that's lost in noise; on the Pi, 0.2–0.5 µs in a five-module chain.
- **A check of an unscoped permission** is a kept answer on the pipeline's verification, one generation up: under a microsecond on the Mac, and about 2 µs on the Pi, with about 465 B allocated (the walk's objects and the answer kept on the message).
- **Save on the Pi** is 6–9 µs slower, more than the check alone accounts for; the rest may be the collector's share of the extra bytes, or the file system's noise, which on this Mac's runs was larger than the difference. Save's own work (opening, appending, closing the file) dominates on both.
- **A scoped holding** works out the file's real form for each message with `realpath`, and its scope's real form at most once a second (cached after the first measurements, which were 70–140 µs on the Mac before the cache).
- The phase 4 gate benchmark (`gates`) shows the same costs on both, within noise.

## Decisions I made while building

- **A pipeline's credential holds only Meep's own permissions** (Q308). Every message from a source descends from the pipeline's verification, including a Listen's requests from anyone. If the pipeline's credential could hold `ReadNotes`, every anonymous request would pass a `ReadNotes` gate. So the pipeline's verification counts at gates for `meep.` names only, and is passed by for any other, and a gate's reason for those stays "Nothing on one of the lines … is a verified credential".
- **Checks in the verbs' code, not `[RequiresPermission]`.** A permission held with a scope doesn't cover the unscoped name (phase 1's rule), so a fixed gate on `meep.files.write` would refuse every pipeline with `#holds "meep.files.write:~/notes/**"`. Checking in code against the file or endpoint lets scopes work. The cost is that `meep lsp`'s hover and the graph don't show these needs (Q315).
- **The fast path.** A check asks first without the target, keeping its answer on the messages as a gate does, and matches the target only if that fails. For an unscoped holding, which is every pipeline without `#holds` and every credential with the plain name, it costs a kept answer.
- **Real paths with `realpath`.** On macOS, `realpath(3)` gives the case on disk as well as following links, which I checked before relying on it; on Linux, it follows links and the case is the case. A scope's fixed start is read the same way, which Q269 didn't ask for, but without it `/tmp/**` would match nothing on a Mac, and every path under `/var/folders` (where temporary files go) would miss a scope written as it's shown.
- **Network scopes by name** (Q313), as above. Matching resolved addresses would mean resolving before the check and then connecting to exactly that address, or a DNS answer could change between the two.
- **Redirects are checked.** Otherwise `meep.network.send:api.example.com` would let a redirect take a request anywhere.
- **Your own file must be yours alone** (Q320). Q253's proposal said an unsigned file is proven by "the file being yours and the daemon running as you", so I check the file and its folder as the trust store's files are checked. A file in a shared folder anyone can write to could otherwise be swapped for one that runs with your rights. A sticky folder, such as `/tmp`, is accepted, since only a file's owner can rename or delete it there; the Pi's tests showed the need, as Linux's temporary folder is `/tmp`, where a Mac's is private.
- **Pinned and developer mode** (Q309). A pin is the owner choosing exact content, so a pinned URL or commit gets the owner's credential, as a file of their own does. Developer mode runs an unsigned pipeline from a local URL, which any local process could be serving, so it gets none. That's the one case where Meep runs a pipeline nobody vouches for today.
- **Pipelines made in code hold nothing** unless their options say who vouches for them. I first gave them the owner's, which kept embedding and tests working unchanged; after your note on breaking changes, a host that forgets now fails closed. Meep's own hosts say, for their pipelines and for their bootstrap and reload pipelines, which are Meep's own.
- **Injected messages hold nothing** until phase 7 gives them the injecting client's credential (Q311). I first gave them the pipeline's, so injecting into a Save kept working; but a client with only the `inject` capability shouldn't be able to make a pipeline write files.
- **A source refused once stops.** It would only be refused again; restarting would fill the log. A change to `#holds` or the store doesn't restart it.
- **`TrustStoreSource.Existing`** for gates, so `meep run` doesn't make `~/.meep/trust` for every pipeline just because its verbs check Meep's permissions.
- **A store that can't be read fails closed** (Q312), Meep's own permissions included, since it might narrow them. I first fell back to Meep's definitions, which kept pipelines working on a machine with a broken store, but would silently drop a narrowing such as `requires=WriteNotes`.
- **The pipeline's verification is made again on every live change**, whether or not `#holds` changed, as it's cheap and keeps the two from drifting. A reload keeps the credential the pipeline was loaded with.
- **`AuthorityKey` rather than reusing `PublicKey`.** `PublicKey` is in `Meep2.Security`, which scripts and plugins don't see, and isn't a message. `Authorise.Authority` turns one into the other.
- **`Parse` without `kind=` now reads a `.pub` line as an `AuthorityKey`**, where before it was an error. With `kind=credential`, the error is the same as before.
- **`DemandFile` returns nothing.** It could return the real path, but the verbs open the path as written, which names the same file, and their messages keep the path as written.

## Found in passing

- **`Listen_takes_https_with_a_certificate` failed twice on `main`**, before any change, in a worktree under `/private/tmp`, with "connection refused" after about 13 seconds; it has passed in every run on the branch in `~/Projects`. I didn't chase it; it may depend on where the test's certificate files are, or on the port it picks.
- **A `meep.` misspelling would have failed open before this phase**: Q289's rule made an undefined name conferred by any authority, so `@permission=meep.file.read` was held by any credential carrying the misspelling. Reserving the prefix closes that.
- **Learn's last save, when it's disposed, isn't checked.** It saves what messages that held `meep.files.write` taught it, so nothing is written that a refused message caused.
- **Listen's certificate and key files** (`certificate=`, `key=`) aren't checked against `meep.files.read`: they're configuration the pipeline's author wrote, not data. Nor are the trust store and plugin files Meep reads itself (Q317).
- **A path can change between its check and its use**, as when someone swaps a folder for a link. Whoever can do that can already write there without Meep, so I've noted it in `RealPaths` rather than guarding against it.
- **A folder that doesn't exist yet is matched as written**, case and all, so on a Mac a scope `~/newdir/**` doesn't match a Save to `~/NewDir/a`, which would create `~/NewDir`. It fails closed.
- **Text sent to the daemon over its socket gets the owner's credential**, as the client that sent it had the `control` capability. Phase 7 could give it the client's instead.
- **`#include`d files from this machine aren't checked for ownership**, only the pipeline's own file. An include from a folder others can write to could change what a pipeline does (Q320).
- **The trust store's watcher narrows its folder to 700** when it starts (`PrivateFolders.Create`), so a test can't make the store unreadable by opening up the folder; it opens up a file instead.
- **A signer's revoked key doesn't take its pipelines' credentials with it.** Pipeline signers are trusted through plugins' `allowed_signers`, not the trust store's `revoked` (Q318).
- **A running Listen isn't stopped when the store changes** to narrow `meep.network.listen`, since sources are checked when they start (Q319).

## Questions

**Q308.** A pipeline's own credential holds only Meep's permissions, because a Listen's requests descend from it, so holding a name like `ReadNotes` would let anonymous requests through its gates. That means a signed pipeline can't carry its signer's own Meep credential (say, the publisher's `ReadNotes`) for its sources' messages. Which?
- A: Keep it: pipelines hold Meep's permissions; people's permissions come only from verified credentials on the message's line.
- B: Let a pipeline's credential hold people's permissions too, but not for messages from inlets (Listen's family), which would start from a verification of their own that holds only Meep's.

I'd keep A. B splits the pipeline's credential in two, and the case for it (a timer-driven pipeline that needs `ReadNotes`) is met by a `Verify` of a credential file under the source.

**Answer:** A

**Q309.** A URL or Git pipeline pinned by its content gets the owner's credential; an unsigned pipeline from a local URL in developer mode gets none. Is that right? The alternative for pins is none, as the plan's words ("a pipeline from a URL without a signature gets none") would have it, which would stop every pinned pipeline from saving a file or listening. The alternative for developer mode is the owner's, which keeps developers' pipelines working fully, at the cost of running whatever a local process serves with your rights.

**Answer:** In developer mode the pipeline should run with the owner's rights.

**Q310.** The plan called the directive `#permissions`, as in `#permissions meep.files.read`. I named it `#holds`, since `#permission` (one letter shorter) already narrows a definition, and a typo between the two would silently do the other thing. Keep `#holds`, or another name?

**Answer:** Keep it.

**Q311.** Messages sent in through the daemon's inject hold nothing, so Meep's own verbs refuse them, until phase 7 gives them the injecting client's credential. Is that right for now, or should a client with the `control` capability (who could start a pipeline with the owner's rights anyway) inject with the pipeline's credential, and one with only `inject` with none?

**Answer:** It's right for now. It'll wait until phase 7.

**Q312.** When the trust store exists but can't be read, every permission is refused, Meep's own included, since the store might narrow them: a store someone else could change stops every Read, Save, Fetch, Send and Listen until it's fixed, which is loud but safe. The gentler alternative is to fail closed only for the permissions the last good store narrowed, but a daemon that starts with a broken store has no last good one. Keep it strict?

**Answer:** Keep strict and make a note in the troubleshooting guide.

**Q313.** Network scopes match the host as the address writes it, not the addresses it resolves to. A pipeline that should only reach the internet, never the home network (against a request steering a Fetch at the router, say), can't say so. Add a keyword scope such as `meep.network.send:public` (or `:private`), checked against the resolved address at connect time? Not now, or now?

**Answer:** Add it now.

**Q314.** A tree in its own place in `~/.meep/trees` is read without `meep.files.read`, as the plan says, but saving it needs `meep.files.write` for that path, so `#holds "meep.files.write:~/notes/**"` stops Learn saving its trees. Should Meep's own folders (`~/.meep/trees`) be exempt from `meep.files.write` too, as reading them is?

*Discussed in chat, 26 November.* A pipeline can already keep its trees working with `#holds "meep.files.write:~/.meep/trees/**"` beside its other scopes. `~/.meep/**` would work too, but it reaches the trust store, the plugin cache and the daemon's settings, so a pipeline holding it could `Save` a line into `~/.meep/trust/authorities` and trust a CA without passing `Authorise.Authority`'s gate. Q321 would settle this and Q314 together.

**Answer:** Answered in Q321.

**Q315.** The verbs check Meep's permissions in code, so `meep lsp`'s hover, the verb catalogue and the graph's `permissions` (the lock) don't show that Save needs `meep.files.write`. Add a declarative attribute, say `[ChecksPermission(MeepPermissions.FilesWrite)]`, that the registry reads for tools only? I'd add it, but leave it off the graph's lock, since nearly every I/O module would have one.

**Answer:** Okay, add the attribute.

**Q316.** A pipeline's own credential is treated as conferred by this machine's own authority (the one `meep trust init` made), so `#permission meep.files.write authority=home` in `permissions.meep` refuses pipelines unless `home` is that authority, and a store with no authority of its own refuses them whenever a definition names one. Is that the right authority, or should pipelines be exempt from `authority=` on Meep's permissions?

*Agreed in chat, 26 November, after the warnings: the middle option, a reserved authority name for pipelines.* A pipeline's own credential confers Meep's permissions as a reserved authority called `pipeline`, which needs no trust store:
- a definition with no `authority=` allows pipelines, as now;
- `authority=home` alone means requests whose credentials `home` conferred, and not pipelines;
- `authority=home authority=pipeline` allows both.

So whether pipelines are bound by `authority=` is the store owner's choice, line by line, and there's no default to reverse later. `meep trust init`, `meep trust add-authority` and `Authorise.Authority` refuse an authority named `pipeline`, and `PermissionDefinitions` doesn't report it as an authority missing from the store. Chosen over exempting pipelines outright, which would make `authority=` on Meep's permissions inert and, if reversed after a release, change the meaning of existing lines and break pipelines on upgrade.

**Answer:** The middle option as described above.

**Q317.** Files Meep reads as configuration rather than data aren't checked against `meep.files.read`: Listen's `certificate=` and `key=`, `#include`d files, plugins, and the trust store. Should a Listen's certificate and key be checked, as files the pipeline names?

**Answer:** Yes.

**Q318.** A signed pipeline's credential names its signer's key, but revoking that key in the trust store (`meep trust revoke <key>`) doesn't take the credential away from pipelines already running, or stop new ones loading, since pipeline signers are trusted through `allowed_signers`. Should a pipeline signer's key revoked in the store fail the pipeline's credential (none), at load and at the next gate?

**Answer:** Yes.

**Q319.** Sources are checked when they start, so a Listen keeps listening if a store reload or a live change narrows `meep.network.listen` under it. Should a source be stopped when its check would now fail, on a store reload or a change to `#holds`?

**Answer:** Yes.

**Q320.** An unsigned pipeline file earns the owner's credential only if it and its folder belong to you and nobody else can change them. Should that go further:
- A: check every folder up to the home folder (or `/`), as OpenSSH does for `authorized_keys`, so a file in a folder inside a world-writable one is refused too;
- B: check every `#include`d file from this machine the same way, refusing the credential if any fails;
- C: both.

I'd do C: includes add modules as surely as the pipeline's own file does.

**Answer:** C

**Q321.** Today a pipeline holding plain `meep.files.write`, as every pipeline you run does unless `#holds` narrows it, can write anywhere you can, `~/.meep` included. It could `Save` into `~/.meep/trust` (`authorities`, `grants`, `revoked`, `permissions.meep`), the plugin cache, or the daemon's settings and tokens. That's no more than you could do yourself. But a pipeline that saves where a request says, as behind a Listen with `any-path=true`, could let a remote client add an authority or revoke one, getting round `Authorise` and `Revoke` and their gates. Should Meep's own folders be outside every `meep.files.*` scope, so they change only through `meep trust`, `Authorise` and `Revoke`?
- A: Yes, all of `~/.meep` except `~/.meep/trees`, which a pipeline may read and write as its trees. This would also answer Q314: trees would need no scope of their own.
- B: Yes, but only `~/.meep/trust` and the daemon's settings and tokens; plugins' and other folders stay ordinary files.
- C: No: `meep.files.write` means anywhere you can write, and `#holds` is how a pipeline gives that up.

I'd do A, refused with a reason that names the folder and says which verb or command changes it instead. Reading would be refused there too, except for trees, since the trust store's `authority.key` is a private key.

**Answer:** A

## After your answers

Added 26 November 2026. What each answer changed. Still on `permissions-phase-6`, not committed.

- The build has no warnings. The whole suite has 961 tests (939 before, and 22 new). Five full runs of the finished work: three passed 961 of 961, and in two `Listen_takes_https_with_a_certificate` timed out; it passed in five runs on its own and with its class, and it failed in both runs on unchanged `main` before this phase (see "Found in passing"). No other test failed.
- On strawberry.local, the whole suite from a published copy: 916 of 961, and the 45 that fail are exactly those that fail with `main`'s own copy, for want of repository files. Every new test passes there, the walk up the folders included. The copies are deleted; the installed daemon wasn't touched.
- **Q308 (A), Q310, Q311.** As built.
- **Q309 (developer mode runs with your rights).** An unsigned pipeline from a URL on this machine in developer mode gets the owner's credential, "it's on this machine, and developer mode is on". `SourceTests` checks it saves a file.
- **Q312 (keep it strict).** As built. The troubleshooting guide's section on an unreadable store now says it's deliberate, and why.
- **Q313 (public and private scopes, now).**
  - `meep.network.send:public` and `:private`, with a port after them if wanted, are matched against the kind of address connected to, never a name, even a host called `public`. `NetworkReach` and `NetworkAddresses.ReachOf` in the SDK say which kind an address is: private is loopback, 10/8, 172.16/12, 192.168/16, link-local, 100.64/10, 0/8, and IPv6's unique-local, link-local and site-local ranges. Public is any other unicast address. Multicast, reserved, and `0.0.0.0` and `::` are neither.
  - `ModuleContext.DemandEndpoint` now returns the reach a connection is limited to: `Any` when the host was allowed by name, or `Public` or `Private` when only a keyword scope allowed it. An address written as one is classed at once.
  - Fetch and Send.HTTP use an `HttpClient` for each reach, whose connection callback resolves the name as it connects and tries only addresses of that kind, so DNS can't swing it between the check and the connection; each reach has its own connection pool. Send.TCP connects the same way, and Send.WebSocket through an invoker with the same callback; their shared connections are keyed by reach too. Every redirect is checked again, with its own reach. A name that resolves only to the wrong kind is a `PermissionDenied`: "localhost is at 127.0.0.1, ::1, and the message may send only to public addresses there".
  - For a Listen, `meep.network.listen:private` allows binding only to a private address.
  - I made the shared range carriers use (100.64/10) private here, though Meep's sense of hosts it may tell about itself (`MeepHosts.IsPrivate`) counts it as not private (Q231). For a scope meant to keep requests away from your networks, Tailscale's machines are on the wrong side to call public.
- **Q314 and Q321 (A).** No `meep.files.*` check passes for a path in Meep's own folder, by its real form, outside its `trees` folder, whatever the message holds: "… is in Meep's own folder, …, which no meep.files permission reaches, except its trees folder". The folder is `PipelineOptions.MeepFolder`, which is `MEEP_HOME` or `~/.meep` unless set, and the daemon and `meep run` set it to the folder they use. Meep's own bootstrap and reload pipelines set it to none, since they read pipeline files wherever they are, including tests' temporary Meep folders. A tree in its own place needs no permission at all: Learn, Classify and Forget check only a tree that `file=` names.
- **Q315 (the attribute).** `[ChecksPermission(permission, on)]` in the SDK, read by the registry into `ModuleRegistration.CheckedPermissions` and the verb catalogue's `Checks`, and shown by `meep lsp`'s hover, as "Checks meep.files.write, on each file it writes." It's on Read, Save, Fetch's two inflections, Send.HTTP, TCP and WebSocket, the three Listens, Learn, Classify and Forget. It isn't on the graph's lock.
- **Q316 (a reserved authority).** As recorded under the question. A pipeline's own credential confers Meep's permissions as `pipeline`, so a definition naming authorities binds pipelines unless it names `pipeline` too. `Permission.PipelineAuthority` holds the name, and `Permission.IsTrustedAuthorityName` refuses it for authorities. `TrustFolder.AddAuthority`, the trust store's reader, `meep trust init` and `add-authority`, and `Authorise.Authority` all refuse it, the last also as a default name from a key's comment. `PermissionDefinitions` doesn't report it as missing from the store. The refusal says "… but only home may confer it, and not pipelines: add authority=pipeline to the definition to let them".
- **Q317 (yes).** A Listen with `https://` checks `meep.files.read` for its certificate and its key before loading them.
- **Q318 (yes).** A signed pipeline whose signer's key is revoked in the trust store (`revoked`, or a KRL by its hash) holds nothing:
  - at load, the verification holds nothing and notes why;
  - when a store reload revokes it later, every gate refuses: "This pipeline's own credential, publisher's, no longer counts: its key, …, is revoked."
  - `Revocations.WhyKeyRevoked(fingerprint)` is new.
- **Q319 (yes).** A running source is checked again when the trust store changes, through a new `TrustStoreSource.Changed` event, which a watcher's reload and a fixed store's `Reload` raise, and after every live change to the pipeline. What's checked again is what it needed to start (its gate) and everything its running instance checked the pipeline's own credential for, such as a Listen's address and certificate, which its context now remembers. One that no longer passes emits a `PermissionDenied`, the log says "… stopped, as the pipeline no longer holds …", and it stops without restarting. A test stops a running Listen by narrowing `meep.network.listen` in the store.
- **Q320 (C).** An unsigned file earns the owner's credential only if it's yours, nobody else can change it, and every folder above it, up to the root, is yours or root's with nobody else able to change it, as OpenSSH asks; a sticky folder may be anyone's. Every file the pipeline `#include`s from this machine is checked the same way when the pipeline is built (`Build`), and the first that fails withdraws the credential: "it includes …, and the folder … can be changed by others".

### Found in passing

- **`realpath` costs about 45 µs on this Mac**, so checking Meep's own folder for every file, which needs the file's real form, made a check of `meep.files.write` cost about 47 µs. `RealPaths.Quickly` keeps each folder's real form for a second and looks only at the last part (following it if it's a link), which brings it back to about 5 µs on the Mac and 11–12 µs on the Pi, against about 1.5 µs and 5 µs before. Save, with its own file work, is within noise on the Mac and about 4 µs slower on the Pi (55–61 µs a message, from 51–59). The cost is that the last part of a path isn't put in the disk's case, and a folder turned into a link is noticed within a second rather than at once.
- **`0.0.0.0` was briefly private.** A first version classed 0/8 as private, so `meep.network.listen:private` let a Listen bind to every network. The test caught it: every address at once is in neither reach.
- **A test sink with `@errors` doesn't see successes**, which cost me a confusing failure; the docs already say so.
- **Reconfiguring a running HTTP Listen to a new path** loads its certificate again without checking it, as the certificate can't change without a restart, which is checked.
- **The Listen on `https://` test** (`Listen_takes_https_with_a_certificate`) timed out in two of five full runs, but in none of the runs on its own or with its class. Its error is the test helper's own ten-second limit, which suggests stopping the pipeline at the end hung rather than the request, though I didn't confirm it: with diagnostics added it passed four full runs in a row. It failed twice on unchanged `main` too. I'd look at it on its own, with the server's shutdown under load.
