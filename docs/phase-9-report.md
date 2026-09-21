# Phase 9 — the close-out

**§13.49: a phase report certifies the commit that contains it.** That commit's
hash cannot be written into the report before the report exists, so this follows
7b.11's arrangement — the report is committed, CI runs on that commit, the
preflight is run against *that* run, and the result is recorded in a short
addendum below. The addendum's own commit is then unverified, which is a known
one-line gap named here rather than hidden.

---

## The headline: CI had not run for five tasks, and the gate built to catch that said `ok`

7b.12 spliced a job into `.github/workflows/ci.yml` and took the `run:` line of
the step above it with the splice, leaving a step with a name and no command.
GitHub rejects that. **Every push from 7b.12 through 9.6 produced a run with
zero jobs**: no failing step, no log to open, and the only visible tell a run
list showing the workflow's path where its name should be. The mutation
workflow was untouched and kept running, so the account and the daemon were
fine, and the one signal that matters was silent.

`scripts/check-workflows.sh` exists for exactly this. It was written after
3b.1's duplicate `working-directory:` key cost seven tasks of unverified pushes,
and its first line says it "rejects a workflow file GitHub's parser would
reject". What it implemented was duplicate mapping keys — one member of that
class, and the member that had already happened. It printed `ok ci.yml: 15 jobs`
throughout: a number that sounds counted from something checked.

**Nothing shipped broken. What was lost was verification**, which is §13.49's
failure arriving through a different door — two phase reports would have
certified commits no job had examined.

The gate now checks the structure GitHub requires, and self-tests against both
failures kept as fixtures, refusing to run if either is accepted. §13.57 has the
rule: an incident gives a gate its class, not its check.

---

## What the phase was for, and what it did

| Scope as approved | Outcome |
|---|---|
| Row 39 — a placement oracle the client's default suite runs | **Closed (9.1).** The fixture existed already; the fix was a split. §13.52 |
| Row 36 — §5's pending-set bound, never set by the product | **Closed (9.2), and the bound had two halves.** §13.54 |
| Row 37 — `PeriodicSnapshotTests`' global-ranking dependency | **Closed (9.3).** The condition is now constructed, not waited for |
| §8 target 1 — adaptive flushing, measured | **Decided (9.4).** §8's batching rule amended; 234 ms → **28.0 ms**, re-measured until it repeated |
| §8 targets 2 and 4 — the decisions | **Decided (9.5), and 7b.4's explanation of target 2 was wrong.** §13.56 |
| Row 40 — row 31's suite is intermittent | **Opened**, rather than left as a sentence in this report |
| Row 31 — the offline-window discard in a browser | **Closed (9.6),** five CI iterations of ten |
| Row 33 — GC's product-visible effect on the deployed stack | **Closed (9.7),** four CI iterations of ten |
| Presence (row 14) | Out of scope in §2, as decided — not deferred |
| Row 38 | Left measured, with its reversal condition |

**§11's Phase 9 done-when asks that no case remain where the spec says one thing
and the shipped product does another.** Two were found and closed inside this
phase: §5's pending-set bound was never set by anything shipped, and its age
half had never been implemented at all. §8's batching sentence now describes
what the code does. I know of no remaining divergence; I do not claim to have
proved there is none, and the honest basis for the claim is §7's requirement
map, the register, and the suites listed below rather than an exhaustive sweep.

---

## The defect ratio, which is the argument for the apparatus

Twelve defects and findings, counted the way 7b's report counted them.

**Where they were.** Six were in code that had already passed its tests — the
pending-set bound and its missing age half, the metric collector reading another
host's gauge, the CI workflow, the placement probe's census, `LogTruncationTests`
waiting on something any sweeper could do. Four were in work written earlier in
the same phase: the age-path's catch-up budget, row 33's probe loop, an
ineffective sabotage, and a vacuity guard that could never pass. Two were in
recorded conclusions rather than code: 7b.4's cause for target 2, and target 4's
distribution being distorted by its sample count.

**How they were found.** Five came from sabotage, a probe, a discriminator or a
prediction rather than from writing or running a test: the probe's own census
(§13.53), the ineffective cascade sabotage, row 33's probe loop (found by writing
the prediction that named it), target 2's real cause (found by a discriminator),
and the age bound (found by asking "who sets this?" about a value rather than a
call). Three came from CI doing what it exists for, once it was running again.

**The one that generalises furthest is §13.54**: a test that supplies the
configuration is testing the mechanism, and whether anything in the product
supplies it is a different question that no amount of such tests asks. Both
cores had complete, correct tests of the pending-set bound. Both set the bound
themselves first. The feature was switched off in the shipped client for nine
phases and the coverage was the reason nobody looked.

---

## §8's four decisions

**Target 1 — adaptive flushing, taken.** §8 required a 25 ms p99 across a
segment it also required to contain a 50 ms wait; 7b.4 recorded that as a
contradiction in the specification, which it was. Adaptive flushing needed no
new mechanism — the consumer loop already drains, writes, and takes whatever
arrived meanwhile, so the window was what suppressed it. Measured across six
rates under both policies: p50 66.6 → 10.1 ms at §8's own scenario, the window
costing a flat 50 ms at every rate rather than a shrinking one, and becoming a
throughput ceiling past 16 batches a second per editor (630/s against 326/s). No
cliff, because there is no threshold to cross.

