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
# There are three guards, and the third is the ratchet: the score in
# mutation-floor.json is the floor, and any decrease fails regardless of the
# absolute number. Stryker's own 85% break threshold stays as a backstop, but it
# cannot see erosion — 85.44% clears an 85% bar after a 1.02-point drop, which is
# how Phase 2 gave back coverage with nothing going red.
#
# The ratchet is ENFORCED IN CI ONLY, and PROJECT_SPEC.md §13.7 says why: the
# score is stable on one machine and is not stable across machines. A mutant that
# survives here can time out on a slower runner, and a timeout counts as a
# detection, so the same commit scored 88.12% here and 88.89% on CI. Comparing
# exactly is right; comparing exactly against a number measured somewhere else is
# not. Locally the comparison is printed and explained; CI decides.
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

# The ratchet. The score is recomputed here rather than scraped from Stryker's
# output: detected mutants over everything that could have been detected. A
# mutant with no coverage counts against the score, because "no test reaches it"
# and "a test reaches it and does not notice" are the same gap.
detected = counts["Killed"] + counts["Timeout"]
denominator = detected + counts["Survived"] + counts["NoCoverage"]
if denominator == 0:
    print("\nNo mutants were tested, so there is no score to ratchet.")
    sys.exit(1)

score = round(detected / denominator * 100, 2)

with open(sys.argv[2], encoding="utf-8") as handle:
    floor = round(float(json.load(handle)["floor"]), 2)

print(f"mutation score: {score:.2f}%   floor: {floor:.2f}%")

# The score is deterministic for a given commit — the same status counts appear
# locally and on CI, timeouts included — so an exact comparison does not flake.
if score < floor:
    print(
        f"\nMutation score fell from {floor:.2f}% to {score:.2f}%.\n"
        "\nThat is a coverage regression, and it fails regardless of the absolute\n"
        "number: a fixed threshold only notices the last step of a slide. Either\n"
        "cover what this change added, or record an argued exception in\n"
        "PROJECT_SPEC.md 13.7 saying what was removed and why the coverage it\n"
        "provided is not worth keeping, and lower the floor in the same commit."
    )
    sys.exit(1)

if score > floor:
    print(
        f"\nMutation score rose from {floor:.2f}% to {score:.2f}%. Commit the new\n"
        "floor in the same change:\n"
        f'\n    "floor": {score:.2f},\n'
        "\nThis fails rather than warns because an unrecorded peak is how a ratchet\n"
        "silently stops ratcheting: the next erosion would be measured from a\n"
        "floor nobody updated."
    )
    sys.exit(1)
REPORT_CHECK
  ratchet=$?
  set -e
  if [[ "$ratchet" -ne 0 ]]; then
    if [[ "${CI:-}" == "true" || "${MUTATION_RATCHET:-}" == "enforce" ]]; then
      exit "$ratchet"
    fi

    echo
    echo "Not failing: the ratchet is enforced in CI, where the floor was measured."
    echo "A local score differing from the floor is expected — see §13.7. Run with"
    echo "MUTATION_RATCHET=enforce to fail here too."
  fi
fi

exit "$status"
