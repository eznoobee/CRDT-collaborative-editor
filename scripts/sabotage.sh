#!/usr/bin/env bash
# §12's sabotage practice, with the one part that keeps being got wrong made
# mechanical.
#
# Reverting a sabotage means `git checkout -- .`, which reverts every tracked
# change in the tree — including implementation work that was never committed.
# That has now destroyed uncommitted work four times in this project, each time
# after the rule "commit before the first sabotage, not before the first revert"
# had already been written down. §13.43: a rule phrased as care does not install
# a habit; a script that refuses to start does.
#
#   scripts/sabotage.sh <patch-script> <test-command...>
#
# <patch-script> is run once to apply the sabotage. Whatever it does to tracked
# files is reverted afterwards; untracked files it creates are removed.
set -uo pipefail

cd "$(dirname "$0")/.."

if [ "$#" -lt 2 ]; then
    echo "usage: scripts/sabotage.sh <patch-script> <test-command...>" >&2
    exit 2
fi

# The refusal. Not a warning: a warning is a thing to read past.
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "REFUSING: the working tree has uncommitted changes to tracked files." >&2
    echo "" >&2
    echo "A sabotage is reverted with 'git checkout -- .', which would destroy them." >&2
    echo "Commit the implementation first — that is what makes the revert safe." >&2
    echo "" >&2
    git status --short >&2
    exit 1
fi

# Untracked files are not reverted by checkout, so they are recorded and removed
# by name afterwards. Sabotaging a task whose new files are untracked otherwise
# leaves the sabotage in place, and the next run measures the wrong tree.
before=$(git ls-files --others --exclude-standard | sort)

patch="$1"
shift

revert() {
    git checkout -- .
    after=$(git ls-files --others --exclude-standard | sort)
    comm -13 <(echo "$before") <(echo "$after") | while read -r new; do
        [ -n "$new" ] && rm -f -- "$new"
    done
}
trap revert EXIT

if ! "$patch"; then
    echo "SABOTAGE DID NOT APPLY — the patch script failed, so nothing was measured." >&2
    exit 1
fi

"$@"
status=$?

if [ "$status" -eq 0 ]; then
    echo ""
    echo "SUSPICIOUS: the suite passed with the sabotage applied."
    echo "Either the sabotage did not express the property, or nothing tests it."
fi

exit "$status"