**Target 1 is still missed, and the measurement was fixed rather than the
statistic.** 9.4 reported 54.7 ms and could not support it: two runs of the same
code minutes apart gave 36.6 and 71.4 ms, a spread wider than the distance to
the threshold. The p95 was stable, and stating the target at p95 would have made
it pass — while the same phase recommended p95 for target 4, where it fails.
Two locally sound arguments that jointly amount to choosing each statistic after
seeing which one passes (§13.61).

So the p99 stays and the sample count changes: five runs of 10,000 samples, with
the agreement tolerance written into the source before the runs. The p99s came
back **25.5, 26.0, 28.0, 28.7, 29.9 ms** — a 16% spread against a 20% tolerance.
**Target 1 is missed at 28.0 ms against 25 ms, in every one of the five runs**,
which is a verdict that does not depend on which run is called typical.

**Target 2 — coalescing rejected, and 7b.4's diagnosis with it.** The prediction
was that a shorter round trip would raise the client's send rate; it fell, 275
of 1,000 against 457, while the latency of what arrived improved sharply. A
discriminator holding everything fixed but the background load delivered 1,000
of 1,000 at a p99 of 30 ms through the same serial `drain`. The send loop was
never the cap. The constraint is the writer's own browser applying about 150
operations a second while `Replica.text` walks the whole document on every
change. Recorded as missed with the cause corrected; the remaining work is
incremental rendering, which is a project and not a close-out task.

**The unsent-work line is an age, not a count.** §8's own report carried *the UI
says `live`, with no problem, the whole time*. The first version fired past eight
queued batches, which is wrong twice over: "edits" is ambiguous when one pasted
paragraph is hundreds of operations, and `DocumentSession` splits a paste at §7's
ingest cap, so eight batches is what a normal paste looks like while it drains.
An indicator that appears when nothing is wrong is one people learn to ignore.
The question is *is my work stuck*, which is about age — five seconds, against a
p50 round trip near ten milliseconds. The wording and the count in the sentence
are unchanged.

**Target 4 — p95 recommended, which makes it a miss.** Raised from 20 samples to
200, because a p95 over twenty has one observation above it. The numbers
improved with no code change — p50 432 → 309, p95 979 → 558 — because twenty
cold loads were substantially measuring warm-up. The deciles rise smoothly to
384 at p80 and then turn: one mode with a right tail, no second population for a
max to pick out and none for a p50 to hide. The shape does not argue against the
prior, so the recommendation follows it, and **adopting it turns 7b.4's
qualified pass into a miss by 12%** — the honest direction for a stricter
statistic to move a result.

---

## Rows 31 and 33, done the 5b way

Both were recorded as Docker-blocked in the Phase 7b report and both were
wrong (§13.51). Predictions were written before the first push and left
unedited; the scores are in `docs/row-31-outcome.md` and
`docs/row-33-outcome.md`.

**Nine CI iterations across the two, against a budget of twenty.** Row 31 took
five, row 33 four. Iteration 2 of each bought nothing except the knowledge that
a remote failure with no evidence costs a whole cycle — which is what made
iterations 3 to 5 productive. Of eleven predictions, six were wrong, and in both
rows the thing I was least sure of was fine while the thing copied from a
working suite was not.

Both suites are on one branch because row 33's stacks on row 31's — it needs the
harness parameterisation — so one run exercises both. Separate refs were asked
for to avoid a concurrency group cancelling in-flight runs within a ref, which
does not arise between two suites on the same ref.

---

## What the suites say

Locally on the commit containing this report: the .NET suite (403 tests), the
client suite (245), the conformance harness, `dotnet format`, and the register,
breakdown and workflow gates. **The CI result for this commit is in the addendum
below and is not asserted here**, because a report cannot state the outcome of a
run on the commit that contains it before that run exists.

**Row 31's suite is intermittent; 9.9 below says what the instrumentation
found.** It went green on the iteration that closed row 31 and failed on later
commits at earlier points, with the page reporting `Failed to fetch` while the
API was demonstrably serving. That was never a reason to call row 31 open again
— the run that closed it exercised the whole path and read §9's sentence off the
screen — but it is a reason not to claim a stable suite, and row 40 is still
open. The instability is in arranging the test rather than in anything §9
specifies; that much has held across every failure.

Two new CI jobs, each with its own stack and its own config, because only a
suite with a job of its own can be required by the preflight. The coverage gate
that enforces that was generalised with them: it derived the runner from a
ternary on the script name, which would have mapped a third suite onto an
existing job — a guard passing while the new suite did not run.

---

## What this leaves

- **§8 target 1's remaining 3 ms.** Missed at 28.0 ms against 25 ms, repeatably.
  The far tail is still unattributed — one run's maximum was 673 ms — and
  attributing it needs the generator off the box, which target 3's harness has
  and this one does not.
- **§8 target 2's remaining work.** Incremental rendering in the client. Named,
  measured, not started.
