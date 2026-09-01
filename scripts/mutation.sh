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
set -euo pipefail

cd "$(dirname "$0")/.."

# Mutation runs execute the suite once per mutant; the full 10,000-case count
# would take hours and add nothing. The 10,000-case gate runs separately in CI.
export CRDT_PROPERTY_CASES="${CRDT_PROPERTY_CASES:-150}"

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
  python3 - "$report" <<'PY'
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
        "PROJECT_SPEC.md §13.7 and stryker-net#3094."
    )
    sys.exit(1)
PY
fi

exit "$status"
