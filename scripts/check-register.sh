#!/usr/bin/env bash
# The findings register agrees with the phase breakdowns.
#
# PROJECT_SPEC.md §12, §13.43, §13.50. The register is the mechanism built to
# stop things being forgotten, and in Phase 7b it silently stopped reflecting
# what it tracks: rows 6 and 7 read open for NINE TASKS after the work shipped,
# and 7b.10 then spent real effort on row 33 being "blocked on row 6" when row 6
# had landed eight tasks earlier.
#
# Closing a row is a manual act performed at the moment of maximum distraction —
# the end of a task, with the next one already in mind — which §13.43 says will
# not hold. It did not, twice. So this is a gate.
#
# WHAT IT CHECKS, in both directions:
#
#   1. Every row a completed phase's breakdown claims is delivered must be
#      CLOSED in the register, or say in its own words that it is still open.
#   2. Every row the register says was closed by a task must name a task that
#      exists in a breakdown.
#
# Direction 1 is the one that caught nothing for nine tasks. Direction 2 is the
# cheaper mistake — a row closed against a task id that was renamed — and it is
# here because a register that points at nothing is the same failure arriving
# from the other side.
#
# A phase is COMPLETE when docs/phase-<name>-report.md exists. That is the same
# artefact §12 makes the phase's closing act, so the gate needs no list of
# finished phases to be kept in step with reality — which would be the very
# thing it exists to prevent.
#
#   ./scripts/check-register.sh [--self-test]
set -euo pipefail

cd "$(dirname "$0")/.."

# The self-test runs by default, not on request. A gate that cannot reject
# anything is §13.19 one level up from what it polices, and checking that by
# hand once is the care-phrased rule §13.43 says will not survive a busy phase.
if [[ "${1:-}" != "--fixture" ]]; then
    fixture_dir=$(mktemp -d)
    trap 'rm -rf -- "$fixture_dir"' EXIT

    mkdir -p "$fixture_dir/docs"
    cat > "$fixture_dir/docs/phase-zz-breakdown.md" <<'FIXTURE'
## zz.1 — Row 991: a row the register will leave open
Body.
FIXTURE
    : > "$fixture_dir/docs/phase-zz-report.md"
    cat > "$fixture_dir/spec.md" <<'FIXTURE'
| 991 | A row that shipped and was never closed | **zz** | Body | §5 |
FIXTURE

    if ./scripts/check-register.sh --fixture "$fixture_dir/spec.md" "$fixture_dir/docs" \
            > /dev/null 2>&1; then
        echo "SELF-TEST FAILED: the gate passed a register it should have rejected." >&2
        echo "It cannot fail, so its green says nothing (§13.19)." >&2
        exit 1
    fi
fi

spec=${2:-PROJECT_SPEC.md}
docs=${3:-docs}

python3 - "$spec" "$docs" <<'CHECK'
import glob, os, re, sys

spec_path, docs_dir = sys.argv[1], sys.argv[2]
spec = open(spec_path, encoding="utf-8").read()

# The register is ONE table, under its own heading, and the parser is scoped to
# it by that heading rather than by the shape of a row. Several other tables in
# this document start with a number — §11's phases, §13's decision log, the
# mutation scores — and a shape-based parser picked up §11's row 5 as register
# row 5 and reported a disagreement that did not exist. A structural anchor is
# the fix; guessing from the row is how the first version got it wrong.
start = spec.index("### The deferred register")
end = spec.find("\n### ", start + 1)
register = spec[start:end if end != -1 else len(spec)]

rows = {}
for line in register.splitlines():
    match = re.match(r"^\|\s*(\d+)\s*\|([^|]*)\|([^|]*)\|", line)
    if match:
        number, title, status = match.group(1), match.group(2), match.group(3)
        rows.setdefault(int(number), (title.strip(), status.strip()))

if not rows:
    print("FAILED: no register rows found — the parser and the table disagree.")
    sys.exit(1)

# A row is settled if its status says so. The words are the ones the register
# actually uses; a new one has to be added here deliberately, which is the
# point — "still open" must be a statement somebody wrote, not a default.
SETTLED = re.compile(
    r"CLOSED|MEASURED|EXERCISED|CORRECTED|CONFIRMED|WITHDRAWN", re.IGNORECASE)
DECLARED_OPEN = re.compile(
    r"still open|remains open|BLOCKED|blocked on|attempted", re.IGNORECASE)

problems = []

# Which phases are complete: a report exists for them.
complete = {
    re.match(r"phase-(.+)-report\.md", os.path.basename(p)).group(1)
    for p in glob.glob(os.path.join(docs_dir, "phase-*-report.md"))
}

# Direction 1: every row a completed phase's breakdown claims must be settled.
claimed = {}
for path in sorted(glob.glob(os.path.join(docs_dir, "phase-*-breakdown.md"))):
    phase = re.match(r"phase-(.+)-breakdown\.md", os.path.basename(path)).group(1)
    for heading in re.findall(r"^##\s+(\S+)\s+—\s+Rows?\s+([0-9,\s and]+)", 
                              open(path, encoding="utf-8").read(), re.MULTILINE):
        task, numbers = heading
        for number in re.findall(r"\d+", numbers):
            claimed.setdefault(int(number), []).append((phase, task))

for number, claims in sorted(claimed.items()):
    for phase, task in claims:
        if phase not in complete:
            continue
        if number not in rows:
            problems.append(
                f"task {task} delivers row {number}, which is not in the register")
            continue

        _, status = rows[number]
        if SETTLED.search(status) or DECLARED_OPEN.search(status):
            continue

        problems.append(
            f"row {number} reads open ({status[:60]}) but phase {phase} is "
            f"reported complete and its task {task} claims to deliver it — "
            f"close it, or say in the row why it is still open")

# Direction 2: a row closed by a task must name a task that exists — but only
# for phases whose breakdown is in the repository. Phases before 7b closed their
# rows against breakdowns that were never committed here, and failing on those
# would be the gate asserting the absence of a file rather than a disagreement.
# It checks what it can see and says how much that is.
tasks = set()
phases_with_breakdowns = set()
for path in glob.glob(os.path.join(docs_dir, "phase-*-breakdown.md")):
    phases_with_breakdowns.add(
        re.match(r"phase-(.+)-breakdown\.md", os.path.basename(path)).group(1))
    tasks.update(re.findall(r"^##\s+(\S+)\s+—", open(path, encoding="utf-8").read(),
                            re.MULTILINE))

def phase_of(task):
    # "7b.4" -> "7b", "7.1" -> "7". The phase is everything before the last dot.
    return task.rsplit(".", 1)[0]

unverifiable = 0
for number, (_, status) in sorted(rows.items()):
    for task in re.findall(r"(?:CLOSED|MEASURED|EXERCISED)\s*\((\d[0-9b.]*)\)", status):
        if phase_of(task) not in phases_with_breakdowns:
            unverifiable += 1
            continue
        if task not in tasks:
            problems.append(
                f"row {number} says it was settled by task {task}, and "
                f"{docs_dir}/phase-{phase_of(task)}-breakdown.md does not define it")

if problems:
    print("FAILED: the register and the phase breakdowns disagree.\n")
    for problem in problems:
        print(f"  - {problem}")
    print("\nThe register is what the next phase plans from (§13.50).")
    sys.exit(1)

print(f"    register agrees with the breakdowns ({len(rows)} rows, "
      f"{len(claimed)} claimed by a task, {unverifiable} closed against a "
      f"phase whose breakdown is not in the repository)")
CHECK
