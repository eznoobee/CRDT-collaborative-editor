#!/usr/bin/env bash
# The TypeScript core against a running C# server (PROJECT_SPEC.md §9, §8).
#
# Separate from run-tests.sh because this one needs infrastructure the unit
# suites do not: a Postgres, a Redis, and a built API assembly it starts as its
# own process. Everything else in the repository either compares the two
# implementations through a file (§9's conformance runner) or drives the server
# over an in-memory transport, and neither can fail the way a deployment fails.
#
# Authentication is real. §7 forbids relaxing a token check anywhere, so the
# harness generates a certificate, serves OIDC metadata over genuine HTTPS, and
# hands the API that certificate through SSL_CERT_FILE — rather than adding a
# development bypass to the product to make a test easier.
set -euo pipefail

cd "$(dirname "$0")/.."

: "${EDITOR_TEST_POSTGRES:?set EDITOR_TEST_POSTGRES to a Postgres connection string}"
: "${EDITOR_TEST_REDIS:?set EDITOR_TEST_REDIS to a Redis configuration string}"

echo "==> Schema"
# The API deliberately does not migrate on startup, so something has to. The
# .NET suite's fixture does it for its own database; this job has its own.
dotnet tool restore >/dev/null
EDITOR_TEST_POSTGRES="$EDITOR_TEST_POSTGRES" dotnet ef database update \
  --project src/Editor.Infrastructure \
  --startup-project src/Editor.Infrastructure
echo
echo "==> Building the API the harness will run"
dotnet build src/Editor.Api --nologo -v q

echo
echo "==> Interop suite"
cd client
[[ -d node_modules ]] || npm ci --silent
npm run --silent test:interop
