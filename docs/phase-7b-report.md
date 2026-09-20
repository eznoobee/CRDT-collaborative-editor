# Phase 7b — the register, worked

Tasks 7b.0 through 7b.11. Branch `phase-4-react-client`, head
`684d4df6f1a366518ab35175d689c80a27fcf63a`.

Phase 7b had no new feature. Its subject was the findings register itself: the
rows earlier phases had opened and deferred, each of which had been written down
precisely because it could not be done honestly at the time. Working a backlog
of *known* debts is the arrangement least likely to produce surprises, which is
what makes the result worth stating first.

---

## The headline: the phase's largest defect was in shipped client code, and no test could have found it

Row 27 asked for one test per configured limit, performing the largest thing a
real user legitimately does. Thirteen limits, numbers taken from the use — a
pasted paragraph, a workshop of forty, a laptop shut over a long weekend — and
never from the configuration, because §13.37 is that a test computing its input
from `MaxDocumentBytes` passes at any setting of `MaxDocumentBytes`.

**It found a defect, and not in a configured number.**
`DocumentSession.edit()` encoded every operation from one change event into a
single batch. Typing produces one operation per change event and never
approached §7's cap of 256; pasting produces as many as the clipboard held, in
one event. So a pasted paragraph went out as one batch of 900 operations, the
server refused it `batch_too_large`, and §9's recovery for that code is `stop`.

**The first paragraph anybody pasted ended their session's sync** — text still
on screen, nothing saying otherwise. Two phases. Every test that existed typed.

That is §13.37's own sentence arriving from the other side: 6b.4 got the rate
limit's number right by reasoning about pasting, and nobody asked whether the
client could paste at all. It is now §12's fifth standing question — *does the
party that must respect this limit know what it is?* — which has an answer you
can grep for.

---

## What the phase found

Nine defects. Six were in code that already existed and passed its tests; three
were in work written earlier in this same phase.

| # | Found in | Defect |
|---|---|---|
| 1 | 7b.5 | `MetricsSnapshot` started its listener on first construction, which was the first scrape — every counter read zero in a service nobody scrapes until something is wrong. **The test written to guard it passed**, because it resolved the singleton and *then* submitted. |
| 2 | 7b.5 | The two-instance stack was one instance twice: both shared a log buffer the runner scans for an address, so `beta` got `alpha`'s URL. A break would have been diagnosed correctly for entirely the wrong reason. |
| 3 | 7b.6 | `oidc-client-ts` defaults its state store to `localStorage`. `pkce.ts` documented `sessionStorage`, with a reason, while shipping `localStorage` for two phases — an abandoned sign-in left a live PKCE verifier there until some later sign-in swept it. |
| 4 | 7b.7 | The client could not paste (above). |
| 5 | 7b.8 | The truncation sweep's queue was keyed on `last_collected_at`, which the collector stamps on every document it examines. The queue refilled itself every sweep; 121 sweeps reclaimed nothing from a document that had just been collected. |
| 6 | 7b.9 | TypeScript had no direct comparator test, and `npm test` — the command `AGENTS.md` tells a contributor to run — passed 213 of 213 with the sibling tie-break inverted. |
| 7 | 7b.10 | Log truncation stranded every first-open client: a hole in a dense sequence stops a replay, so the client buffered everything past it forever while reporting itself current. Written in 7b.8, found in 7b.10. |
| 8 | 7b.10 | My own untruncated-delta test was vacuous — its document had no snapshot, so it passed with the whole never-truncated fast path deleted. Found by sabotage. |
| 9 | 7b.7 | `PeriodicSnapshotTests` depends on what its neighbours left in the shared database. Surfaced, not fixed (row 37). |

**Five of the nine were found by sabotage or by a probe, not by writing or
running the test.** That ratio is the phase's main evidence for §12's practice,
and it is unchanged from Phase 7's.

---

## Register movement

Closed by this phase: **7** (7b.1), **6** (7b.2), **28, 32** (7b.3),
**24, 25, 26** (7b.6), **27** (7b.7), **29, 30** (7b.8), **34** (7b.9).
Measured: **5**. Exercised: **8**.
Still open: **31, 33** — see below.
Opened: **35, 36, 37, 38, 39**, and **33** restated.

