#!/usr/bin/env bash
# Mutation testing for Crdt.Core (PROJECT_SPEC.md §5: >=85% mutation score,
# which replaced line coverage).
#
# The guard below is the point of this script. Stryker does not support
# Microsoft.Testing.Platform (stryker-net#3094): pointed at an xunit.v3 project
# without a VSTest adapter it discovers zero tests, reports nothing, and exits 0.
# That is a passing gate that measures nothing, which is worse than no gate. So
# a run that finds no tests fails here regardless of Stryker's exit code.
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

exit "$status"
