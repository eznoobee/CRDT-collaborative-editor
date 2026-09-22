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
# IT HAPPENED AGAIN, AND THIS SCRIPT DID NOT CATCH IT (§13.57). 7b.12 spliced a
# new job in and took the `run:` line of the step above it with the splice,
# leaving a step with a name and no command. GitHub rejects that; PyYAML does
# not, so this script printed "ok ci.yml: 15 jobs" while every push from 7b.12
# through 9.6 produced a run with zero jobs. Five tasks and two phase gates went
# unverified, and the only visible tell was the run list showing the workflow's
# path instead of its name.
#
# The lesson is about this script rather than about YAML. It was written after
# 3b.1's duplicate `working-directory:` key, and it checked for duplicate keys —
# the shape of the one failure that had happened, rather than the class the
# script is named for. It now checks the structure GitHub requires as well:
# every job has `runs-on` and a non-empty `steps`, and every step is exactly one
# of `run` or `uses`.
#
# Duplicate mapping keys stay first because they remain the subtlest trap: every
# YAML library in common use accepts them silently, last value winning, so "it
# parses" is not evidence.
set -euo pipefail

cd "$(dirname "$0")/.."

# THE SELF-TEST. A gate that stops rejecting goes quiet, and this one did
# exactly that for five tasks: it kept printing "ok" while GitHub refused the
# file. So it is run against the two shapes that have actually broken this
# repository, and refuses to proceed if either is accepted. The fixtures are
# those failures, kept.
if [[ "${CHECK_WORKFLOWS_SELF_TEST:-1}" == "1" ]]; then
    for fixture in tests/fixtures/workflows/*.yml; do
        if CHECK_WORKFLOWS_SELF_TEST=0 "$0" "$fixture" >/dev/null 2>&1; then
            echo "SELF-TEST FAILED: $fixture was accepted and must not be." >&2
            echo "This gate no longer rejects a workflow GitHub would reject." >&2
            exit 1
        fi
    done
fi

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

    # The structure GitHub requires. A document that parses is not a document
    # Actions will run, and each of these produces a run with zero jobs and
    # nothing local to read.
    problems = []
    for name, job in document["jobs"].items():
        if not isinstance(job, dict):
            problems.append(f"job {name} is not a mapping")
            continue

        if "uses" in job:
            # A reusable-workflow call has no steps and no runs-on of its own.
            continue

        if not job.get("runs-on"):
            problems.append(f"job {name} has no runs-on")

        steps = job.get("steps")
        if not isinstance(steps, list) or not steps:
            problems.append(f"job {name} has no steps")
            continue

        for index, step in enumerate(steps):
            if not isinstance(step, dict):
                problems.append(f"job {name} step {index + 1} is not a mapping")
                continue

            has_run = "run" in step
            has_uses = "uses" in step
            label = step.get("name") or f"step {index + 1}"

            if has_run and has_uses:
                problems.append(f"job {name}: {label!r} has both run and uses")
            elif not has_run and not has_uses:
                # 7b.12's failure, exactly: a name with nothing under it.
                problems.append(f"job {name}: {label!r} has neither run nor uses")

    if problems:
        failed = True
        print(f"REJECTED {path}:")
        for problem in problems:
            print(f"  {problem}")
        continue

    print(f"ok {path}: {len(document['jobs'])} jobs")

sys.exit(1 if failed else 0)
PY