**Rows 6 and 7 were closed by the work in 7b.1 and 7b.2 and still read as open
until this task.** `/health/ready` probes both dependencies and names the one
that failed; every metric §10 lists exists and moves; the correlation id is per
connection, with a test distinguishing that from per-request. All of it shipped
nine tasks ago and the register said otherwise, which is the register's own
failure mode — a row that stops tracking reality is worse than no row, because
the next phase plans around it. Found while writing this report, which is the
one task that reads every row.

Nine new entries in §13 — **13.40** through **13.48** — of which four are
techniques rather than observations: measure the factor rather than halving once
(13.45), sabotage one rule every party shares and count which suites notice
(13.47), enumerate every invariant the data participates in before removing it
(13.48), and the §12 questions that came out of 13.41, 13.44 and 13.45.

---

## §8's targets, reported as measured

Row 5, measured in 7b.4. **Reported here exactly as 7b.4 recorded them**; §8's
rule is that a missed target is a recorded miss and a decision, not a retune,
and nothing in 7b.5 through 7b.11 re-measured them.

| Target | Result | The decision it needs |
|---|---|---|
| 1 · receive → broadcast p99 | **missed**, 234 ms against 25 ms | §8's target or §8's 50 ms batching window — they contradict |
| 2 · keystroke → render p99 | **missed**; 54% never sent | whether `drain` coalesces, and whether the UI shows a backlog |
| 3 · 1,000 connections under 2 GB | **passed**, 285 MiB | none |
| 4 · document load 500 ms | p50 inside, tail outside | whether the target is a p50 or a max |

Targets 1 and 2 are one architectural finding: the batching window sits inside
both the measured segment and the client's serial send loop. Neither is code
doing what it was not meant to; both are consequences of decisions §8 records,
against a target §8 set before anything measured it. **Four decisions are owed
and none of them is mine to take.**

---

## Row 8's honest limit, unchanged

7b.5 broke a §8 target deliberately and followed the dashboards to it. Two
findings came out of that exercise and both were acted on. The limit stated then
stands, quoted rather than paraphrased:

> Still weaker than a second person. Fixing what the exercise found does not make
> the exercise stronger evidence about whether a reader without the breaker's
> knowledge would have reached the same place.

Nothing in 7b.6 through 7b.11 bears on it, and this report does not upgrade it.
Row 8 also still reads *detects but does not localise*: every instance reads the
same Postgres, so a state-derived gauge is identical everywhere by construction
and can say a thing is wrong without saying where.

---

## Rows 31 and 33 — open, and blocked rather than deferred

Both need a Docker daemon to run against the Compose stack. **This development
environment has none**, and `scripts/compose-suite.sh` already records why that
is not papered over: a gate that skips when its infrastructure is missing is a
check that cannot fail (§13.19), so the Compose suites are CI jobs rather than
preflight gates.

**Row 31 — the offline-window discard in a browser.** Needs its own stack: a
one-minute `T_retire` would retire tabs mid-walk in the existing one, so it
needs a separate compose override, script, vitest config and CI job — four files
that could not be executed here even once. `ReplicaRetirementOptions.Retire` has
no validation floor, so the configuration is feasible; that is the one thing
that could be checked without Docker, and it was. **Nothing was written**,
because four unrunnable files presented as done is the stub rule (§12).

**Row 33 — a walk step observing GC on the deployed stack.** Attempted in 7b.10,
and the attempt changed the row. Its blocker is no longer row 6, which delivered
in 7b.5; **the row's premise collides with §7**. Row 6's metric surface serves
`/metrics` on an admin port the proxy deliberately does not forward —
*"unreachable from outside the deployment by construction rather than by a rule
someone has to keep applying"*, and explicitly not "authenticate it instead". A
black-box walk cannot reach that port without publishing it, which would undo
the control in the artefact that ships. The row now carries two restatements,
one of which 7b.10 supplied: after collection and truncation, a first-open
client's catch-up returns a snapshot where it previously returned a delta, which
**is** visible over the ordinary hub API.

Asking that question is what found defect 7 above. That is the argument for the
walk as a design tool rather than a regression suite: its value is the
viewpoint — the client that arrives cold and asks for everything — not the
coverage.

---

## What the suites say

Every number below was produced on this commit, with the environment named.

