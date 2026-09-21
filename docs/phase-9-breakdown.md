# Phase 9 — The close-out. Breakdown for approval.

The last phase. It finishes the register's remaining debts, takes or escalates
the four §8 decisions, and records what is deliberately not being built.

**Scope, as approved.** Row 39, row 36, row 37, the four §8 decisions, and rows
31 and 33. **Presence (row 14) is out of scope, not deferred** — it is a
feature, and this project's value is in correctness and verification. **Row 38 is
left as a measurement**, with its reversal condition, not as a fix.

---

## A correction that shapes two of these tasks

7b.8, 7b.10 and the Phase 7b report all state that rows 31 and 33 are *blocked
on a Docker daemon*. **That is wrong, and it is worth being precise about how.**

CI has Docker. The walk job and the §7 deployment-conformance job both bring up
`docker compose` on every push, and both were green in the preflight this
session ran. The whole of Phase 5b was built by pushing files that could not be
executed locally and reading what CI did with them.

What is true is that **this sandbox** has no Docker daemon. I reasoned from my
own inability to run something to the project's inability to have it, wrote that
into two register rows and a phase report, and it went unchallenged for three
tasks. §13.51 in 9.0 records the general form. Rows 31 and 33 are done the 5b
way, and the cost of that is budgeted in 9.6.

**The iteration cost, stated up front so "prohibitive" is a number and not a
feeling.** Measured on this branch: the CI workflow takes ~5m40s and the
mutation workflow ~8m40s; they run in parallel, so a full cycle is **about nine
minutes of wall time**. Pushes serialise — the concurrency group cancels an
in-flight run, so a second push before the first finishes destroys its result.
Budget is **ten CI iterations per row**, or about ninety minutes of waiting
each. If either row passes that without converging, it stops and comes back with
the iteration count and what each one changed, rather than absorbing the cost
silently.

**Sequencing, and 9.6 and 9.7 go on branches of their own.** CI's concurrency
group is `${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress:
true`, so two pushes to one ref cancel each other — overlapping 9.6 and 9.7 on
the same branch would spend the iteration budget on cancellations rather than
runs. Separate refs are separate groups. Each merges back to
`phase-4-react-client` once green. (The mutation workflow is keyed on `sha` with
`cancel-in-progress: false`, so it is unaffected either way.)

9.0 first. Then 9.6 and 9.7 begin on their own branches, because their CI loops
are the long pole, and 9.1 through 9.3 are the local work done while runs are in
flight — local work is unconstrained, only pushing is. 9.4 and 9.5 follow, and 9.8 closes.

---

## 9.0 — Spec amendments, and the gate that enforces them

**Done when.** §2 lists presence as a non-goal with its reason; register rows 14
and 38 say what was decided rather than naming a phase that will not happen;
rows 31 and 33 drop the "blocked" framing; §11 carries a Phase 9 row; §13.51 is
written; and `check-breakdown.sh` requires §12's questions 4 and 5, which it does
not today.

**Vacuity risk.** A spec amendment cannot fail a test, so the risk is the
opposite one: writing the words and changing nothing that would catch their
violation. Two of these are checkable and must be. The register gate built in
7b.12 must still pass with rows 14 and 38 reworded — if "out of scope" is not a
settled status the gate recognises, it will read them as open forever, and the
fix is to teach the gate the word rather than to phrase the row around it.
**And the gate change is itself the test of itself**: `check-breakdown.sh` gains
questions 4 and 5, and the malformed fixture must be extended to omit one of
them, or the new requirement is a line of code no run exercises.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — there is no runtime mechanism here and no principals. The nearest
analogue is a *reader* who consults §2 and not the register, which is why
presence is recorded in both.

**§12 Q2 — does anything invoke this, or only the test?** Applies to the gate
change. `check-breakdown.sh` runs in CI and the preflight already; the new
requirement must be proved by a fixture it rejects, not by this breakdown
happening to satisfy it.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — nothing here asserts that two parties agree.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Not applicable — nothing here has a lifecycle.

**§12 Q5 — does the party that must respect this limit know what it is?** Not
applicable — no limit is introduced.

---

## 9.1 — Row 39: a placement oracle the client's own suite can replay

