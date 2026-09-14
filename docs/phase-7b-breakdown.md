# Phase 7b — Scale and observability. Approved breakdown.

Covers register rows **5, 6, 7, 8** and **24–34**, with row 33 behind row 6.

Required fields per §12, enforced by `scripts/check-breakdown.sh`. See
`docs/breakdown-template.md`.

---

## The shape of this phase, and why its scepticism is different

Phase 7's failure mode was corrupted documents. **7b's failure mode is learning
nothing** — and that changes what a vacuity risk looks like. In Phase 7 a
vacuous test hid a defect. In 7b a vacuous test produces *a number*, and a
number is more convincing than a green tick. §13.22 governs: "the dashboards
exist" was a criterion met by something that diagnoses nothing.

**The standing rule for every measurement task here: a measurement is not done
until something has been deliberately broken and the measurement said which
thing.** A number nobody can make move is not evidence.

**Ordering.** 7b.3 (rows 28 and 32) runs before 7b.4 (the load measurement),
because both change what is being measured. Measuring §8's targets against a
system where §6's snapshot never happens and §5's piggyback is not wired would
measure a configuration that will not exist — producing numbers that are wrong,
against which §8's targets would then be judged.

---

## 7b.0 — Spec amendments, and the breakdown gate

**Done when.** §8's four targets are written as thresholds with a stated
environment; §10's metric list is written as names that must exist; §13.22's
deliberate-break rule is in §12; row 8's diagnosis-procedure discipline is
specified; and `scripts/check-breakdown.sh` runs as a gate, with a fixture
proving it rejects a malformed breakdown.

**Vacuity risk.** A spec amendment cannot fail a test, so the risk is that it is
written to describe whatever gets built — thresholds retuned quietly to match a
measurement. Mitigation: the numbers are fixed here, before any measurement
exists to be embarrassed by them, and a later change is a spec edit with its own
justification. The gate has its own risk: a validator that examines nothing
passes trivially (§13.19), so it refuses a run that finds no breakdown files and
no task sections, and a deliberately malformed fixture must make it exit
non-zero.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — this task adds no mechanism keyed on a user action. The gate is
keyed on a file existing, and the principal who never writes a breakdown is
someone doing work small enough not to need one, which is out of scope by
construction.

**§12 Q2 — does anything invoke this, or only the test?** This is the task's
whole point. The check runs from `phase-preflight.sh` and from CI, both of which
run anyway; it is not a thing to remember. §13.43 is the entry that demanded
this form.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — the gate compares a document against a field list, not two parties
against each other.

---

## 7b.1 — Row 7: `/health/ready` probes Postgres and Redis

First because everything else depends on knowing the stack is actually up, and
because it is the cheapest place to establish the deliberate-break discipline
before the expensive tasks.

**Done when.** Readiness returns unhealthy **with the dependency named** when
Postgres is stopped, again when Redis is stopped, and healthy when both are up —
each asserted by stopping the real thing.

**Vacuity risk.** The register row's own words: an endpoint returning healthy
without checking anything passes every test that only runs against a working
stack. The test must stop a dependency. A mocked `IHealthCheck` returning
unhealthy proves the serialisation, not the probe — if the harness cannot stop a
real dependency, the task is *written, not done*.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — readiness is polled by an orchestrator rather than keyed on
anything a user does.

**§12 Q2 — does anything invoke this, or only the test?** The probe must be
mapped at the path the deployment actually polls. A health check registered and
not mapped is §13.41 exactly, so the Compose stack's own probe configuration is
part of what this task asserts.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — the assertion is a response against an expected status, not two
parties agreeing.

---

## 7b.2 — Row 6: §10 observability, the metric list and the correlation id

**Done when.** Every metric §10 names exists and **moves** under load; a
correlation id is attached per connection and appears on every log line that
connection causes; a trace spans receive→validate→persist→broadcast.

**Vacuity risk.** "The metric exists" is satisfied by a counter registered and
never incremented — §13.15's shape, hit twice already in this project. Every
metric needs a test that makes it move, and counters that can only be zero or
non-zero need the pair: a case where it moves and a case where it does not. A
second risk: a correlation id generated per *request* rather than per connection
looks identical in any single-request test, so the test must follow one
connection across several operations and assert the id is the same, then across
two connections and assert it differs.

