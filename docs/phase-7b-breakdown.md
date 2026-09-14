# Phase 7b — Scale and observability. Breakdown for approval.

Per §12: every task states **how its test could pass meaninglessly**, written
before the test exists, and answers §12's three questions where they bite.

Covers register rows **5, 6, 7, 8** (the phase's own) and **24–34** (carried), with
row 33 behind row 6.

---

## The shape of this phase, and why its scepticism is different

Phase 7's failure mode was corrupted documents. **7b's failure mode is learning
nothing** — and that changes what a vacuity risk looks like here. In Phase 7 a
vacuous test hid a defect. In 7b a vacuous test produces *a number*, and a
number is far more convincing than a green tick. §13.22 is this phase's
governing entry: "the dashboards exist" was a criterion that could be met by
something that diagnoses nothing.

So the standing rule for every measurement task below: **a measurement is not
done until something has been deliberately broken and the measurement said
which thing.** A number nobody can make move is not evidence.

One ordering constraint, already recorded: **7b's load test could not be trusted
before Phase 7's GC existed**, because load against a system that never collects
measures a document growing monotonically, which is not the steady state §8
describes. That is now satisfied.

---

## 7b.0 — Spec amendments before code

Write §8's targets as *thresholds with a stated environment*, §10's metric list
as names that must exist, and the §13.22 rule above into §12. No code.

**Vacuity risk.** A spec amendment cannot fail a test, so the risk is that it is
written to describe whatever gets built. Mitigation: the thresholds and metric
names are fixed here, before any measurement exists to be embarrassed by them,
and any later change to a number is a spec edit with its own justification —
not a quiet retune.

**§12 Q1 (who never performs the action).** Not applicable; no mechanism.

---

## 7b.1 — Row 7: `/health/ready` probes Postgres and Redis

Small and first because everything else in this phase depends on knowing the
stack is actually up, and because it is the phase's cheapest chance to get the
§13.22 discipline into the habit before the expensive tasks.

**Done when** readiness returns unhealthy with the dependency named when
Postgres is stopped, and again when Redis is stopped, and healthy when both are
up — each asserted by stopping the real thing, not by injecting a fake.

**Vacuity risk, and it is the register row's own words:** an endpoint that
returns healthy without checking anything passes every test that only ever runs
against a working stack. The test must stop a dependency. If that cannot be
done in the harness, the task is not done — a mocked `IHealthCheck` returning
unhealthy proves the serialisation, not the probe.

**§12 Q2 (does anything invoke it).** The probe must be reachable at the path
the deployment's orchestrator actually uses; a health check registered but not
mapped is §13.41 exactly.

---

## 7b.2 — Row 6: §10 observability, the metric list and the correlation id

**Done when** every metric §10 names exists and moves under load, a correlation
id is attached per connection and appears on every log line that connection
causes, and a trace spans receive→validate→persist→broadcast.

**Vacuity risk (the big one).** "The metric exists" is satisfied by a counter
registered and never incremented — §13.15's shape, which this project has
already hit twice. Every metric needs a test that makes it *move*, and counters
that can only be zero or non-zero need the pair: a case where it moves and a
case where it does not.

**Second risk:** a correlation id that is generated per *request* rather than
per connection looks identical in any single-request test. The test has to
follow one connection across several operations and assert the id is the same,
and across two connections and assert it differs.

**§12 Q1.** A metric keyed on submission covers writers. The user who never
submits is the viewer — again. Presence, catch-up and the acknowledgement timer
must be represented, or the dashboards will show a document nobody is editing
as a document nobody is using.

This unblocks **row 33**.

---

## 7b.3 — Row 5: §8's four performance targets, measured

p99 receive→broadcast, p99 keystroke→render, 1,000 connections per instance
under 2 GB, 500 ms document load.

**Done when** each number is reported **with the build that produced it and the
machine it ran on** (§8, §13.7), against a document in steady state — meaning
GC has run, because a monotonically growing document is not what §8 describes.

**Vacuity risks, three:**

1. **Measuring the wrong segment.** 3b.1's lesson: a length measured on the
   payload rather than the frame showed two protocols as identical. Each
   target's endpoints must be named in the spec and asserted to be the segment
   actually instrumented.
2. **A load generator that is the bottleneck.** If the harness saturates before
   the server does, the number describes the harness. The test must show
   headroom — the generator's own utilisation reported beside the result.
3. **p99 over too few samples.** A p99 from 50 requests is the worst of 50.
   Sample count is part of the reported result or the percentile is decoration.

**Explicitly not a goal:** making the numbers good. If a target is missed, the
result is a recorded miss and a decision, not a retune of the target.

---

## 7b.4 — Row 8: dashboards that diagnose a deliberately broken target

**Done when** one §8 target is broken on purpose, on one instance of a
multi-instance stack, and **the dashboards alone say which target and which
instance** — with the person reading them not told in advance what was broken.

**Vacuity risk.** This is §13.22's own correction and the criterion is already
written to resist the obvious cheat ("dashboards exist"). The remaining cheat is
subtler: whoever breaks the target and whoever reads the dashboard being the
same person, who then recognises what they already know. If that cannot be
separated, say so in the report rather than claiming the stronger result.

Depends on 7b.2 and 7b.3.

---

## 7b.5 — Rows 28 and 32: the two things §5 and §6 specify and nothing does

**Row 28 — §6's periodic snapshot is never taken.** `SnapshotPolicy` and
`SaveSnapshotAsync` are implemented, correct, and called only from tests. Wire
it up.

**Row 32 — §5's acknowledgement piggybacked on submission.** Needs a field on
`OperationBatchMessage`, which is a wire change on the hot path (§13.13a).

Together here because both change what the load test measures, so they land
**before 7b.3** if 7b.3 is to measure the system as specified rather than the
system as it drifted. *(Ordering note: this is the one place where the numbered
sequence above is wrong — 7b.5 should run before 7b.3. Stated rather than
silently renumbered, so the dependency is visible.)*

**Vacuity risk.** Snapshotting every 500 operations is invisible to every test
that submits fewer than 500. The test must cross the threshold, and must assert
a snapshot row appears *and* that a load through the snapshot path produces the
same document — which is §13.42 territory, so the two sides must decide
independently.

---

## 7b.6 — Rows 24, 25, 26: the guard-audit debts

- **Row 24:** the redaction sentinel driven through the document API, not just a
  hub connection.
- **Row 25:** the seeded-documents grep extended to the C# harness, which still
  writes document rows directly in eleven call sites.
- **Row 26:** unit coverage for `pkce.ts` and `tokenSource.ts`, which today rest
  entirely on the browser walk.

**Vacuity risk for row 26 specifically**, and it is the reason the row exists:
`app.e2e.test.ts` signs in for real and **would still sign in if the code
challenge stopped being sent**. A unit test that asserts a challenge was
generated has the same hole unless it asserts the challenge is the one derived
from the verifier that was stored.

**§12 Q3 (does each side decide independently).** Row 26's PKCE tests compare a
challenge against a verifier. If the test derives the expected challenge with
the same function the code uses, it proves the function is deterministic. The
expected value must come from the RFC's own test vector.

---

## 7b.7 — Row 27: a largest-legitimate-use test for every configured limit

Thirteen remaining values (Phase 7 took `T_retire` and the GC watermark). Each
gets **one test that performs the largest thing a real user legitimately does
and asserts it succeeds** — numbers taken from the use, never from the
configuration.

**Vacuity risk.** §13.37's whole point: a test that reads the limit from
configuration passes at any value, including a wrong one. If the test would
still pass after halving the configured number, it is testing the wrong thing.

---

## 7b.8 — Rows 29, 30, 31: the three Phase 7 findings that need infrastructure

- **Row 30:** `resync_required` end to end, which becomes reachable only once
  the log is truncated behind a collected snapshot. **Truncation is the work
  here**, and it is the second operation in this system that destroys data — so
  it gets Phase 7's standard of care, not 7b's.
- **Row 29:** whether a placeholder tombstone's payload can be dropped while its
  position is kept, **decided on measurement against realistic edit traces**
  rather than on the trailing-run case.
- **Row 31:** the offline-window discard observed in a browser, needing the walk
  stack configured with a short `ReplicaRetirement__Retire` and a heartbeat
  under it.

**Vacuity risk for row 29.** Measuring reclamation on a corpus of
append-then-delete-the-end traces reproduces exactly the shape 7.3 found the
suite had been testing all along. The traces must include mid-document
deletion, or the measurement will confirm a rate that does not occur in use.

---

## 7b.9 — Row 34: audit the convergence tests for §13.42's shape

Twenty files carry a convergence assertion. Each gets the §13.42 question asked
of it by hand: *what property is under test, and could the checking party have
obtained that property from the party being checked?*

One lead already identified: `ScaleOutTests`' rejoin case compares
`kept.Normalised` against a replica that adopted a server snapshot wholesale, so
what it proves is closer to "the snapshot round-trips" than to "two replicas
independently agree."

**Vacuity risk.** An audit that produces "all clear" is the least trustworthy
possible output, for the same reason a clean first run is suspicious. The audit
is done when it reports, per file, *which* independent decision each comparison
rests on — not when it reports that nothing was found.

---

## 7b.10 — Row 33: the walk step observing GC on the deployed stack

Behind 7b.2. It cannot be written before the metrics exist, because §5 requires
collection to be invisible through the product — identical text, identical
version vector, by design — so the collector's counters are the only evidence
there could be.

**Vacuity risk, and the reason this is last:** a step that opens a collected
document and finds the text correct passes identically whether or not anything
was collected. That is the version to refuse.

---

## 7b.11 — Preflight and phase report

`scripts/phase-preflight.sh`, then the report. Stop and report.

---

## Two things I want to flag before approval

**The ordering defect above is real.** 7b.5 (rows 28 and 32) should precede
7b.3 (the load measurement), because both change what is being measured. I have
left the numbering as written and said so rather than renumbering silently,
because the dependency is the interesting part; tell me if you would rather I
reorder.

**Row 8's separation-of-roles problem may be unsolvable here.** Whoever breaks
the target and whoever reads the dashboard are the same agent. I can make the
break selection mechanical — chosen by a seed, not disclosed in the task's own
notes until after the diagnosis is written — but that is a weaker guarantee than
a second person, and I would rather state its weakness now than discover it in
the report.
