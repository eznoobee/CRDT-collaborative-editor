#!/usr/bin/env bash
# Every task in a phase breakdown carries §12's required fields.
#
# PROJECT_SPEC.md §12 has required a vacuity-risk statement per task since Phase
# 3b, and §12's three questions since Phase 7. Both were honoured by hand, and
# §13.43 is the entry explaining why that was never going to last: a rule that
# names a thing to be careful about depends on someone remembering to be careful
# at the moment they are most absorbed in something else.
#
# So this is a gate rather than a reminder. A breakdown missing a field is
# MALFORMED — it fails — rather than merely incomplete, which is a thing nobody
# notices.
#
#   ./scripts/check-breakdown.sh [file...]
#
# With no arguments it checks every docs/phase-*-breakdown.md.
set -euo pipefail

cd "$(dirname "$0")/.."

FIXTURE="tests/fixtures/breakdown/malformed-breakdown.md"

# The self-test, and it runs by default rather than on request.
#
# A gate that cannot reject anything is §13.19 one level up from the tests it
# polices, and checking that by hand once is precisely the kind of care-phrased
# rule §13.43 says will not survive a busy phase. So the no-argument run — the
# one the preflight and CI make — also runs this script against a deliberately
# malformed fixture and fails if that fixture PASSES.
if [[ $# -eq 0 ]]; then
  if [[ ! -f "$FIXTURE" ]]; then
    echo "BREAKDOWN CHECK FAILED: the malformed fixture $FIXTURE is missing."
    echo "Without it nothing proves this gate can reject anything (§13.19)."
    exit 1
  fi

  if "$0" "$FIXTURE" >/dev/null 2>&1; then
    echo "BREAKDOWN CHECK FAILED: the malformed fixture $FIXTURE was ACCEPTED."
    echo "This gate cannot reject a breakdown missing its §12 fields, so its"
    echo "passing says nothing about the breakdowns it just checked."
    exit 1
  fi
fi

python3 - "$@" <<'CHECK'
import glob, re, sys

REQUIRED = [
    ("Done when", r"\*\*Done when"),
    ("Vacuity risk", r"\*\*Vacuity risk"),
    ("§12 Q1", r"\*\*§12 Q1"),
    ("§12 Q2", r"\*\*§12 Q2"),
    ("§12 Q3", r"\*\*§12 Q3"),
]

# Questions 4 and 5 arrived in 7b.5 and 7b.7 and went unenforced until 9.0 —
# this gate exhibiting the drift it was built to prevent: §12 grew and the
# mechanism that checks §12 did not (§13.50). Adding a question to §12 now means
# adding it here, and the fixture must omit it.
LATER = [
    ("§12 Q4", r"\*\*§12 Q4"),
    ("§12 Q5", r"\*\*§12 Q5"),
]

# Breakdowns written before those questions existed are NOT retrofitted, and the
# exemption is a named list rather than a date comparison or a silent skip.
#
# Retrofitting would mean inventing eleven tasks' worth of answers after the work
# was done, and the value of the questions is entirely that they are asked
# beforehand — a back-filled answer makes this gate's green mean less, not more.
# Exempting silently would be the hole the gate exists to close. So the
# exemption is explicit, visible in the failure output when it applies, and
# cannot be joined by accident: a new breakdown is not on this list.
EXEMPT_FROM_LATER = {"7b"}

# A task heading: "## 7b.3 — Title". The em dash is required so that prose
# headings ("## The shape of this phase") are not mistaken for tasks.
TASK = re.compile(r"^##\s+(\d+b?\.\d+)\s+—\s+(.+)$")

# "Not applicable" is a real answer; "n/a" on its own is a box being ticked.
BARE_NA = re.compile(r"^\**(not applicable|n/?a)\**\s*\.?\s*$", re.IGNORECASE)

paths = sys.argv[1:] or sorted(glob.glob("docs/phase-*-breakdown.md"))
if not paths:
    print("FAILED: no breakdown files found.")
    print("A check that examines nothing passes trivially (§13.19).")
    sys.exit(1)

problems = []
checked = 0

for path in paths:
    with open(path, encoding="utf-8") as handle:
        lines = handle.read().split("\n")

    # Split into task sections. Everything before the first task heading is
    # prose and is not checked.
    sections, current = [], None
    for line in lines:
        match = TASK.match(line)
        if match:
            current = (path, match.group(1), match.group(2), [])
            sections.append(current)
        elif current is not None:
            current[3].append(line)

    if not sections:
        problems.append(f"{path}: no task sections found. A breakdown with no "
                        "tasks passes every field check vacuously.")
        continue

    for file_path, number, title, body in sections:
        checked += 1
        text = "\n".join(body)

        phase = ""
        if "phase-" in file_path:
            phase = file_path.split("phase-", 1)[1].split("-breakdown", 1)[0]

        for name, pattern in REQUIRED + ([] if phase in EXEMPT_FROM_LATER else LATER):
            found = re.search(pattern, text)
            if not found:
                problems.append(
                    f"{file_path}: task {number} ({title}) has no '{name}' field.")
                continue

            # The field must say something. A marker with nothing after it is
            # the same omission wearing the right heading.
            after = text[found.end():]
            answer = after.split("\n\n")[0]
            answer = re.sub(r"^[^\n]*?\*\*", "", answer, count=1).strip()

            if len(answer) < 12:
                problems.append(
                    f"{file_path}: task {number} ({title}) has an empty "
                    f"'{name}' field.")
            elif BARE_NA.match(answer):
                problems.append(
                    f"{file_path}: task {number} ({title}) answers '{name}' "
                    "with a bare 'not applicable'. Give the reason — an "
                    "unexplained n/a is a required field decaying into a box "
                    "to tick.")

if problems:
    print("BREAKDOWN CHECK FAILED\n")
    for problem in problems:
        print(f"  {problem}")
    print(f"\n{len(problems)} problem(s) across {len(paths)} file(s). See "
          "docs/breakdown-template.md and PROJECT_SPEC.md §12.")
    sys.exit(1)

print(f"    {checked} task(s) across {len(paths)} breakdown file(s): "
      "every §12 field present")
CHECK
