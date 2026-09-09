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

# §12: no harness reaches past the product to create a document.
#
# Register rows 15 and 16 were exactly this — every harness seeded through psql
# because nothing in the product could make a document, and eleven phases of a
# green suite never noticed. A grep rather than a review, because judgement at
# the end of a long phase is what produced those rows: it is easy to add one
# INSERT "just for this test", and impossible to see later.
echo "==> no seeded documents"
if grep -rniE 'insert[[:space:]]+into[[:space:]]+(documents|document_members|users)' \
    src --include='*.ts' --include='*.tsx'; then
  echo
  echo "A harness is writing rows the product's own API should create."
  echo "PROJECT_SPEC.md §12: seeding through psql is what register rows 15 and 16 were."
  exit 1
fi
