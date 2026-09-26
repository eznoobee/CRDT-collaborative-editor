# What this project found

`PROJECT_SPEC.md` §13 is a decision log with seventy-three entries, written in
the order things went wrong. That is the right shape for a record and the wrong
shape for a reader. This is the dozen with the widest reach, grouped by what
they are about rather than when they happened, each with the concrete failure
that produced it.

**The through-line:** almost every entry is a check that could not fail. Not a
missing test — a present, passing, plausible-looking test that would have gone
on passing if the thing it named had been deleted. The project's output is less
the editor than a catalogue of the ways verification lies.

Numbers in brackets are §13 entries; read those for the full account.

---

## I. Checks that cannot fail

### 1. A differential test is vacuous when one side is derived from the other [§13.42]

The conformance harness compares the C# and TypeScript cores and fails the
build if they disagree about where a character goes. That is the project's
central guarantee.

**It is also two implementations written by one author from one paper.** A
misreading produces two implementations that agree perfectly and are both
wrong — and the harness reports green, because agreement is all it can see.
The same shape appeared in the convergence tests, where the expected order was
computed by the code under test.

**The rule.** Ask what a differential test compares *to*. If both sides trace
back to one source — one author, one reading, one helper — it measures
consistency, not correctness, and something outside it has to supply the truth.
Here that is the paper, the mutation score, and a committed corpus nothing
regenerates.

### 2. Assert the effect, not that control reached the line [§13.15, §13.41, §13.44]

Three instances, years apart in project time, identical in shape:

- **A counter beside a write.** §10's acknowledgement counter read identically
  on a healthy instance and on one where the write had been deleted, because
  the counter was the statement *after* the write, not the write.
- **A test that calls the mechanism directly.** It proves the mechanism works.
  It says nothing about whether anything in the product calls it — and for §5's
  pending-set bound, nothing did, for nine phases.
- **`dev-cert.sh` printing "Wrote cert.pem and key.pem".** It printed that
  because control reached the `echo`. openssl had failed, the certificate did
  not exist, and one permission bit — a key left at 644 because the `chmod`
  never ran — was the only evidence.

**The rule.** A check on control flow passes whenever the code runs. Check the
state that should have changed: read the row back, read the file back, ask the
product rather than the helper.

### 3. A test that supplies the configuration proves the mechanism, not the product [§13.54]

Both cores had complete, correct, passing tests of §5's per-connection
pending-set bound. Both tests set the bound themselves before exercising it.

**The shipped client never set it.** The feature was off in production for nine
phases, and the coverage is precisely why nobody looked: the tests were good,
they were green, and they were testing a configuration that existed only inside
them.

**The rule.** If the test supplies the value, the test is the only thing that
supplies it. Let the product configure itself and assert the value the product
chose.

### 4. A green run proves the fault absent, not the repair working [§13.66]

A fix for an intermittent browser failure went green twice. The repair had
never executed — the fault simply did not occur, which is what green means most
of the time on an intermittent fault. Two runs of evidence that the fault was
*absent* were about to be written up as evidence that the repair *worked*.

The closing condition the register row carried — "closes when the suite has
survived CI" — could not tell those apart, and was satisfied by a run that
demonstrated neither.

**The rule.** When a fix targets something intermittent, ask what the next green
run will have proved. If the answer is "that it did not happen this time", the
fix is not yet testable, and no number of green runs will change that. Make the
repair reachable from a suite that can create the conditions on demand.

### 5. Seven tasks reported against a workflow that never ran [§13.20, §13.57, §13.60]

CI silently stopped verifying this repository **four times** — a duplicate YAML
key, a run cancelled by the concurrency group, a step left with a name and no
command, a workflow that produced runs with no jobs. Every one was found at
phase end by a preflight, long after the work it should have checked.

Each fix was written from the failure in front of it and caught that failure,
not its class. What finally worked was giving up on inspecting the workflow file
and checking the observable thing instead: **after every push, did a run with
jobs appear for this exact sha?** And per run, not totalled across them — the
repository has two workflows, and one outage left the other healthy, so every
broken commit reported "2 runs, 1 job" and a total that was not zero.

**The rule.** Check the effect at the moment it should happen, not the
configuration that ought to produce it.

---

## II. Checks that fail when nothing is wrong

### 6. "I do not know" is not "it is broken" [§13.67]

The push gate above reported `NO RUN AT ALL for this commit`. CI was running;
all fifteen jobs passed. The account had been renamed, `api.github.com` answered
301, and the helper collapsed every HTTP failure into an empty result — so a
301, a 403, a spent rate limit and a genuine absence of runs were one answer.

**This is worse than a missed detection, not better.** A gate that cries wolf is
read as noise, and the next time it says those words it will be right.

**The rule.** A check may report that a thing is broken, or that it does not
know. It may never report the first when it means the second. Separate exits,
and a message that names which.

### 7. A guard that refuses a legitimate state teaches people to work around it [§13.63]

