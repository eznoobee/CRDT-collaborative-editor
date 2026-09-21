#!/usr/bin/env bash
# Push, then confirm CI actually started for the commit that was pushed.
#
# THE FOURTH TIME. CI has silently stopped verifying this repository four
# times: 3b.1's duplicate `working-directory:` key (seven tasks), run 79
# cancelled by the concurrency group (six pushes), and 7b.12's step with a name
# and no command (five tasks, during which 9.0 reported the workflows gate
# green). Each one was found at phase end, by a preflight, long after the work
# it should have checked.
#
# scripts/check-workflows.sh now rejects the file shapes that caused two of
# them, and that fixes those instances. It cannot fix the class: a workflow can
# fail to start for reasons that are not in the file — a disabled workflow, a
# spent quota, a cancelled run, a `paths:` filter that excludes the change, a
# ref with no workflow on it at all. What every one of those has in common is
# observable in one place, immediately: *after a push, is there a run with jobs
# for this exact sha?*
#
# §13.43: a rule phrased as care does not install a habit; a script that
# refuses to finish does. So pushing goes through here, the way sabotaging goes
# through scripts/sabotage.sh, and the check is not something to remember at the
# end of a phase.
#
#   scripts/push.sh [git push arguments...]
#
# With no arguments it pushes the current branch to origin with -u.
set -uo pipefail

cd "$(git rev-parse --show-toplevel)" || exit 1

REPO="${PUSH_REPO:-eznoobee/CRDT-collaborative-editor}"
API="https://api.github.com/repos/$REPO"

# How long to wait for a run to appear, and how long for jobs to be scheduled
# within it. A queued run has no jobs yet, which is not the failure this looks
# for, so both are waited out before anything is concluded.
APPEAR_DEADLINE="${PUSH_APPEAR_DEADLINE:-180}"

branch=$(git rev-parse --abbrev-ref HEAD)

if [[ $# -eq 0 ]]; then
    set -- -u origin "$branch"
fi

echo "==> git push $*"
if ! git push "$@"; then
    echo "PUSH FAILED: git push exited non-zero; nothing to verify." >&2
    exit 1
fi

sha=$(git rev-parse HEAD)
echo "==> Confirming CI started for ${sha:0:8}"

deadline=$(( $(date +%s) + APPEAR_DEADLINE ))
runs=""
jobs=0

while [[ $(date +%s) -lt $deadline ]]; do
    read -r runs jobs empty < <(./scripts/ci-jobs-for-sha.py "$REPO" "$sha")

    # EVERY run must have jobs, not the total across them. This repository has
    # two workflows and the 7b.12 outage left one of them healthy, so every
    # commit in that window reports two runs and one job — a total that is not
    # zero while the workflow that matters ran nothing.
    if [[ "${runs:-0}" -gt 0 && "${empty:-1}" -eq 0 ]]; then
        echo "    ${runs} run(s), ${jobs} job(s), none empty, for ${sha:0:8}"
        echo "PUSHED AND VERIFIED: CI is running for this commit."
        exit 0
    fi

    sleep 10
done

echo >&2
echo "PUSH VERIFICATION FAILED for ${sha:0:8} after ${APPEAR_DEADLINE}s." >&2
echo >&2
echo "  runs for this sha:  ${runs:-0}" >&2
echo "  jobs across them:   ${jobs:-0}" >&2
echo "  runs with no jobs:  ${empty:-?}" >&2
echo >&2

if [[ "${empty:-0}" -gt 0 ]]; then
    echo "A run exists with NO JOBS. That is what an unparseable workflow looks" >&2
    echo "like: GitHub shows the workflow's path where its name should be, there" >&2
    echo "is no failing step and no log. Run scripts/check-workflows.sh." >&2
else
    echo "NO RUN AT ALL for this commit. Workflows disabled, quota spent, a" >&2
    echo "paths: filter excluding this change, or no workflow on this ref." >&2
fi

echo >&2
echo "The commit is pushed. It is NOT verified, and nothing else will notice" >&2
echo "until a phase preflight — which is how this went unseen four times." >&2
exit 1