- **The unsent-work line's threshold and wording**, implemented and flagged.
- **Row 38**, measured with its reversal condition, deliberately not fixed.
- **Row 40**, still open: named twice, explained as far as the evidence reaches,
  and not closed on a reading that the next CI run had already falsified once.
- **Row 41**, opened by 9.9 below: a transient failure during `bootstrap` leaves
  the user on a dead page. Found in a CI log, not fixed here.
- **Presence**, out of scope in §2.

---

## 9.9 — row 40, named twice, and still open

Row 40 was the one row this phase opened, and it was opened with a closing
condition that forbade the easy exit: *the instrumentation names the failing
request and the cause is fixed or explained — not when a run happens to be
green.*

**It has named it twice, and the second naming corrected the first.**

`bdbf784`: `GET /me — net::ERR_NETWORK_CHANGED`, Chromium's error for the host's
network configuration changing underneath an in-flight request. That commit
isolates the intermittency about as cleanly as it can be isolated — **the same
job passed in one run of it and failed in another**. The reading taken was that
a runner brings up Docker's bridge network while the suite's own Compose stack
starts, and a repair followed, scoped to the sign-in prologue.

`7be8c7a`, the very next run: `POST /documents — net::ERR_NETWORK_CHANGED`, with
`GET /`, the bundle, `/callback`, `GET /me` and `GET /documents` all answered
200 first and nginx logging a `499` because the client had gone. **The stack had
been serving for a second.** The tidy explanation was wrong, and the repair
built on it never fired.

> **§13.65 is the finding, and it is worth more than the fix.** The error was
> named; the *span* was not. "The sign-in prologue" was never a line in this
> test's design — it was the place the one failure on record had happened.
> Scoping a repair to where the last failure landed looks principled, cites
> §13.29 correctly, and covers exactly one sample.

The repair now covers **the arrangement** — a fresh context, sign in, create the
document, reach `live`, type and watch the outbox drain — rebuilt at most three
times, on exactly that `errorText`. The boundary is `setOffline`, and it can be
defended without reference to which request failed last: before it the property
has not been exercised; after it, nothing retries for any reason.

**What survives of the explanation** is narrower and less satisfying than what
was written first. Both failures land within the first two seconds of the
browser's first navigation, and the API answers on either side of each. Why a
runner's network changes there is not visible from this repository, and the
record says so rather than keeping the better sentence.

**And the log answered a question nobody asked.** Both times the page said
`Failed to fetch` and stayed there. `bootstrap` wraps sign-in, the token
exchange, `GET /me` and the document open in one `try` and returns
`{ kind: 'failed', message }`; nothing retries, and the app offers no way back
but a manual reload. A user whose connection blips for one request gets a dead
page with a healthy API behind it. That is **row 41**, deliberately not fixed on
the close-out day — a retry inside the bootstrap sequence can loop on a genuine
401 or re-enter the PKCE exchange, and it needs a test that tells those apart
(§13.64).

**Row 40 is not closed.** The explanation on record before `7be8c7a` was
falsified by the next CI run, and a row closed on a reading with that record — in
a phase whose complaint about this suite was precisely green-red-green — would
be the thing the row exists to prevent. It closes when the rescoped suite has
survived CI, or it is written up honestly a third time.

`docs/row-40-outcome.md` carries both findings, the evidence for each, and what
would falsify what is left.

---

## Addendum — the preflight, on the commit containing this report

**`./scripts/phase-preflight.sh` PASSED for `82c92dede24cdd1c00676c64c0ebc83340d2d8a5`**,
which is the commit that contains this report (§13.49).

Two workflows for that exact commit, neither superseded:

- **CI**, 15 jobs, all `success` — the .NET build and test, cross-implementation
  conformance, the TypeScript core against a running server, client lint,
  typecheck and test, the application in a browser, the walk, §7 against the
  deployed stack, §9's offline-window discard in a browser, §5's collection seen
  through the product, the browser document-load metric, the secret scan, and the
  four §12 gates (sabotage, seeding, breakdown, register).
- **Mutation**, 1 job, `success` — `Crdt.Core`'s score against §13.7's ratchet.

All thirteen local gates green: workflows, format, breakdown, register,
sabotage, seeding, tests, client, conformance, interop, e2e, mutation.

**The named residue.** The commit adding this addendum is not itself certified by
the run above — that is unavoidable while a report lives in the tree it
describes, and 7b.11 chose the same arrangement. A known one-line gap is better
than an unknown one. Everything the report asserts about code, measurements and
register rows is in `82c92de` and was verified there.

**One qualification, stated because it would otherwise be read out of the green.**
Row 31's suite has been intermittent since it was written, and **row 40 is still
open**. 9.9 above says what the instrumentation found —
`net::ERR_NETWORK_CHANGED`, twice, on two different requests, both within the
first two seconds of the browser's first navigation and both with the API
answering on either side. The arrangement now rebuilds on exactly that error and
nothing else. **What this does not do is prove the suite stable**, and the first
version of this paragraph claimed more than that on an explanation the next CI
run falsified. A recurrence carrying a different `errorText` is a different
fault and will fail loudly, which is intended. The instrumentation will name it.