**§12 Q1 — who is the legitimate user that never performs the action?** The
viewer, again, for the fourth time in this project. A metric keyed on submission
covers writers only; presence, catch-up and §5's acknowledgement timer must be
represented, or the dashboards will show a document being read as a document
nobody is using.

**§12 Q2 — does anything invoke this, or only the test?** Metrics emitted from a
background sweep (retirement, the collector, the membership sweep) must be
observed with nobody calling the sweep — the clock moves and the metric moves.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — metric assertions compare a reading against an expected movement,
with no second party.

This task unblocks **row 33** (7b.10).

---

## 7b.3 — Rows 28 and 32: the two things §5 and §6 specify and nothing does

Moved ahead of the load measurement, per the ordering note above.

**Done when.** §6's periodic snapshot is taken by the running server at the
configured interval, and §5's acknowledgement is piggybacked on submission via a
field on `OperationBatchMessage`.

**Vacuity risk.** Snapshotting every 500 operations is invisible to every test
that submits fewer than 500 — the test must cross the threshold. It must assert
a snapshot row appears *and* that loading through the snapshot path produces the
same document, which is §13.42 territory. The piggyback has its own: a field
added to the message and never read produces no failure anywhere, so the
assertion is that the frontier advances *without* the timer firing.

**§12 Q1 — who is the legitimate user that never performs the action?** The
piggyback is keyed on submission, so the user who never performs it is the
viewer — which is exactly why §5 requires the timer as well, and why row 32 is
an optimisation rather than a correctness fix. The snapshot is keyed on
operation count, so the document nobody writes to never snapshots; that is
correct and worth stating, because it means an idle document's load time is a
full replay.

**§12 Q2 — does anything invoke this, or only the test?** §6's policy is
currently implemented and never called — that *is* row 28. The test must show
the snapshot appearing from ordinary submission traffic, with nothing in the
test calling `SaveSnapshotAsync`.

**§12 Q3 — in this comparison, does each side decide for itself?** Yes, and it
must be checked: comparing a snapshot-loaded document against a replay-loaded
one is two parties, and if the replay is seeded from the snapshot the comparison
is vacuous. Both must be built from the log independently.

---

## 7b.4 — Row 5: §8's four performance targets, measured

p99 receive→broadcast, p99 keystroke→render, 1,000 connections per instance
under 2 GB, 500 ms document load.

**Done when.** Each number is reported **with the build that produced it and the
machine it ran on** (§8, §13.7), against a document in steady state — GC having
run, because a monotonically growing document is not what §8 describes.

**Vacuity risk.** Three. **Measuring the wrong segment** — 3b.1's lesson, where
a length measured on the payload rather than the frame showed two protocols as
identical; each target's endpoints must be named in the spec and asserted to be
the segment instrumented. **A load generator that is the bottleneck**, which
makes the number describe the harness; the generator's own utilisation is
reported beside the result. **A p99 over too few samples** — a p99 of 50
requests is the worst of 50, so the sample count is part of the result or the
percentile is decoration. Explicitly not a goal: making the numbers good. A
missed target is a recorded miss and a decision, not a retune.

**§12 Q1 — who is the legitimate user that never performs the action?** The
idle-but-connected viewer is the one who costs memory and produces no
throughput, and the 1,000-connections target is about exactly them. A load
profile of pure writers would measure the cheapest population and report it as
capacity.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — the
measurement is driven by the harness by definition; there is no triggered
mechanism whose invocation could be missing.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — measurements are compared against thresholds, not against another
party's answer.

---

## 7b.5 — Row 8: dashboards that diagnose a deliberately broken target

**Done when.** One §8 target is broken on purpose, on one instance of a
multi-instance stack, and **the dashboards alone say which target and which
instance** — the diagnosis reached by following a procedure written before the
break.

**Vacuity risk.** §13.22's own correction already resists the obvious cheat
("dashboards exist"). The remaining one is that whoever breaks the target and
whoever diagnoses it are the same agent, holding the code just changed and the
reasoning about where it would show. **A seed does not fix this** — it makes the
selection mechanical without making the searcher ignorant of the search space.
Two things make it a real constraint:

