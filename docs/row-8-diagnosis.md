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
changes that, and the report does not claim otherwise. That sentence is stated
at exactly the strength the evidence supports and **is not to be upgraded by a
later phase report**: following a procedure mechanically establishes that the
reasoning did not fill in for the dashboards on this run. It does not establish
that a reader without the breaker's knowledge would have reached the same place.

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

**Step 1 — do the instances DIFFER on a §8 target?** Read view 1. Compare
`over 25 ms` between instances as a share of observations.

> **Revised after the first run (7b.5). The original branched on a threshold —
> "both high → shared cause → step 5" — and a permanently red signal then
> captures every diagnosis there will ever be.** §8's 25 ms target is missed on
> every submission everywhere (register row 5), so the first run routed straight
> past the three views where the break was visible, and would have done so for
> any break drawn, not just that one. **A step comparing two instances branches
> on whether they differ, not on whether either exceeds a threshold**: "both
> high" and "both high in the same way" are different readings and only the
> second is uninformative.

- **They differ** → the instance that is worse is the subject for a latency
  target. **The procedure stops here for *which target* and *which instance*,
  and cannot go further: no view breaks the segment into stages.** Record that.
- **They agree, and both are within target** → no latency target is being
  missed. Go to step 2.
- **They agree, and both are outside target** → this is a shared condition, and
  a shared condition that is the same on both instances is **not evidence about
  this incident** — it may be a standing miss that predates it. Note it and **go
  to step 2**. Do not treat it as the answer, and do not jump to step 5: a
  reading identical everywhere rules out nothing instance-local.

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

**Step 4 — is §5's machinery moving?** Read view 4 (counted from the rows)
before view 4b (counted at the hub). The order matters: 4 says whether anything
was written, 4b says only how many requests arrived.
- **`silent` non-zero and rising with traffic → the frontier is not being
  written.** Stop for *what*. This reading is identical on every instance by
  construction — one database — so it **cannot** say which instance, and the
  procedure records "detected, not localised" rather than guessing.
- `acknowledgements · timer` at zero in 4b while `submit` or `catchup` are
  non-zero → the client timer path, which is the known blind spot §13.32
  records.
- A 4b count present on one instance and absent on the other, with `silent`
  flat, is request routing rather than a fault.
- `elements collected` and `replicas retired` at zero on both while `silent` is
  zero → the frontier is being written and collection is still not running; go
  to step 5.
- A background-sweep count present on one instance and absent on the other is
  **suggestive only**: which instance wins a batch is arbitrary. Do not stop on
  that alone.

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
