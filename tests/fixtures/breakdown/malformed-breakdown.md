# Phase 99 — A breakdown that must be rejected.

<!--
  A FIXTURE, not a real breakdown. scripts/check-breakdown.sh is run against
  this file and must exit non-zero; a gate that cannot reject anything is
  §13.19 one level up from the tests it polices.

  Each task below omits something different, so the fixture exercises each
  failure mode rather than only the first one the checker happens to hit.
-->

## 99.1 — Missing the vacuity risk entirely

**Done when.** Something observable is true.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — there is no mechanism here, because this task does not exist.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — same
reason.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — same reason.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Not applicable — a reason is given here, so this field is well formed.

**§12 Q5 — does the party that must respect this limit know what it is?** Not
applicable — a reason is given here too.

## 99.2 — Answering a question with a bare not-applicable

**Done when.** Something observable is true.

**Vacuity risk.** This task could pass without the code being right, in a way
described at enough length to clear the emptiness check.

**§12 Q1 — who is the legitimate user that never performs the action?** n/a

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — a
reason is given here, so this field is well formed.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — a reason is given here too.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Not applicable — a reason is given here, so this field is well formed.

**§12 Q5 — does the party that must respect this limit know what it is?** Not
applicable — a reason is given here too.

## 99.3 — Answering with a longer bare not-applicable

**Done when.** Something observable is true.

**Vacuity risk.** This task could pass without the code being right, in a way
described at enough length to clear the emptiness check.

**§12 Q1 — who is the legitimate user that never performs the action?** Not applicable.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — a
reason is given here, so this field is well formed.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — a reason is given here too.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Not applicable — a reason is given here, so this field is well formed.

**§12 Q5 — does the party that must respect this limit know what it is?** Not
applicable — a reason is given here too.

## 99.4 — Missing question 5, which §12 gained in 7b.7

**Done when.** Something observable is true.

**Vacuity risk.** This task could pass without the code being right, in a way
described at enough length to clear the emptiness check.

**§12 Q1 — who is the legitimate user that never performs the action?** Not
applicable — a reason is given here.

**§12 Q2 — does anything invoke this, or only the test?** Not applicable — a
reason is given here.

**§12 Q3 — in this comparison, does each side decide for itself?** Not
applicable — a reason is given here.

**§12 Q4 — does this start when the host starts, or when someone first asks?**
Not applicable — a reason is given here.
