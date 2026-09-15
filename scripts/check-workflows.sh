#!/usr/bin/env bash
# Rejects a workflow file GitHub's parser would reject (PROJECT_SPEC.md §12).
#
# A workflow that does not parse produces a run with ZERO jobs and a
# conclusion of failure. There is no failing step to read, no log to open, and
# the run's name silently changes from the workflow's `name:` to its path —
# which is the only visible tell. Nothing local goes red, because nothing local
# reads this file.
#
# Editing ci.yml in 3b.1 left two `working-directory:` keys on one step. Every
# push from 3b.1 through 3b.8 was a startup failure, and eight jobs' worth of
# checks — the .NET suite, conformance, the mutation gate — did not run at all
# for seven consecutive tasks. §12's preflight would have caught it on the first
# one; it was not run. This makes the same failure local and immediate.
#
# The check is duplicate mapping keys specifically, because that is the trap:
# every YAML library in common use accepts them silently, last value winning,
# so "it parses" is not evidence. GitHub Actions rejects them.
set -euo pipefail

cd "$(dirname "$0")/.."

python3 - "$@" <<'PY'
import glob
import sys

try:
    import yaml
except ImportError:
    print("PyYAML is not installed; cannot check workflow files.", file=sys.stderr)
    sys.exit(1)


class Strict(yaml.SafeLoader):
    """A loader that refuses what GitHub refuses."""


def no_duplicate_keys(loader, node, deep=False):
    seen = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in seen:
            raise yaml.YAMLError(
                f"duplicate key {key!r} on line {key_node.start_mark.line + 1}")
        seen[key] = loader.construct_object(value_node, deep=deep)
    return seen


Strict.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, no_duplicate_keys)

paths = sys.argv[1:] or sorted(
    glob.glob(".github/workflows/*.yml") + glob.glob(".github/workflows/*.yaml"))

if not paths:
    print("No workflow files found — is this the repository root?", file=sys.stderr)
    sys.exit(1)

failed = False
for path in paths:
    try:
        document = yaml.load(open(path, encoding="utf-8"), Loader=Strict)
    except yaml.YAMLError as error:
        failed = True
        print(f"REJECTED {path}: {error}")
        continue

    if not isinstance(document, dict) or not document.get("jobs"):
        failed = True
        print(f"REJECTED {path}: no jobs")
        continue

    # A workflow with no name runs under its path, which is also what a
    # startup failure looks like in the run list. Naming them keeps the two
    # distinguishable at a glance.
    if not document.get("name"):
        failed = True
        print(f"REJECTED {path}: no top-level name")
        continue

    print(f"ok {path}: {len(document['jobs'])} jobs")

sys.exit(1 if failed else 0)
PY
