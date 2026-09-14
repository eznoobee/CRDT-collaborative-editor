# Phase N — Title. Breakdown for approval.

<!--
  THE REQUIRED FIELDS ARE CHECKED BY scripts/check-breakdown.sh.

  A breakdown missing them is malformed and fails the gate — it is not merely
  incomplete. That is deliberate, and §13.43 is why: the vacuity-risk statement
  and §12's three questions are exactly the things that get skipped when a phase
  runs long, because nothing mechanical forced them. Now something does.

  Every task section (a heading of the form `## <phase>.<n> — <title>`) must
  carry all five fields below. "Not applicable" is a legitimate answer to any of
  the three questions, but it must be followed by a reason — an unexplained n/a
  is how a required field decays back into a box to tick.
-->

## N.0 — Task title

**Done when.** The observable criterion. What is true afterwards that is not
true now, phrased so that someone else could check it without asking what was
meant.

**Vacuity risk.** How this task's test could pass whether or not the code is
right — written before the test exists (§12). If the verification needs
infrastructure that does not exist yet, say so here and mark the task *written,
not done*; it stays open until the task that supplies it lands.

**§12 Q1 — who is the legitimate user that never performs the action?** For a
mechanism keyed on an action, name the principal who does not take it and decide
deliberately whether they are covered (§13.32). Answer "Not applicable — …" with
the reason if there is no such mechanism.

**§12 Q2 — does anything invoke this, or only the test?** For anything driven by
a timer, a hosted service, or a background sweep, at least one test must
exercise it with nobody calling it (§13.41). Answer "Not applicable — …" with
the reason if nothing here is triggered rather than called.

**§12 Q3 — in this comparison, does each side decide for itself?** For any
assertion that two parties agree, check that neither side's answer was derived
from the other's (§13.42). Answer "Not applicable — …" with the reason if the
task asserts nothing of that shape.
