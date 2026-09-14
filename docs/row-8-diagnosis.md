# Row 8 — the break list and the diagnosis procedure

**Written before any break was implemented and before the seed was drawn.** The
commit that adds this file contains no break and no selection. That ordering is
the whole mechanism, so it is worth being able to check: `git log` shows this
file landing before anything in the list exists.

## Why this document exists

§13.22 rewrote "dashboards exist" into "the dashboards alone say which target
broke and on which instance". 7b adds the part that makes it a constraint rather
than a gesture: **whoever breaks the target and whoever reads the dashboard are
the same agent here**, holding the code just changed and the reasoning about
where it would show.

A seed makes the selection mechanical without making the searcher ignorant of
the search space. Two things narrow the gap:

1. **The reading order is fixed in advance** (below), and followed mechanically
   rather than reasoned freshly. If the procedure reaches the right subsystem,
   the dashboards did the work. Departing from it means they did not, and the
   report says so.
2. **The list includes breaks expected to be undiagnosable.** A set of only
   diagnosable failures is a set constructed to pass. The entries marked
   *expected invisible* are what show the dashboards have edges.

**This remains weaker than a second person doing the diagnosis.** Nothing below
changes that, and the report does not claim otherwise.

## The stack

Two API instances, `alpha` and `beta`, sharing one Postgres and one Redis, each
serving `/metrics` on its own admin port. The break is applied to **`alpha`
only**. `beta` is the control.

## The break list

Eight entries. The prediction column is written now, before any of them exists,
so that a wrong prediction is visible as a wrong prediction rather than
retrofitted.

| # | Break, applied to `alpha` only | Predicted verdict |
|---|---|---|
| 1 | A 40 ms delay inside the broadcast stage | **diagnosable** — view 1 shows over-25 ms on `alpha` and not `beta` |
| 2 | `Backpressure:MaxOutboundBytes` set to its floor | **diagnosable** — view 3 shows drops on `alpha` |
| 3 | Tombstone collection disabled | **expected invisible** — the collector sweeps a shared database, so `beta` collects the same documents; "alpha's collector is off" and "beta won the batch" produce the same two numbers |
| 4 | `Snapshots:OperationsPerSnapshot` set to 0 | **expected invisible** — snapshot age is a property of the documents, not of the instance that did or did not write one, and `beta` still sweeps them |
| 5 | `RateLimits:CodePointsPerConnection` set to 1 | **diagnosable** — view 2 shows `rejected · rate_limited` on `alpha` |
| 6 | Acknowledgements dropped rather than written | **partially diagnosable** — view 4 splits by source and by instance, but clients are spread across both, so a low count on `alpha` is suggestive rather than conclusive |
| 7 | Origin lookup returns nothing, so every batch is refused | **diagnosable** — view 2 shows `rejected · unknown_origin` on `alpha` |
| 8 | A 40 ms delay inside the persist stage | **diagnosable as a target, NOT attributable to a stage** — view 1 will show `alpha` over target, and the dashboard has no per-stage breakdown at all; the traces do, and the dashboard does not expose them |

Three of the eight are expected to fail to localise. That is the point of
including them.

## The diagnosis procedure

Followed top to bottom. Each step says what its answer rules out. **Stop at the
first step that names a subsystem**, and record the step number reached — the
step number is the result, more than the conclusion is.

**Step 0 — who is answering.** Read view 0. If an instance is unreachable or its
connection count is zero while the other's is not, that instance is the subject
and no further view is needed for *which instance*. Otherwise both are serving
and the difference is behavioural, not availability.

**Step 1 — is a §8 target actually being missed?** Read view 1. Compare
`over 25 ms` between instances as a share of observations.
- Both low → no latency target is being missed; the reported violation is not
  this one. Go to step 2.
- One instance high → that instance is the subject for a latency target. **The
  procedure stops here for *which target* and *which instance*, and cannot go
  further: no view breaks the segment into stages.** Record that.
- Both high → the cause is shared (database, Redis, or the load itself) and not
  instance-local. Go to step 5.

**Step 2 — is work being refused?** Read view 2. Compare the `rejected · code`
rows between instances.
- A code non-zero on one instance and zero on the other → that code names the
  subsystem, and that instance is the subject. Stop.
- `resync_required` non-zero anywhere → that is data loss and outranks
  everything else in this list; stop and say so regardless of what else is set.
- Nothing refused → ingest is accepting; go to step 3.

**Step 3 — are clients being disconnected?** Read view 3. Backpressure drops on
one instance and not the other name the fan-out on that instance. Stop.
Otherwise go to step 4.

**Step 4 — is §5's machinery moving?** Read view 4.
- `acknowledgements · timer` at zero while `submit` or `catchup` are non-zero →
  the client timer path, which is the known blind spot §13.32 records.
- `elements collected` and `replicas retired` at zero on both while
  acknowledgements are non-zero → the frontier is not advancing.
- A count present on one instance and absent on the other is **suggestive only**:
  these are background sweeps over a shared database, and which instance wins a
  batch is arbitrary. Do not stop here on that basis alone; record it and go to
  step 5.

**Step 5 — is the document store keeping up?** Read view 5. Snapshot age rising
with `snapshots written` flat means the sweep is not running anywhere; both flat
and both instances serving means it is a shared-state problem, not an
instance-local one.

**Step 6 — the dashboards did not localise it.** Say so. Name the last step
reached and what it ruled out.

## What counts as success

Not "I found the break". The break is known to whoever applied it, and that is
the problem this procedure exists to work around. Success is:

- the step number reached, and
- whether the subsystem the procedure names is the one that was broken, and
- whether the procedure was departed from at any point.

A departure means the dashboards did not do the work — the reasoning did — and
that is recorded as a failure of this exercise even if the answer was right.
