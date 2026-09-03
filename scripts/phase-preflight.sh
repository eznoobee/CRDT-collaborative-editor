#!/usr/bin/env bash
# Refuses to let a phase be reported complete while CI is red.
#
# PROJECT_SPEC.md §12. Phase 2.5's mutation gate was red for six consecutive
# pushes while six of seven jobs were green, and it went unnoticed because the
# report format had a slot for what was built and no slot for whether the build
# agreed. This is that slot, and it is a script rather than a checklist item
# because an intention gets skipped exactly when things are busy.
#
#   ./scripts/phase-preflight.sh ci-status.json [branch]
#
# The CI status is an INPUT, not something this script fetches. In the agent
# environment this repository is developed in, api.github.com answers 403 and
# GitHub is reachable only through tooling a shell script cannot call, so a
# self-fetching preflight is not possible here. Requiring the status as a file
# gets the same structural property: the check cannot be passed without having
# actually gone and looked, and the script refuses stale or partial data by
# verifying it against the commit that is really pushed.
#
# Expected shape, straight from the workflow-jobs query:
#
#   {
#     "sha": "0c60bb58850be1c5e6e026fd217c699dfd6674ec",
#     "status": "completed",
#     "jobs": [ { "name": "...", "conclusion": "success" }, ... ]
#   }
set -euo pipefail

cd "$(dirname "$0")/.."

status_file="${1:-}"
branch="${2:-$(git rev-parse --abbrev-ref HEAD)}"
remote="${PREFLIGHT_REMOTE:-origin}"

fail() {
  echo
  echo "PREFLIGHT FAILED: $1"
  echo "Do not report the phase complete (§12)."
  exit 1
}

if [[ -z "$status_file" ]]; then
  fail "no CI status file given. Query the jobs of the newest completed run on
this branch head, save them as JSON, and pass the path. Reporting a phase
without having looked is the failure this exists to prevent."
fi

[[ -f "$status_file" ]] || fail "$status_file does not exist"

echo "==> Preflight for $branch"

git fetch --quiet "$remote" "$branch" 2>/dev/null \
  || fail "cannot fetch $remote/$branch — is it pushed?"

local_head=$(git rev-parse HEAD)
remote_head=$(git rev-parse "$remote/$branch")

[[ "$local_head" == "$remote_head" ]] \
  || fail "local HEAD $local_head is not $remote/$branch $remote_head; CI has not seen this code"

[[ -z "$(git status --porcelain)" ]] \
  || fail "the working tree is dirty, so what CI ran is not what is here"

echo "    head $local_head is pushed and the tree is clean"

python3 - "$status_file" "$local_head" <<'CHECK_CI'
import json, sys

path, head = sys.argv[1], sys.argv[2]

try:
    with open(path, encoding="utf-8") as handle:
        report = json.load(handle)
except (OSError, ValueError) as error:
    print(f"\nPREFLIGHT FAILED: {path} is not readable JSON ({error}).")
    sys.exit(1)

def die(message):
    print(f"\nPREFLIGHT FAILED: {message}")
    print("Do not report the phase complete (§6, §12).")
    sys.exit(1)

sha = str(report.get("sha", ""))
if not sha:
    die("the status file names no commit, so it cannot be tied to this code")

# Short or full, but it must be THIS commit. A status file from the previous
# push is exactly the mistake that produced six silent red runs.
if not (head.startswith(sha) or sha.startswith(head)):
    die(f"the status file is for {sha}, not the pushed head {head}")

if report.get("status", "completed") != "completed":
    die("the run is not completed; a run in progress is not a pass")

jobs = report.get("jobs")
if not isinstance(jobs, list) or not jobs:
    die("the status file lists no jobs; a rollup alone is not enough")

bad = [j for j in jobs if j.get("conclusion") != "success"]
print(f"    {len(jobs)} jobs reported for {sha[:12]}")
for job in jobs:
    print(f"      {job.get('conclusion', '?'):>10}  {job.get('name', '?')}")

if bad:
    names = ", ".join(f"{j.get('name','?')} ({j.get('conclusion')})" for j in bad)
    die(f"{len(bad)} job(s) are not green: {names}")
CHECK_CI

echo "    CI is green for this exact commit"
echo
echo "==> Local gates"

failures=()

run_gate() {
  local name=$1
  shift
  echo "--- $name"
  if "$@" >/dev/null 2>&1; then
    echo "    ok"
  else
    echo "    FAILED (rerun it directly to see why)"
    failures+=("$name")
  fi
}

# First, because a workflow GitHub cannot parse means the job table above came
# from a run that executed nothing — and an empty run reports failure with no
# failing step to look at.
run_gate "workflows" ./scripts/check-workflows.sh
run_gate "format" dotnet format --verify-no-changes
run_gate "tests" ./scripts/run-tests.sh
run_gate "conformance" ./scripts/conformance.sh
run_gate "interop" ./scripts/interop.sh
run_gate "mutation" ./scripts/mutation.sh

if [[ ${#failures[@]} -gt 0 ]]; then
  fail "local gates failed: ${failures[*]}"
fi

echo
echo "PREFLIGHT PASSED for $local_head. The job table above goes in the report."
