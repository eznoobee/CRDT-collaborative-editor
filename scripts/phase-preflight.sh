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
# WHAT THIS GOT WRONG, and why the shape below is bigger than it was.
#
# The first version checked that every job it was GIVEN concluded "success".
# That part was right and is unchanged. What it never checked was whether it had
# been given all of them. Run 79 concluded `cancelled` — eight jobs green and
# the mutation job cancelled at 7m32s by the concurrency group — and a status
# file listing those eight jobs passes a check that only looks at the jobs in
# the file. The run's own conclusion was never read at all, because the script
# read `status`, and a cancelled run's status is "completed".
#
# So the expected set is now derived from the workflow files rather than taken
# from the report: a job that did not run is a job missing from the list, and
# absence is exactly what a status file assembled by hand cannot be trusted to
# show. Same class as the CI that was not running for seven tasks — checking the
# values present instead of the values required.
#
# It also refuses a run that has been superseded. A green run on a commit that
# is no longer head was already refused by the sha check; a green run that a
# NEWER run on the same commit has replaced was not, and a re-run exists
# precisely because someone doubted the first answer.
#
# Expected shape. Two queries per workflow — the run list for the commit, and
# the jobs of the run being reported:
#
#   {
#     "sha": "0c60bb58850be1c5e6e026fd217c699dfd6674ec",
#     "runs_for_sha": [
#       { "id": 34398694675, "workflow": "CI",
#         "status": "completed", "conclusion": "success" }
#     ],
#     "runs": [
#       { "id": 34398694675, "workflow": "CI",
#         "jobs": [ { "name": "...", "conclusion": "success" }, ... ] }
#     ]
#   }
#
# `runs_for_sha` must list EVERY run GitHub reports for the commit, across every
# workflow; that is what makes supersession visible. Trimming it to the runs
# being reported defeats the check, which is why the failure messages say so.
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
import glob, json, sys

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

try:
    import yaml
except ImportError:
    die("PyYAML is not installed, so the expected jobs cannot be derived")

# The expected set comes from the workflow files, never from the report. A
# status file cannot be asked whether it is complete; the workflows are what
# say how many jobs there should be and what they are called.
expected = {}
for workflow_path in sorted(
        glob.glob(".github/workflows/*.yml") + glob.glob(".github/workflows/*.yaml")):
    document = yaml.safe_load(open(workflow_path, encoding="utf-8"))
    workflow = document.get("name") or workflow_path
    expected[workflow] = {
        definition.get("name") or job_id
        for job_id, definition in (document.get("jobs") or {}).items()
    }

if not expected:
    die("no workflow files found, so there is nothing to check the report against")

runs_for_sha = report.get("runs_for_sha")
if not isinstance(runs_for_sha, list) or not runs_for_sha:
    die("the status file has no runs_for_sha; without the full run list for this\n"
        "commit a superseded run cannot be told from the current one")

reported = report.get("runs")
if not isinstance(reported, list) or not reported:
    die("the status file lists no runs; a rollup alone is not enough")

by_workflow = {}
for run in reported:
    workflow = str(run.get("workflow", ""))
    if not workflow:
        die("a reported run names no workflow")
    if workflow in by_workflow:
        die(f"{workflow} is reported twice; one run per workflow")
    by_workflow[workflow] = run

missing_workflows = sorted(set(expected) - set(by_workflow))
if missing_workflows:
    die(f"no run reported for: {', '.join(missing_workflows)}. Every workflow in\n"
        ".github/workflows must have run on this commit — a workflow that did not\n"
        "run is the failure this check exists for, and it looks like nothing.")

unknown = sorted(set(by_workflow) - set(expected))
if unknown:
    die(f"reported runs name workflows that do not exist here: {', '.join(unknown)}")

for workflow in sorted(expected):
    run = by_workflow[workflow]
    run_id = run.get("id")
    if not isinstance(run_id, int):
        die(f"{workflow}'s reported run has no numeric id")

    siblings = [r for r in runs_for_sha if str(r.get("workflow", "")) == workflow]
    if not siblings:
        die(f"runs_for_sha lists no run of {workflow} for this commit, but one is\n"
            "reported — the run list is trimmed, and a trimmed list hides exactly\n"
            "the newer run this check looks for")

    unfinished = [r for r in siblings if r.get("status") != "completed"]
    if unfinished:
        die(f"{workflow} has a run still in progress on this commit "
            f"({unfinished[0].get('id')}); a run in flight is not a pass")

    newest = max(siblings, key=lambda r: r.get("id", 0))
    if newest.get("id") != run_id:
        die(f"{workflow} run {run_id} was superseded by {newest.get('id')} on the\n"
            "same commit. A re-run exists because someone doubted the first answer;\n"
            "the newest one is the answer.")

    if newest.get("conclusion") != "success":
        die(f"{workflow} run {run_id} concluded {newest.get('conclusion')!r}, not\n"
            "'success'. Cancelled is not green — a cancelled run has completed\n"
            "status and no result.")

    jobs = run.get("jobs")
    if not isinstance(jobs, list) or not jobs:
        die(f"{workflow} run {run_id} lists no jobs")

    print(f"    {workflow} run {run_id}: {len(jobs)} jobs for {sha[:12]}")
    for job in sorted(jobs, key=lambda j: str(j.get("name", ""))):
        print(f"      {str(job.get('conclusion', '?')):>10}  {job.get('name', '?')}")

    seen = {str(job.get("name", "")) for job in jobs}
    absent = sorted(expected[workflow] - seen)
    if absent:
        die(f"{workflow} run {run_id} has no result for: {', '.join(absent)}.\n"
            "A job missing from the report is not a job that passed.")

    surplus = sorted(seen - expected[workflow])
    if surplus:
        die(f"{workflow} run {run_id} reports jobs that are not in the workflow:\n"
            f"{', '.join(surplus)}. The report and the workflow have drifted.")

    # Unchanged, and the part that was always right: success, and nothing else.
    # Not "anything but failure" — cancelled, skipped and neutral are all ways
    # for a job to produce no result while looking like it finished.
    bad = [j for j in jobs if j.get("conclusion") != "success"]
    if bad:
        names = ", ".join(f"{j.get('name','?')} ({j.get('conclusion')})" for j in bad)
        die(f"{len(bad)} job(s) in {workflow} are not green: {names}")
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
run_gate "client" ./scripts/client-gates.sh
run_gate "conformance" ./scripts/conformance.sh
run_gate "interop" ./scripts/interop.sh
run_gate "e2e" ./scripts/e2e.sh
run_gate "mutation" ./scripts/mutation.sh

if [[ ${#failures[@]} -gt 0 ]]; then
  fail "local gates failed: ${failures[*]}"
fi

echo
echo "PREFLIGHT PASSED for $local_head. The job table above goes in the report."
