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

# The repository the caller is in, not the one this script lives in. Those are
# the same thing in every real use and different in the self-test, whose whole
# point is a throwaway repo — and a self-test that operated on the real tree
# both proved nothing and sabotaged the wrong thing.
cd "$(git rev-parse --show-toplevel)" || exit 1

# A revert that silently does not revert is the exact failure this script
# exists to prevent, so it is proved on every run rather than assumed. Both
# cases are covered because only one of them was, and the other is the one that
# reached a commit.
self_test() {
    local sandbox tracked untracked
    sandbox=$(mktemp -d)
    (
        cd "$sandbox" || exit 1
        git init -q .
        git config user.email t@t.invalid
        git config user.name t
        echo "tracked-original" > tracked.txt
        git add tracked.txt
        git commit -qm base
        echo "untracked-original" > untracked.txt

        cat > patch.sh <<'PATCH'
#!/usr/bin/env sh
echo "tracked-sabotaged" > tracked.txt
echo "untracked-sabotaged" > untracked.txt
echo "created-by-sabotage" > created.txt
PATCH
        chmod +x patch.sh

        # patch.sh is itself untracked, so it must survive its own run.
        "$SABOTAGE" ./patch.sh true > /dev/null 2>&1

        [ "$(cat tracked.txt)" = "tracked-original" ] || { echo "tracked"; exit 1; }
        [ "$(cat untracked.txt)" = "untracked-original" ] || { echo "untracked"; exit 1; }
        [ ! -f created.txt ] || { echo "created"; exit 1; }
        [ -f patch.sh ] || { echo "patch"; exit 1; }
    )
    local failed=$?
    rm -rf -- "$sandbox"

    if [ "$failed" -ne 0 ]; then
        echo "SELF-TEST FAILED: the revert does not restore the tree." >&2
        exit 1
    fi
}

SABOTAGE=$(cd "$(dirname "$0")" && pwd)/sabotage.sh
export SABOTAGE

# The recursion guard: the self-test runs this script inside its sandbox, and
# that inner run must not self-test again.
if [ "${SABOTAGE_SELF_TEST:-1}" = "1" ]; then
    export SABOTAGE_SELF_TEST=0
    self_test
    if [ "${1:-}" = "--self-test" ]; then
        echo "sabotage.sh: revert restores tracked and untracked files"
        exit 0
    fi
fi

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

# Untracked files are copied aside and restored wholesale, because
# 'git checkout -- .' does not touch them at all. Deleting only the ones the
# sabotage created is not enough: a sabotage that EDITS an untracked file — a
# new source file added by the task in hand and not yet committed — survives the
# revert silently and lands in the next commit. That is not hypothetical; it is
# how default(ActivityContext) got into EditorTracing.cs.
root=$(pwd)
before=$(mktemp -d)
git ls-files --others --exclude-standard -z | while IFS= read -r -d '' file; do
    mkdir -p "$before/$(dirname "$file")"
    cp "$file" "$before/$file"
done

patch="$1"
shift

revert() {
    git checkout -- .

    # Every untracked file the tree had before, restored; every untracked file
    # the sabotage introduced, removed.
    git ls-files --others --exclude-standard -z | while IFS= read -r -d '' file; do
        rm -f -- "$file"
    done
    (cd "$before" && find . -type f -printf '%P\0') | while IFS= read -r -d '' file; do
        mkdir -p "$root/$(dirname "$file")"
        cp "$before/$file" "$root/$file"
    done
    rm -rf -- "$before"
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
