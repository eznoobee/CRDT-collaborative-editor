#!/usr/bin/env bash
# The application in a real browser (PROJECT_SPEC.md §9, §11's Phase 4 row).
#
# Separate from interop.sh because this one also builds the client and drives
# Chromium. What it proves is the thing §13.22 records as having been missing:
# not that the pieces work, but that a person can open the application, sign in
# and type.
#
# Authentication is real, and nothing is relaxed for it. The harness serves OIDC
# metadata over genuine HTTPS and Chromium is given that one certificate by SPKI
# pin — never `ignoreHTTPSErrors`, which would switch certificate validation off
# for the whole browser and quietly invalidate every claim these tests make.
set -euo pipefail

cd "$(dirname "$0")/.."

: "${EDITOR_TEST_POSTGRES:?set EDITOR_TEST_POSTGRES to a Postgres connection string}"
: "${EDITOR_TEST_REDIS:?set EDITOR_TEST_REDIS to a Redis configuration string}"

echo "==> Restore"
dotnet restore CollaborativeEditor.slnx

echo
echo "==> Schema"
dotnet tool restore >/dev/null
EDITOR_TEST_POSTGRES="$EDITOR_TEST_POSTGRES" dotnet ef database update \
  --project src/Editor.Infrastructure \
  --startup-project src/Editor.Infrastructure

echo
echo "==> Building the API the harness will run"
dotnet build src/Editor.Api --nologo -v q

echo
echo "==> End-to-end suite"
cd client
[[ -d node_modules ]] || npm ci --silent

# NODE_ENV is set by the harness for the client build it makes, deliberately and
# not here: vitest sets NODE_ENV=test in its own process, a build spawned from a
# test inherits it, and Vite then resolves React through its development export
# condition. See §13.26 — the harness also asserts the artefact, because setting
# a variable fixes one cause and checking the output fails whatever the next one
# turns out to be.
npm run --silent test:e2e
