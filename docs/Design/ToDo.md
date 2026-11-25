# To do

Things to come back to, a line or two each, with where they came from.

## Permissions

- **Which grants count (Q276, permissions phase 2).** A recorded grant counts if any authority trusted for `meep-grant` signed it: this machine's own, and any added with `meep trust add-authority --grants`, such as another Meep's. The alternative is to count a recorded grant only if this machine signed it, with other machines' grants counting only where they're carried as messages. Kept as it is for now, 24 November 2026; decide once grants from other machines are in use (phases 5 and 7).
- **Update Synesthesia for the permissions work (Q294, permissions phase 4).** `docs/Design/synesthesia-handover.md` lists what changed in what the daemon sends: the new `denied` count, which moves three totals one place along, `permissions` on graph modules, and the new message and error types. Hand it to a session working in `~/Projects/Synesthesia`. Added 25 November 2026.
- **Consent to a grant (Q300, permissions phase 5).** `Authorise` grants to a credential without its holder taking part, so rights can be pushed onto someone's credential unasked, and onto pipelines that use a bearer credential from a file. An optional `consent=required` could need a passing verification of the subject's own credential among the message's ancestors, for a request-then-approve flow. Left for later, 25 November 2026.
- **Signing grants without a key file (Q304, permissions phase 5).** Grants are signed in the process, from `~/.meep/trust/authority.key`, which must be an unencrypted Ed25519 key. A key encrypted with a passphrase, held only in ssh-agent, or on a security key can't sign grants. Options to explore: signing through ssh-agent, which would need an agent running for the daemon; or a key the daemon unlocks once. Left for later, 25 November 2026.
