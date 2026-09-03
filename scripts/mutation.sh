#!/usr/bin/env bash
# Mutation testing for Crdt.Core (PROJECT_SPEC.md §5: >=85% mutation score,
# which replaced line coverage).
#
# Stryker drives VSTest and does not support Microsoft.Testing.Platform
# (stryker-net#3094), which is why Crdt.Core.Tests runs on xunit v2 while the
# other test projects run on xunit.v3. See AGENTS.md and PROJECT_SPEC.md §13.7.
#
# The guards below are permanent. Before the migration a full run reported every
# one of 227 tested mutants as Survived — a score of 0.00% that the tool exited
# 0 on, while the same suite killed those mutations when they were injected by
# hand. A gate that cannot fail loudly is not a gate.
#
# There are three guards, and the third is the ratchet: mutation-floor.json lists
# WHICH mutants are known to go undetected, and a mutant appearing outside that
# list fails the build. Stryker's own 85% break threshold stays as a backstop,
# but it cannot see erosion — 85.44% clears an 85% bar after a 1.02-point drop,
# which is how Phase 2 gave back coverage with nothing going red.
#
# The ratchet keys on identity rather than on the score because the score moves
# with the hardware (PROJECT_SPEC.md §13.7). A timeout counts as a detection, so
# a slower runner detects mutants a faster one does not: commit 9ffe234 touched
# neither Crdt.Core nor its tests and the score still went 88.89% to 89.27%
# because CI timed out eight more mutants. A mutant flipping between Killed and
# Timeout never appears in the undetected list at all, so this check does not
# move with the runner — and the list is the union of what has been observed, so
# a fast machine surfacing what a slow one timed out is already accounted for.
set -euo pipefail

cd "$(dirname "$0")/.."

# Mutation runs execute the suite once per mutant; the full 10,000-case count
# would take hours and add nothing. The 10,000-case gate runs separately in CI.
export CRDT_PROPERTY_CASES="${CRDT_PROPERTY_CASES:-150}"

# Same reasoning for size (PROJECT_SPEC.md §13.10). Unbounded, the scale cases
# make the suite slow enough that mutants which merely slow it down are recorded
# as timed out — detected without anything having caught them — and the counts
# start depending on how fast the machine is. §13.7's ratchet compares the score
# exactly, which is only sound while the score is deterministic.
export CRDT_SCALE_ELEMENTS="${CRDT_SCALE_ELEMENTS:-2000}"

log=$(mktemp)
trap 'rm -f "$log"' EXIT

set +e
dotnet dotnet-stryker 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
set -e

if grep -qE "Number of tests found: 0|do not contain any test" "$log"; then
  echo
  echo "Stryker discovered no tests, so the mutation score is meaningless."
  echo "Check that tests/Crdt.Core.Tests still references xunit.runner.visualstudio"
  echo "and Microsoft.NET.Test.Sdk — Stryker drives VSTest, not the MTP runner."
  exit 1
fi

report=$(ls -dt StrykerOutput/*/reports/mutation-report.json 2>/dev/null | head -1 || true)
if [[ -n "$report" ]]; then
  set +e
  python3 - "$report" mutation-floor.json <<'REPORT_CHECK'
import collections, json, sys

with open(sys.argv[1], encoding="utf-8") as handle:
    report = json.load(handle)

counts = collections.Counter(
    mutant.get("status")
    for file in report.get("files", {}).values()
    for mutant in file.get("mutants", [])
)
tested = counts["Survived"] + counts["Killed"] + counts["Timeout"]
print(f"\nmutant statuses: {dict(counts)}")

# A suite that kills nothing at all is a broken harness, not a real score. This
# suite provably kills reversed sibling ordering and a broken right-origin
# tie-break when those are injected by hand, so a zero here means Stryker never
# observed the failures.
if tested > 0 and counts["Killed"] == 0:
    print(
        "\nEvery tested mutant survived. That is the signature of Stryker not "
        "running the tests, not of a suite that catches nothing — see "
        "PROJECT_SPEC.md 13.7 and stryker-net#3094."
    )
    sys.exit(1)

detected = counts["Killed"] + counts["Timeout"]
denominator = detected + counts["Survived"] + counts["NoCoverage"]
if denominator == 0:
    print("\nNo mutants were tested, so there is nothing to ratchet.")
    sys.exit(1)

# Reported, never gated. The score is a function of the hardware as much as of
# the suite; §13.7 records how that was found out.
print(f"mutation score: {detected / denominator * 100:.2f}%  (reported, not gated)")

def identify(path, source, mutant):
    """A key that survives edits elsewhere in the file.

    Keyed on line NUMBER, this ratchet was invalidated by any insertion above
    a mutant: adding 54 lines to Replica.cs re-flagged thirty known entries as
    erosion at once. That direction is merely noisy. The other one is not — a
    genuinely new undetected mutant landing on a line number a shifted entry
    used to occupy is absorbed as already-known, and the check stays green
    while coverage falls. So the key is the mutated source line's TEXT plus
    the mutator and its replacement, which changes when the code changes and
    not before.
    """
    start = mutant["location"]["start"]
    name = path.replace("\\", "/").rsplit("/", 1)[-1]
    lines = source.splitlines()
    index = start["line"] - 1
    text = " ".join(lines[index].split()) if 0 <= index < len(lines) else ""
    replacement = " ".join(str(mutant.get("replacement", "")).split())[:60]
    return f'{name}:{mutant["mutatorName"]}:{replacement}:{text}'

# Counted rather than a set: two mutants of the same shape on identical source
# lines are two gaps, and collapsing them would let one become covered while
# the other silently took its place in the floor.
undetected = collections.Counter(
    identify(path, file.get("source", ""), mutant)
    for path, file in report.get("files", {}).items()
    for mutant in file.get("mutants", [])
    if mutant.get("status") in ("Survived", "NoCoverage")
)

with open(sys.argv[2], encoding="utf-8") as handle:
    known = collections.Counter(json.load(handle)["undetected"])

appeared = sorted((undetected - known).elements())
gone = sorted((known - undetected).elements())

print(f"undetected: {sum(undetected.values())}   known: {sum(known.values())}")

if gone:
    # Not a failure: an entry can vanish because a test now kills it, or merely
    # because this machine timed it out. Only the first is worth acting on, and
    # the script cannot tell them apart, so it reports and leaves the judgement.
    print(f"\n{len(gone)} known entries were detected this run. If a test now covers")
    print("them, drop them from mutation-floor.json; if the runner just timed them")
    print("out, leave them (§13.7).")
    for entry in gone[:10]:
        print(f"    {entry}")

if appeared:
    print(f"\nCoverage eroded: {len(appeared)} mutant(s) went undetected that are not known:")
    for entry in appeared:
        print(f"    {entry}")
    print(
        "\nThis fails regardless of the score, which is the point: a percentage\n"
        "only notices the last step of a slide, and it moves with the runner.\n"
        "Either cover these, or add them to mutation-floor.json with an argued\n"
        "exception in PROJECT_SPEC.md §13.7 saying why the coverage is not worth\n"
        "keeping."
    )
    sys.exit(1)
REPORT_CHECK
  ratchet=$?
  set -e
  if [[ "$ratchet" -ne 0 ]]; then
    exit "$ratchet"
  fi
fi

exit "$status"
