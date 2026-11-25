# Hand-over: what Synesthesia needs to know from Meep2's permissions work

Started 25 November 2026, for a session that updates Synesthesia (`~/Projects/Synesthesia`) to match Meep2. It lists what changed in what the daemon sends and accepts, phase by phase of `docs/Design/Chat/permissions.md`, so that session needn't read every phase's doc. Add to it as later phases change more.

## Phases 1–3: credentials, the trust store and `Verify`

- **A new verb, `Verify`**, with inflections Credential, Certificate and Permission, from the runtime's own assembly (`RuntimeModules.Assembly`) beside the core modules. `meep/verbs` and the daemon's verb catalogue list it; it emits `VerifiedMessage`.
- **New message types** a tap or a graph link may show: `Credential`, `Certificate`, `Permission`, `Grant` and `VerifiedMessage` (with `Success`, `Failures`, `Permissions`, `Notes`, `Proof` and `IsGenuine`). A `VerifiedMessage` can't be injected, nor anything descended from one; the daemon's inject makes only text and JSON, so nothing Synesthesia does today is affected.
- **The daemon reads `~/.meep/trust`** when it starts and logs `[trust]` lines about it. `meep trust` manages it; nothing in the API does yet.

## Phase 4: gates

- **Subscriptions: a new count, `denied`**, the last of each module's counts, after `refused`. So module counts are now `received, handled, emitted, passed, busy, queued, dropped, filtered, failed, restarts, expired, refused, denied`, and among a pipeline's or the host's totals, `sourced`, `bytesReceived` and `bytesSent` each move one place along (to 13, 14 and 15, counting from 0). `hello` gives the names in order; if Synesthesia reads any count by its position, it needs changing (Q294).
- **`GET /pipelines/{name}/graph` and `meep/graph`: `permissions` on each module**, a list of names such as `["ReadNotes", "meep.files.write:~/notes/**"]`, or absent when the module isn't gated. It's what a message needs to reach the module: its `@permission`s, then what its verb always requires. On a source, it's what the pipeline must hold for the source to start. A drawing can put a lock on such a module.
- **`ModuleStats` has `Denied`**, so the pipeline detail the daemon sends (`modules[].counts`) has `denied` too: messages refused at the module's gate, refusals by its own checks in code, and for a Listen with `signature=required`, requests it answered with 401.
- **A new error type, `PermissionDenied`**, with `Gate`, `Needed` and `Reasons`, which taps and unhandled-error logs will show. A good thing to make readable, such as listing the reasons one to a line.
- **New Meeplang:** the `@permission=Name` modifier, written more than once for several; `#permission` lines at the top of a pipeline; and `signature=required` on `Listen` for HTTP. Synesthesia's editor, if it highlights or completes Meeplang itself, should know them; `meep lsp` already does.
- **OpenTelemetry:** `meep.module.messages` has `meep.event = denied`, and a refusal adds a `meep.permission.denied` event to a span (phase 5 makes it a span of its own, Q298).
- **The daemon's unhandled-error log** has a `PermissionDenied` for each refusal nothing handled. Refusals never stop a pipeline.

## Still to come

- Phase 5: `Parse`, `Authorise` and `Revoke` verbs, refusal spans, and gates that need a kind of proof.
- Phase 7: tokens become credentials, Synesthesia presents a credential when it connects and signs its commands, and mutual TLS. That's the phase that changes how Synesthesia connects.
