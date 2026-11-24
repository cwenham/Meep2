# To do

Things to come back to, a line or two each, with where they came from.

## Permissions

- **Which grants count (Q276, permissions phase 2).** A recorded grant counts if any authority trusted for `meep-grant` signed it: this machine's own, and any added with `meep trust add-authority --grants`, such as another Meep's. The alternative is to count a recorded grant only if this machine signed it, with other machines' grants counting only where they're carried as messages. Kept as it is for now, 24 November 2026; decide once grants from other machines are in use (phases 5 and 7).
