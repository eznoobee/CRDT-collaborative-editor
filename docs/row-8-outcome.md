# Row 8 — what happened

The list and the procedure are in `docs/row-8-diagnosis.md`, committed at
`83af432`, before any break existed. This file is the result.

## The draw

The break was selected from the commit that added the list, so the selection is
reproducible by anyone and could not be steered afterwards:

```
commit 83af43213b440dc4e55b35095eb3c9bc4d88f7dd
draw   int(0xf7dd) = 63453
break  63453 mod 8 = 5 → entry 6
```

**Entry 6: acknowledgements dropped rather than written, on `alpha` only.**
Predicted verdict: *partially diagnosable*.

Applied by compiling `alpha` from sabotaged source into a directory of its own —
the frontier write in `EditorHub.RecordAcknowledgementAsync` removed — while
`beta` ran a clean build. The break is not a switch in the product: a
fault-injection hook shipped so an exercise can use it is a permanent weakness
bought for convenience.

## The stack, and its ground truth

Two instances sharing one Postgres and one Redis, six clients each, every one of
§5's three report paths exercised: catch-up on join, the vector on each
submission, and an explicit acknowledgement standing in for the client timer.

The break worked. Queried after the fact, the document's replica rows are
**6 populated and 6 empty** — beta's six clients recorded what they held, and
alpha's six recorded nothing.

## The diagnosis, followed mechanically

| Step | Read | Answer | Where it sent me |
|---|---|---|---|
| 0 | who is answering | both up, 6 connections each | behavioural, not availability → step 1 |
| 1 | §8 target 1 | over 25 ms: **48 of 48 on both** | "both high → shared cause → **step 5**" |
| 5 | snapshots | 0 written, 0 age, both serving | shared-state, not instance-local |
| 6 | — | **the dashboards did not localise it** | |

**Last step reached: 5. The procedure did not find the break, and it was not
departed from.**

## Finding 1 — a chronic miss hijacks the reading order

Step 1 routed everything to step 5 because **every submission on both instances
was over 25 ms**, which is 7b.4's finding: §6's 50 ms batching window sits inside
the segment §8's 25 ms target measures, so that target is missed permanently and
everywhere.

The procedure's "both high → shared cause" branch is correct reasoning and gives
the wrong answer here, because the shared cause it correctly identifies is a
*pre-existing* condition rather than the incident. **A reading order premised on
the targets normally being met is the wrong reading order for a system with a
target that is never met.** Steps 2, 3 and 4 — where anything about this break
could have appeared — were never read.

That generalises past this exercise: a dashboard whose first discriminator is a
permanently-red signal has no first discriminator.

## Finding 2 — the acknowledgement counter counts the wrong thing

Read after the diagnosis was over, and labelled as such, view 4 held:

```
acknowledgements · via=catchup        6        6
acknowledgements · via=submit        48       48
acknowledgements · via=timer          6        6
```

**Identical on both instances, while alpha wrote nothing.**

So even if the procedure had reached step 4, the dashboards would have shown no
difference. The break was not partially diagnosable as predicted — **it was
completely invisible**, and for a reason the prediction did not anticipate.

The cause: `_metrics.Acknowledgements.Add(...)` sits after the frontier write in
the same method. 7b.3 placed it there deliberately, and the commit message said
why — *"after the write, not before: a count that moves for an acknowledgement
that failed to store reports a frontier advancing on evidence the database does
not have."* That reasoning covers a write that **throws**. It does nothing about
a write that **is not there**.

> **A counter adjacent to an effect is not a measurement of the effect.** It
> measures that control reached the line after it. §10's acknowledgement counter
> reports a healthy stability frontier while the frontier is frozen, which is the
> exact failure the counter was added to make visible — and it is Phase 7's own
> line arriving one level up: *the log is evidence, an acknowledgement is a
> claim.* The counter is a claim about a claim.

The instrument that would not have lied is one derived from **state** rather than
from control flow: the frontier's own position, or the count of replicas whose
acknowledged vector is empty. Neither exists. That is a live defect in §10's
metric surface, found by using it.

## Honest limits

- **This is weaker than a second person**, and nothing above changes that. The
  procedure was followed mechanically and not departed from, which is the most
  this arrangement can establish; it does not establish that a reader without my
  knowledge would have done the same.
- The **prediction was wrong** — "partially diagnosable" against an outcome of
  completely invisible. Wrong in the direction that matters, and it is recorded
  as wrong rather than reinterpreted.
- Two harness defects were found before the exercise could run at all, and both
  would have made it meaningless while looking like a result:
  1. `MetricsSnapshot` started its listener on first construction, which was the
     first scrape, so the first run's dashboards read **zero for every counter**.
     The test written to guard this passed, because it resolved the snapshot from
     the container before submitting — §13.41 inside a test written to guard
     §13.15.
  2. Both instances shared one log buffer, and `startApi` learns its address by
     scanning it, so `beta` was handed `alpha`'s URL. The first real run reported
     twelve connections on alpha and none on beta: **a two-instance stack that
     was one instance twice**, which is indistinguishable from an instance-local
     break at a glance.