The phase preflight refused a commit with two CI runs of one workflow, on the
grounds that a re-run supersedes. Opening a pull request legitimately produces
two runs from two events. The commit was fine.

A false refusal is not a safe failure: the workaround for it — trimming the run
list until the check passes — is indistinguishable from the tampering the guard
exists to prevent.

---

## III. Measuring

### 8. Choosing the statistic after seeing which one passes [§13.61]

A p95 was proposed for §8's target 1, where it would pass, and for target 4,
where the same change turned a pass into a miss. Each was defensible alone.
Together they were the shape of choosing the measure after seeing the result.

The instrument was fixed instead of the target: run until repeated runs agree
within a tolerance written down beforehand, then report. **Target 1 is a miss —
28.0 ms against 25 ms, in all five agreeing runs** — and is recorded as a miss
with its cause rather than retuned.

### 9. A delta on a global aggregate is not a measurement of your own change [§13.62]

`before`, do a thing, assert `after == before + 1` — four times, in four files,
against counters that other tests and background sweeps also move. It reads like
a controlled experiment and is not one; it is stable only while nothing else
happens to be running.

**The rule.** Scope the reading to what the test itself caused, or assert a
direction rather than an exact delta.

---

## IV. Judgement under uncertainty

### 10. Name the specific thing you are trusting; never widen the class [§13.29, §13.65]

Three certificates in this project could each have been made to "just work" with
one line — `ignoreHTTPSErrors`, disabling `RequireHttpsMetadata`, replacing the
system trust store. Each would have silently invalidated every later claim about
TLS. Instead: pin the key, name the certificate, add to the store.

**The second half took longer to learn.** A retry for a flaky browser suite was
scoped to the sign-in prologue — because the prologue was where the one failure
on record had landed. The next run failed four requests later. Naming the
specific *error* is necessary; naming a *span that exists for a reason* is the
other half, and a span chosen from the last stack trace will be rechosen after
the next one.

### 11. An explanation that fits every available detail can still be wrong [§13.65, §13.68, §13.70]

One formatting failure produced three consecutive explanations. Two were wrong,
and each was falsified by the following build:

1. *A Roslyn feature-band difference.* Killed by the same pinned SDK passing in
   CI and failing locally.
2. *A CRLF working tree.* Still unresolved — and the diagnostic proposed for it
   shipped in the same commit as the fix, so by the time it ran it could not
   distinguish "never was CRLF" from "was, and is now corrected". Register
   row 43.
3. *The build stopping early*, so fixing one file merely let the compiler reach
   the rest of the graph.

**The observation was right throughout**; the mechanism inferred from it was
wrong twice. The fact that separated them — CI and the reporter disagreeing
under an identical pinned SDK — was available before either wrong explanation
was written.

---

## V. What verification never reached

### 12. Every setup failure was a precondition nothing stated [§13.72, register row 44]

Getting one person from a fresh clone to a running editor produced five
failures. **None was a bug in the product.**

| failure | precondition nobody wrote down |
|---|---|
| `IDE0055` in the image | the working tree is LF, whatever the platform does on checkout |
| `dev-cert.sh` producing no certificate | openssl's arguments survive the shell running it |
| `EISDIR` on the certificate | the path in `.env` denotes a file that exists |
| `EACCES` on the CA bundle | the volume is writable by the user the container runs as |
| `Failed to fetch` at sign-in | the browser trusts the issuer's origin, separately from the app's |

All five were found by a person running it, and none by the apparatus — which
is otherwise the most thoroughly checked thing here. CI checks out on Linux,
generates its certificate from a harness rather than the script a human runs,
writes its own `.env` instead of copying `.env.example`, and hands the browser a
certificate pin on the command line. Each is reasonable. **Together they mean
the suites exercise a path no person takes.**

§13.27's walk was built for exactly this class and does not do it: it drives the
harness's stack, not the README's. The close-out declared the project finished
while that path had never been walked once.

---

## Two more, briefly

- **Writing the spec first put the same bug in both implementations** [§13.11].
  A specification written before either core existed was precise, wrong in one
  clause, and faithfully implemented twice. The conformance harness agreed.
- **The end-to-end suite was testing a build no user receives** [§13.26]. The
  published image contained a development build of React, so the suite
  exercised Strict Mode's double-invoked effects — and found a real
  single-use-code bug only because of it. The artefact nobody had asked what
  was inside.

---

## Where to go next

- [`PROJECT_SPEC.md`](../PROJECT_SPEC.md) — the contract. §13 is the full log;
  the register near the end of §13 lists every open item with its closing
  condition.
- [`docs/phase-9-report.md`](phase-9-report.md) — the close-out.
- [`docs/row-40-outcome.md`](row-40-outcome.md) — one flaky test, three wrong
  answers, in the order they arrived. The best single illustration of §13's
  method.
- [`docs/phase-8-measurements.md`](phase-8-measurements.md) — the four
  performance targets, with what was broken to make each number fail.