**Done when.** `npm test` alone — no C# runner, no materialised corpus — fails
when the sibling tie-break is inverted, on evidence that did not come from the
TypeScript implementation.

7b.9 closed the specific hole by adding `elementId.test.ts`, which tests the
comparator directly. The general shape stands: everything else the two cores
must agree on byte for byte is checked only by `conformance.test.ts`, which the
default run excludes because it needs the C# runner first. A contributor can
break core behaviour and see 219 green.

The fix is a **committed fixture** the TypeScript suite replays unaided —
2.5's canonical-form fixtures are the precedent — holding a handful of traces
with their expected §9 normalised output.

**Vacuity risk.** **A fixture generated by the TypeScript implementation is that
implementation agreeing with itself** (§13.42), and it would pass with the
comparator inverted because the fixture would be inverted too. The fixture must
be produced by the C# runner, committed with its provenance, and regenerating it
must be a deliberate act — if `npm test` can rewrite its own expectations, the
oracle is gone. The check that this worked is not that the tests pass: it is
that `scripts/placement-probe.sh` reports the client suite going red with the
fixture present and the corpus absent.

**§12 Q1 — who is the legitimate user that never performs the action?** The
contributor who runs only `npm test`. They are the principal this row exists
for, and the one every previous arrangement left uncovered.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — a
fixture is data read by a test; nothing here is triggered rather than called.

**§12 Q3 — in this comparison, does each side decide for itself?** **Centrally.**
The fixture is the checking side and must not derive from the party being
checked. That is the whole of the vacuity risk above.

**§12 Q4.** Not applicable — no lifecycle.

**§12 Q5.** Not applicable — no limit.

---

## 9.2 — Row 36: the pending-set bound, set by the layer that owns it

**Done when.** The browser client sets a bound on its replica's pending set; a
burst past it produces a defined, observable recovery; and §9's rejection table
has an entry for it, which today it does not.

§5 requires a bounded pending set **per connection**. Both cores default it to
unbounded, deliberately and with a written reason — a replica is not a
connection — and both say *whoever attaches a replica to a network connection
sets this*. Nobody does. The server has no pending set by design, so the browser
client is the only such layer, and `DocumentSession` constructs
`new Replica(id)` and leaves it.

**The number and the recovery are two decisions, and only the second is
genuinely open.** The number follows §13.37 — the largest legitimate burst, which
row 27 already put at 2,000 operations for a peer's offline afternoon arriving
across a partition. The recovery does not exist yet: §5 says *reject, log and
close* for the connection layer, and §9's table has no entry. **This task
proposes the entry** — approved in principle, with the wording to be reviewed —
so the deliverable includes the row as it would read in §9's table, not merely
an implementation that behaves some way.

**Vacuity risk.** **A test that sets `maxPending` itself and then overflows it
tests the core, not the product** — and that test already exists, in
`causalReadiness.test.ts` on both sides, which is precisely why this gap survived
two phases with the core fully covered. The test must go through the product's
own wiring and fail if `DocumentSession` stops setting the bound. Second risk:
asserting the overflow is *refused* without asserting a legitimate burst is
*accepted* makes a bound of zero pass.

**§12 Q1 — who is the legitimate user that never performs the action?** The
client whose peer backlog is large but legitimate — §13.37's question applied to
this bound. A number chosen from the abuse case refuses somebody's offline
afternoon, which is the exact failure 6b.4 made with the rate limit.

**§12 Q2 — does anything invoke this, or only the test?** The bound is enforced
on the apply path, which the tests drive directly. Nothing here is on a timer.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — this asserts a refusal and an acceptance, with no second party.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
The bound is set in the constructor, so it is in force from the first operation
rather than from the first overflow. A test must show it set on a session nobody
has touched.

**§12 Q5 — does the party that must respect this limit know what it is?** The
client both sets and enforces it, so yes by construction — but the *peer* whose
burst is refused does not, which is why the recovery has to be defined rather
than left as a closed connection.

---

## 9.3 — Row 37: `PeriodicSnapshotTests` independent of its neighbours

**Done when.** The sweep assertion no longer depends on what other tests left in
the shared database, and reinstating a large un-snapshotted document does not
turn it red.

`SnapshotSweeper` ranks laggards globally and sweeps the top N, so a test
asserting that *its* document was swept is asserting that no other test left N
documents further behind. It went red once in 7b.7 and green on a re-run, which
is the signature.

