#!/usr/bin/env bash
# The client's own gates: lint, typecheck, unit tests, build.
#
# A script rather than four commands someone remembers, and wired into
# `phase-preflight.sh` for §12's reason: a check whose invocation is a judgement
# call is skipped exactly when it matters. Twice in Phase 4 a commit went out
# with `tsc` or `eslint` red because the verification was chained onto the same
# line as the commit and its output was read afterwards. CI would have caught
# both; the point of the preflight is not to need CI to find them.
set -euo pipefail

cd "$(dirname "$0")/../client"

[[ -d node_modules ]] || npm ci --silent

echo "==> lint"
npm run --silent lint

echo "==> typecheck"
npm run --silent typecheck

echo "==> tests"
npm run --silent test

echo "==> build"
npm run --silent build

# The no-seeding rule moved to scripts/check-seeding.sh, which covers both
# harnesses and has a CI job of its own.
#
# It lived here, grepping `client/src` only, and nothing in CI called this
# script — so it covered half the repository and ran once per phase. Register
# row 25 is both halves of that. Still called from here, so a client-side
# regression fails the client gate too.
../scripts/check-seeding.sh