1. **The diagnosis procedure is written before the break**: given a target
   violation, the order in which the dashboards are read. It is then followed
   mechanically rather than reasoned freshly. If the procedure reaches the right
   subsystem, the dashboards did the work; if it is departed from, they did not,
   and the report says so.
2. **The seed picks from a list written before any break is implemented, and
   that list includes breaks expected to be undiagnosable.** A set of only
   diagnosable failures is a set constructed to pass. The known-invisible entry
   is what shows the dashboards have edges, and the honest report reads "the
   seed picked the one I expected to be invisible, and it was."

This remains **weaker than a second person**, and the report says so rather than
claiming the stronger result.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — the break is injected by the harness, not keyed on a user action.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — the
dashboards are read by a person following a procedure; nothing here is triggered
on a schedule.

**§12 Q3 — in this comparison, does each side decide for itself?** This is the
task's central problem in §13.42's own terms: the diagnosing party must not
derive its answer from the breaking party. The procedure-before-break and the
undiagnosable-entry are the two mechanisms that separate them as far as one
agent can be separated from itself.

Depends on 7b.2 and 7b.4.

---

## 7b.6 — Rows 24, 25, 26: the guard-audit debts

**Done when.** The redaction sentinel travels the document API as well as a hub
connection (24); the seeded-documents grep covers the C# harness, whose eleven
direct row writes are gone (25); `pkce.ts` and `tokenSource.ts` have unit
coverage (26).

**Vacuity risk.** Row 26 is the reason the row exists: `app.e2e.test.ts` signs
in for real and **would still sign in if the code challenge stopped being
sent**. A unit test asserting "a challenge was generated" has the same hole
unless it asserts the challenge is the one derived from the verifier that was
stored. Row 24's sentinel has the §13.19 shape if the sentinel value never
reaches a log line the test inspects.

**§12 Q1 — who is the legitimate user that never performs the action?** Row 24's
sentinel is keyed on traffic; the endpoints nobody exercises in tests are the
ones that leak. The audit must enumerate endpoints from the route table rather
than from the tests that exist — the same "derive the expected set from the
source" move the preflight makes.

**§12 Q2 — does anything invoke this, or only the test?** Row 25's grep must run
in CI, not on request, or it is a rule nobody applies — §13.43 again.

**§12 Q3 — in this comparison, does each side decide for itself?** Yes, and it
bites: row 26's PKCE tests compare a challenge against a verifier. If the test
derives the expected challenge with the same function the code uses, it proves
the function is deterministic and nothing else. The expected value comes from
the RFC's own test vector.

---

## 7b.7 — Row 27: a largest-legitimate-use test for every configured limit

Thirteen remaining values; Phase 7 took `T_retire` and the GC watermark.

**Done when.** Each configured limit has one test that performs the largest
thing a real user legitimately does and asserts it succeeds, with numbers taken
from the use and never from the configuration.

**Vacuity risk.** §13.37's whole point: a test that reads the limit from
configuration passes at any value, including a wrong one. The mechanical check —
**if the test would still pass after halving the configured number, it is
testing the wrong thing.**

**§12 Q1 — who is the legitimate user that never performs the action?** Central
here. Each limit is charged on an action, and the test must be phrased as the
action a person takes rather than as the counter being incremented — the pasted
document, the long session, the many open tabs.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable —
limits are enforced on the request path, which the tests exercise directly;
nothing here is triggered on a timer.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — these assert an operation succeeds, with no second party.

---

## 7b.8 — Rows 29, 30, 31: the three Phase 7 findings that need infrastructure

**Done when.** The log is truncated behind a collected snapshot and
`resync_required` is verified end to end (30); whether a placeholder tombstone's
payload can be dropped is decided on measurement against realistic edit traces
(29); the offline-window discard is observed in a browser against a walk stack
configured with a short `ReplicaRetirement__Retire` (31).

**Vacuity risk.** Row 29's is the sharp one: measuring reclamation on a corpus
of append-then-delete-the-end traces reproduces exactly the shape 7.3 found the
suite had been testing all along. The traces must include mid-document deletion,
or the measurement confirms a rate that does not occur in use. Row 30's is that
a test which truncates the log by hand proves the classifier, not the truncation
— the truncation must be the product's.