**Vacuity risk.** **Raising `BatchSize` until the red goes away is tuning a
control into silence** (§13.37's second half) and is rejected here by name. The
repair is to make the assertion independent of the ranking — sweep scoped to a
document, or assert on the snapshot *given* the document was in the batch —
rather than to widen the batch until the ranking stops mattering. The check is
adversarial: fill the shared database with laggards larger than the test's own
and require it to stay green.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — this is a test-independence repair with no runtime principal.

**§12 Q2 — does anything invoke this, or only the test?** The sweep's own
trigger test stays as it is; this task must not weaken it while making the
per-document assertion independent.

**§12 Q3 — in this comparison, does each side decide for itself?** The file's
central comparison — load-through-snapshot against replay-from-zero — is two
genuinely different code paths over two tables and stays that way.

**§12 Q4.** The sweep is a hosted service and already has its trigger test; this
task must not disturb it.

**§12 Q5.** Not applicable — no limit.

---

## 9.4 — §8 target 1: adaptive flushing, measured against the same generator

**Done when.** Adaptive flushing exists behind configuration, is measured with
7b.4's paced generator at §8's scenario rate **and** under saturation, and the
p99 for both arrangements is reported with the segment boundary named.

The target: receive → broadcast p99 of 25 ms. Measured at **234 ms**.

**The options, with the rejected ones and why.**

| Option | Verdict |
|---|---|
| Move the target to ~250 ms | **Rejected as a first move.** §8's rule is that a missed target is a decision, not a retune, and no measurement has yet shown 25 ms to be unreachable — only that the current arrangement does not reach it. |
| Remove or shrink the 50 ms batching window | **Rejected as stated.** The window exists to amortise the log append; removing it trades a latency number for a throughput number nobody has measured, which is the same mistake in the other direction. |
| Move the segment boundary so the window sits outside it | **Rejected.** It makes the number smaller without making anything faster, and §13.38 is the entry about exactly this — four boundaries over one piece of work, spanning 45%. |
| **Adaptive flushing:** write immediately when no write is in flight; batch only while one is pending | **To be measured.** Low latency at low load, amortisation under load, and no change to either §8 number. |

Adaptive flushing may lose — under a sustained rate every write has one in
flight, so it degenerates to the current behaviour plus a branch. It is measured
rather than dismissed, and if it loses, the choice between moving the target and
changing the window is **genuinely the user's**, because both edit §8.

**The correctness argument, which comes before the measurement.** Adaptive
flushing changes *when* writes happen, and `server_seq`'s monotonic visibility
depends on there being one writer per document at a time under the advisory
lock. So "no write in flight" must be **per document**, never global. A global
flag fails in both directions: it would serialise unrelated documents for no
reason, and — the direction that matters — it would let a flush for one document
start while another document's write was pending, admitting **two concurrent
flushes for the same document** and breaking the invariant that makes
`server_seq` a total order clients can rely on. The task states why the
invariant still holds under the new scheduling, and carries **a test that fails
if two flushes for one document ever overlap**, rather than asserting the
ordering that such an overlap would corrupt — an overlap can produce correct
ordering by luck, and a test of the ordering would pass on the run where it did.

**Vacuity risk.** Three, and the first is the one that would produce a
plausible-looking wrong answer. **Measuring adaptive flushing only at low load
proves the trivial half** — it wins there by construction, because there is no
write in flight — so the claim "amortisation under load" must be measured at
saturation, where it is most likely to lose. And **a new number is not
comparable to 7b.4's unless it is taken at the same boundary with the same
generator** (§13.38); a re-measurement that quietly moved either would show an
improvement that is an artefact. The old arrangement is re-measured in the same
run rather than compared against a recorded figure. **And two points on the load
curve are not a curve.** Adaptive schemes commonly have a latency cliff where
they change mode — here, the rate at which a write is almost always already in
flight — and idle and saturation are precisely the two points that bracket that
cliff without showing it. The measurement sweeps the middle, and a cliff inside
§8's operating range is a result whether or not the endpoints look good.

**§12 Q1 — who is the legitimate user that never performs the action?** The
client that submits once and stops — under adaptive flushing its single write
has nothing in flight ahead of it, so it takes the immediate path, which is the
case the batching window currently penalises most and which no throughput
measurement sees.

**§12 Q2 — does anything invoke this, or only the test?** Central. Adaptive
flushing is triggered by a write *completing*, not by a timer, so a test must
show the flush happening with nobody calling it — and the degenerate failure is
a completion handler that never fires, leaving writes to the window as before
while the code reads as if it had been adopted.

**§12 Q3 — in this comparison, does each side decide for itself?** The
comparison is between two arrangements of the same system, not two parties, so
§13.42's shape does not arise. The comparability risk is §13.38's instead and is
covered above.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
The in-flight tracking must be live from the first submission, not created on
first contention — a lazily-constructed tracker reports "nothing in flight"
forever and makes every write take the immediate path, which would look like a
spectacular result.

**§12 Q5.** Not applicable — no limit is introduced.

---

## 9.5 — §8 targets 2 and 4: the decisions, taken or escalated

**Done when.** Each of the four §8 decisions has its options, its rejected
options with reasons, and either a decision with the evidence behind it or a
statement of what the user needs in order to take it.

**Target 2 — keystroke → render p99, missed, 54% never sent.** Two questions.
*Whether `drain` coalesces*: mine to decide and implement — the outbox holds
adjacent batches from one replica in sequence order, coalescing them is
mechanical, and the measurement says whether it helps. *Whether the UI shows a
backlog*: a §9 question about what a person sees when their own typing is
queued. I will propose and implement; it is flagged for review because it is a
product decision wearing an implementation's clothes.

**Target 4 — document load, p50 inside, tail outside.** *Whether the target is a
p50 or a max*: **genuinely the user's**, and their prior is on record before the
data — **neither**: a p50 hides the users who suffer, a max is dominated by
events unrelated to the design, and a p95 is the usual reading. I bring the
distribution. If its shape argues against p95 — a bimodal tail, say, where p95
falls in the gap between two populations and so describes nobody — I make that
case explicitly rather than quietly reporting the number that fits the prior.

**Target 1** arrives here from 9.4 with its measurement attached.

**Vacuity risk.** **A decision document is the easiest artefact in this project
to write vacuously** — options listed, one ticked, no evidence attached, and
nothing that would read differently if the opposite had been chosen. The guard
is that every decision names the measurement that supports it and what would
reverse it; a decision with no reversal condition is a preference, and §8's own
rule is that a miss is a decision rather than a retune.

**§12 Q1 — who is the legitimate user that never performs the action?** For the
backlog indicator: the user who never queues anything, and for whom the
indicator must be invisible rather than reassuringly empty.

**§12 Q2 — does anything invoke this, or only the test?** Coalescing happens in
the drain loop, which the controller's own tests drive; the indicator must
render from state the controller already exposes rather than from a flag the
test sets.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — the comparisons here are between measurements of one system.

**§12 Q4.** Not applicable — nothing new has a lifecycle.

**§12 Q5.** Not applicable — no limit.

---

## 9.6 — Row 31: the offline-window discard, in a browser, on CI

**Done when.** A CI job brings up a Compose stack configured with a short
`ReplicaRetirement__Retire`, a real browser holds a document, a tab closed past
the window observes §9's discard on reconnect, and a tab that stayed live does
not.

Done the 5b way: the files are written, the **predictions are written down
before the first push**, and CI executes them. Its own stack, because a
one-minute `T_retire` in the existing walk stack would retire tabs mid-walk —
so a compose override, a script, a vitest config and a CI job, following
`scripts/compose-suite.sh`'s existing separation.

**Vacuity risk.** Three. **A step that reconnects and sees a warning proves
nothing if the warning renders unconditionally** — the live tab is the control
and must show no warning in the same run. **A test that would pass with
`T_retire` at its default has not aged anything out**; the run must assert the
replica was actually retired server-side, or a slow CI runner and a working
discard are indistinguishable. And **the stack's short `T_retire` must not leak
into the artefact that ships** — it is an override for this job, asserted absent
from the default compose.

**§12 Q1 — who is the legitimate user that never performs the action?** The
person whose laptop was shut and who never reconnects inside the window. They
perform nothing observable, which is why the server-side retirement has to be
asserted rather than inferred from what the browser shows.

**§12 Q2 — does anything invoke this, or only the test?** Central. Retirement is
a background job; the test must not call it. The job's interval is configured
short and the run waits for it to fire on its own.

**§12 Q3 — in this comparison, does each side decide for itself?** The two tabs
are compared, and their states must come from their own connections rather than
from one being told what the other saw.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
The retirement job must be running in the deployed image from startup; a job
that starts on first request would never fire for a tab nobody is using, which
is the entire scenario.

**§12 Q5.** `T_retire` is a limit, and §13.37's question applies: the short value
is for this job only, and the shipped default stays at seven days.

---

## 9.7 — Row 33: the walk observes GC on the deployed stack

**Done when.** The walk drives a document into a collectable state through
ordinary use, the collector and truncator fire on their own schedules, and the
walk observes the effect **through the product's own API**.

7b.10 established that the obvious route is closed: §10's metric surface is
served on an admin port the proxy deliberately does not forward, and publishing
it would undo a §7 control in the artefact that ships. Of the two restatements,
this task takes the second — **the product-visible consequence**. After
collection and truncation, a first-open client's catch-up returns a *snapshot*
where it previously returned a *delta*, and that is visible over the ordinary
hub API to a black-box client.

**Vacuity risk.** The one the original row named: **a step that opens a
collected document and finds the text correct passes identically whether or not
anything was collected**, because §5 requires collection to be invisible — that
is §13.19 written on purpose. And one this task adds: **"catch-up returned a
snapshot" is also what happens when the delta exceeds `MaxDeltaOperations`**, so
the document must be small enough that the delta path would otherwise be taken,
asserted rather than assumed — otherwise the step passes on a document that was
never collected at all.

**§12 Q1 — who is the legitimate user that never performs the action?** The
client that arrives cold and asks for everything. It is the principal no unit
test models, because every test builds its clients from state it just created —
and it is the client that 7b.10's stranding defect actually broke.

**§12 Q2 — does anything invoke this, or only the test?** Central, and it is the
row's reason for existing. The collector and the truncator run on their own
schedules; the deployed stack's intervals are configured short for this job and
the walk waits rather than calling either.

**§12 Q3 — in this comparison, does each side decide for itself?** The
comparison is a catch-up answer's *shape* before and after, against one client,
so no second party is involved.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Both sweeps are hosted services, and the deployed image must run them from
startup. A sweep that began on first scrape would never fire in a walk that
scrapes nothing.

**§12 Q5.** `TombstoneCollection__Interval` and `LogTruncation__Interval` are
configured short for this job only; the shipped defaults are untouched and that
is asserted.

---

## 9.8 — Preflight, the phase report, and the close-out

**Done when.** `scripts/phase-preflight.sh` passes **against the commit that
contains the report** (§13.49), CI is green for that exact commit, and
`docs/phase-9-report.md` records what the project finished, what it deliberately
did not, and where the evidence for each lives.

The close-out is more than a phase report. It states the terminal condition:
no known case where the spec says one thing and the shipped product does
another; every remaining gap measured and recorded rather than merely listed;
and the §8 decisions either taken or named as the user's with what they need.

**Vacuity risk.** **A close-out report is the easiest document in the project to
make sound finished** — a list of green suites and a confident sentence. The
guard is that it names what is *not* done with the same specificity as what is:
presence, row 38, row 35, and any §8 decision left open, each with its reason
and its reversal condition. A report whose "remaining" section is shorter than
its "delivered" section, in a project whose main output has been findings,
should be read as suspicious.

Second risk, and 7b.11 walked into it: **the preflight must run on the commit
containing the report.** The first run verifies the tree before the report
exists, which is a different tree, and nothing in the document says so unless it
is made to.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — reporting has no runtime principals.

**§12 Q2 — does anything invoke this, or only the test?** The preflight is run
by this task, and §12 makes that structural rather than an intention (§13.43).

**§12 Q3 — in this comparison, does each side decide for itself?** The preflight
derives its expected job set from `.github/workflows/` rather than from the
status file, because a status file cannot be asked whether it is complete. The
register gate added in 7b.12 does the same for the register against the
breakdowns.

**§12 Q4.** Not applicable — nothing new has a lifecycle.

**§12 Q5.** Not applicable — no limit.