| Suite | Result | Where |
|---|---|---|
| `Editor.Api.Tests` | 395 passed, 6 skipped | local, Postgres 16 on 5433 + Redis on 6399; and CI |
| `Crdt.Core.Tests` | 77 passed | local and CI |
| `Conformance` | implementations agree byte for byte | local and CI |
| `client` (`npm test`) | 219 passed | local and CI |
| interop | 10 passed | local and CI |
| e2e (browser) | 6 passed | local and CI |
| walk, deployment conformance | passed | **CI only** — no Docker daemon here |
| `Crdt.Core` mutation | passed the ratchet | **CI only** |

The six skipped tests in `Editor.Api.Tests` are the Testcontainers cases, which
skip without Docker locally and fail in CI if the daemon is missing — Phase 0's
arrangement, deliberately asymmetric.

**Measurements** are reported with their environment rather than as properties
of the system: §8's targets in `docs/phase-8-measurements.md`, limit headroom in
`docs/limit-headroom.md`, GC reclamation in `docs/gc-reclamation.md`, the
convergence audit in `docs/convergence-audit.md`, and row 8's exercise in
`docs/row-8-diagnosis.md` and `docs/row-8-outcome.md`. Three of those are
re-runnable as scripts — `limit-headroom.sh`, `placement-probe.sh`,
`dashboard.sh` — rather than numbers that were true once.

---

## The preflight

`scripts/phase-preflight.sh` was run against this head with the CI status for
this exact commit. The job table it verified:

| Workflow | Run | Jobs | Conclusion |
|---|---|---|---|
| CI | 35518900362 | 12 | success |
| Mutation | 35518900360 | 1 | success |

The expected job set is derived from `.github/workflows/` rather than taken from
the status file, because a status file cannot be asked whether it is complete.
Two runs exist for this commit and neither is superseded.

All eleven local gates passed on the same head: `workflows`, `format`,
`breakdown`, `sabotage`, `seeding`, `tests`, `client`, `conformance`, `interop`,
`e2e`, `mutation`.

    PREFLIGHT PASSED for 684d4df6f1a366518ab35175d689c80a27fcf63a.

**It was run twice, and the second run is the one that counts.** The first
passed on `146e3f9`, the head the phase's code landed on — but committing this
report moved the head, and a preflight that verified the commit before the
report is a preflight that never saw the report. The register edits closing rows
6 and 7 are in that commit too. So the whole thing was re-run against
`684d4df`: same two workflows, thirteen jobs, all eleven local gates. A
preflight whose subject is one commit behind the thing being reported is the
category of mistake it exists to catch.

**Two things the preflight got wrong about itself, both now fixed.**

Three local gates — `tests`, `interop`, `e2e` — failed on the first run for want
of `EDITOR_TEST_POSTGRES` and `EDITOR_TEST_REDIS`, reporting only "FAILED (rerun
it directly to see why)". That is **§13.23** — a harness that cannot explain its
own failure — where a missing variable and a real regression produce the
identical sentence. Fixed at the source: the variables are checked by name
before any gate runs, so an unconfigured run stops in a second rather than
twenty minutes, and a gate that does fail now prints its last twelve lines.

And the **register gate** is new here: `scripts/check-register.sh`, in the
preflight and in CI. Rows 6 and 7 read open for nine tasks after shipping, and
7b.10 spent real effort on row 33 being "blocked on row 6" when row 6 had landed
eight tasks earlier. Closing a row is a manual act at the moment of maximum
distraction, which §13.43 says will not hold — and it did not, twice in one
phase. The gate fails in both directions and is checked against the state it was
built for: reinstating rows 6 and 7 as they actually read makes it name both.
**§13.50.**

---

## What Phase 8 inherits

Rows **14** (presence), **35** through **39**, and rows **31** and **33** above.
The two that matter most are not the oldest:

- **Row 38.** 114 of 1220 elements in a normally edited document are tombstones
  rule 2 can never collect, and nothing bounds that fraction as a document ages.
  Row 29's measurement was asked about payloads and answered about positions:
  a placeholder costs about one byte, so this is a correctness question about
  splicing, not a storage question — and explicitly not to be attempted by
  relaxing rule 2, which 7.3 already settled.
- **Row 39.** The conformance corpus is the client's only placement oracle and
  is not in its default run. 7b.9 closed the specific hole for the comparator;
  the general shape stands, and the fix is a committed fixture the TypeScript
  suite can replay unaided.

Four §8 decisions are also owed, listed above.