**Truncation is the second operation in this system that destroys data, so it
gets Phase 7's standard of care rather than 7b's.**

**§12 Q1 — who is the legitimate user that never performs the action?** The
offline client, as in 7.3 — the principal whose references truncation can
invalidate and who performs nothing observable while away. Row 31's principal is
the person whose laptop was shut, who never reconnects within the window.

**§12 Q2 — does anything invoke this, or only the test?** Truncation will be a
background sweep like the collector, so it needs the trigger test: nobody calls
it, the schedule fires, the log shrinks on its own.

**§12 Q3 — in this comparison, does each side decide for itself?** Yes. Row 29's
measurement compares reclamation between trace shapes rather than replicas, but
row 30's end-to-end test compares a resynced client against one that never
resynced, and that is §13.42's shape exactly — both must compose independently
and exchange.

---

## 7b.9 — Row 34: audit the convergence tests for §13.42's shape

**Done when.** Each of the twenty files carrying a convergence assertion has the
§13.42 question asked of it by hand — *what property is under test, and could
the checking party have obtained it from the party being checked* — and the
audit reports, per file, which independent decision each comparison rests on.

**Vacuity risk.** An audit reporting "all clear" is the least trustworthy
possible output, for the same reason a clean first run is suspicious. It is done
when it names the independent decision per comparison, not when it finds
nothing. One lead already: `ScaleOutTests`' rejoin case compares
`kept.Normalised` against a replica that adopted a server snapshot wholesale, so
what it proves is closer to "the snapshot round-trips" than "two replicas
independently agree" — found in the first spot-check, which is itself evidence
about the base rate.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — an audit of existing tests has no runtime mechanism and no
principals.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — the
audit is a one-off reading exercise, not a mechanism that must fire. Any
*repairs* it produces inherit the question from the tests they fix.

**§12 Q3 — in this comparison, does each side decide for itself?** This is
literally the audit's subject, applied to every convergence assertion in the
repository.

---

## 7b.10 — Row 33: the walk step observing GC on the deployed stack

Behind 7b.2, because §5 requires collection to be invisible through the product
— identical text, identical version vector, by design — so the collector's
counters are the only evidence there could be.

**Done when.** The walk drives a document into a collectable state against the
deployed stack and observes, through the metric surface 7b.2 builds, that the
collected count moved for that document.

**Vacuity risk.** The version to refuse: a step that opens a collected document
and finds the text correct passes identically whether or not anything was
collected. That is §13.19 written on purpose, and a vacuous walk step is worse
than none, because the walk's value is that its greenness means something.

**§12 Q1 — who is the legitimate user that never performs the action?** The walk
must drive collection the way a person would — typing and deleting — rather than
by poking an endpoint, or it proves a path no user takes.

**§12 Q2 — does anything invoke this, or only the test?** Central: the collector
runs on its own schedule, and the walk must observe it firing without the walk
calling it. The deployed stack's `TombstoneCollection__Interval` is configured
short for the walk rather than the sweep being invoked.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — the step compares a counter against zero, with no second party.

---

## 7b.11 — Preflight and phase report

**Done when.** `scripts/phase-preflight.sh` passes against the pushed head with
CI green for that exact commit, and the report is written with the preflight's
job table in it. Stop and report.

**Vacuity risk.** The report format having a slot for what was built and none
for whether the build agreed is the exact failure the preflight exists to
prevent (Phase 2.5, six red mutation runs unnoticed). The additional risk here
is this phase's own: a report full of numbers reads as more rigorous than it is,
so every number is reported with the environment that produced it and every
target that was missed is named as missed.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — reporting is not a mechanism with principals.

**§12 Q2 — does anything invoke this, or only the test?** The preflight is a
script run by the phase's own closing task; §12 makes it structural rather than
an intention, which is §13.43's form.

**§12 Q3 — in this comparison, does each side decide for itself?** The preflight
derives the expected job set from `.github/workflows/` rather than from the
status file — the status file cannot be asked whether it is complete. That is
this rule applied to CI reporting.
