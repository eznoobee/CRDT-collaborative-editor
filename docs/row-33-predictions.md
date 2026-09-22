# Row 33, predicted before the first push

Same arrangement as row 31 (§13.51): CI has Docker, this sandbox does not, so
the predictions are written before the push and not edited afterwards.

## What I expect to work

1. **The overlay applies and the stack starts.** Five intervals and one
   threshold, all bound from configuration like row 31's three. Nothing
   validates a relationship between them at startup except retirement's
   `Heartbeat < Retire`, which holds.
2. **The before-probe gets a delta.** The document is about thirty operations
   against a `MaxDeltaOperations` of 2,000, and `truncated_through` is still
   zero, so `CatchUpReader` takes the delta path.

## What I expect to be wrong

3. **The after-probe may never see a snapshot, and the reason will be the
   frontier rather than the sweeps.** This is my main doubt. Collection needs
   every replica that touched the document to be retired or up to date, and each
   probe connects a *new* replica. If a probe's replica is not retired before the
   next sweep, the frontier never advances and nothing is collected — and the
   loop polls with a *new* subject each time, so a slow retirement means each
   attempt adds a replica faster than retirement removes them. **If this is what
   happens, the loop is the bug**: it should reuse one subject, or wait without
   connecting at all.
4. **Truncation may not be reached even after collection.** §6 truncates only
   past a snapshot, and the snapshot sweep needs the document to be due. With
   `OperationsPerSnapshot` at 8 it should be, but the sweep is ranked globally
   over a database this test shares with nothing — so unlike register row 37's
   case, the ranking should not bite. I expect this to be fine and note it
   because row 37 makes it the obvious thing to be wrong about.
5. **`buildDeletes` may throw.** It requires ten visible elements at the moment
   it is called, which requires the preceding insert batch to have been applied
   locally — through `applyCatchUp`, which the test does. If the broadcast
   arrives first the state is the same; if neither has arrived it throws with a
   message saying so, which is the outcome I want rather than a silent
   miscount.

## What would make me stop rather than iterate

6. If collection happens and catch-up still answers with a delta, that is a
   defect in `CatchUpReader.DeltaIsSafeAsync` — 7b.10's gate on
   `truncated_through` — and it is a finding, not a harness problem.
7. If the text differs across the transition, collection has damaged the
   document, which is the failure §5's four conditions exist to prevent. That
   stops everything else.
