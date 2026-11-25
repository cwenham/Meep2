# To do

Things to come back to, a line or two each, with where they came from.

## Permissions

- **Which grants count (Q276, permissions phase 2).** A recorded grant counts if any authority trusted for `meep-grant` signed it: this machine's own, and any added with `meep trust add-authority --grants`, such as another Meep's. The alternative is to count a recorded grant only if this machine signed it, with other machines' grants counting only where they're carried as messages. Kept as it is for now, 24 November 2026; decide once grants from other machines are in use (phases 5 and 7).
- **Update Synesthesia for the permissions work (Q294, permissions phase 4).** `docs/Design/synesthesia-handover.md` lists what changed in what the daemon sends: the new `denied` count, which moves three totals one place along, `permissions` on graph modules, and the new message and error types. Hand it to a session working in `~/Projects/Synesthesia`. Added 25 November 2026.
