# Row 33 — what happened, against what was predicted

Four CI iterations of a budgeted ten. `docs/row-33-predictions.md` was written
before the first push and is unedited.

## The predictions, scored

| # | Predicted | Outcome |
|---|---|---|
| 1 | The overlay applies, the stack starts | **Right.** |
| 2 | The before-probe gets a delta | **Right**, once the probes could connect at all. |
| 3 | The after-probe may never see a snapshot, because the probes' own replicas hold the frontier — my main doubt | **Wrong.** Spacing the probes past `T_retire` was enough, and retirement was never the blocker. |
| 4 | Truncation may not follow collection; noted because row 37 makes ranking the obvious suspect | **Wrong, and the right answer was one step earlier**: nothing was ever *collected*, so truncation had no candidates. |
| 5 | `buildDeletes` may throw if the document is not what the test thinks | **Wrong** — it did not throw, and that is the point. |

**The interesting miss is 5.** The helper checked that enough elements were
visible and they were. What it did not check — because it had no reason to know
— was that the elements it chose were ones §5 forbids collecting.

## The iterations

**1: `negotiate failed: 404`.** The probes are separate people and none had been
granted access. §7's authorization working against a test that never asked.

**2: eight probes over two hundred seconds, still a delta.** The assertion said
"collection or truncation did not happen" and nothing more, which cost a cycle.

**3: the assertion gained the stack's own logs**, and answered itself.

**4: green.** The truncation sweeper selects candidates on `last_reclaimable_at
IS NOT NULL`, and nothing ever became reclaimable. The collector was running on
this document every three seconds and correctly taking nothing: the test had
typed a chain and tombstoned the **first** ten characters, so every tombstone
still had a child, and §5's second collection condition forbids collecting
those. §13.58.

**It is register row 38 from the other side.** That row measures how many
tombstones rule 2 can never collect in a normally edited document — 114 of
1,220 — and this test had built a document where the answer was all of them. The
property was already in the register, described, and it still took three
iterations to recognise in a failure.

## What the row asked for, and what it got

§10's counters for collection and truncation are served on an admin port the
proxy deliberately does not forward, and publishing it to make a test possible
would undo a §7 control in the artefact that ships. So this observes what a
*client* can see, which 7b.10 identified: a first-open client is answered with a
delta while the log is whole, and with a snapshot once collection and truncation
have taken it away. Both halves are asserted, because either alone passes
against a server that always answers the same way — and the text is compared
across the transition, because a snapshot of a document collection has damaged
would satisfy everything else.
