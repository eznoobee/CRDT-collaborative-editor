#!/usr/bin/env bash
# §13.27's walk against the real Compose stack (PROJECT_SPEC.md §11, Phase 5b).
#
# Brings the artefact up the way a deployment does and follows what a user does
# from a cold start, with nothing seeded and nothing run by hand. The step it
# stops at is the output: a green walk with no recorded stopping point is either
# a finished product or a walk trimmed to what passes.
#
# NOT A PREFLIGHT GATE, deliberately. It needs a Docker daemon, which the
# sandbox this project is developed in does not have, and a gate that skips when
# its infrastructure is missing is a check that cannot fail — the exact shape
# §13.19 is about. It runs in CI as its own job instead, and the preflight
# already refuses a phase report while any CI job is red, so the coverage is the
# same and the hole is not.
set -euo pipefail

cd "$(dirname "$0")/.."

if ! docker info >/dev/null 2>&1; then
    echo "FAILED: no Docker daemon. The walk tests a deployment; there is nothing to test." >&2
    exit 1
fi

echo "==> Walking"
cd client
[[ -d node_modules ]] || npm ci --silent
npm run --silent test:walk
