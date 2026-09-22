#!/usr/bin/env bash
# §8's performance targets (PROJECT_SPEC.md §8, register row 5).
#
# Not part of any other gate, deliberately. These take twenty minutes, they want
# the box to themselves, and a measurement sharing a machine with a compiler is
# a measurement of the compiler.
#
# Release throughout. §8: "Every performance number is reported with the build
# that produced it, and a number without one is not a result."
set -euo pipefail

cd "$(dirname "$0")/.."

: "${EDITOR_TEST_POSTGRES:?set EDITOR_TEST_POSTGRES to a Postgres connection string}"
: "${EDITOR_TEST_REDIS:?set EDITOR_TEST_REDIS to a Redis configuration string}"

REPORT="${EDITOR_LOAD_REPORT:-$(pwd)/docs/measurements/$(date -u +%Y-%m-%dT%H%M%SZ).txt}"
export EDITOR_LOAD_REPORT="$REPORT"
mkdir -p "$(dirname "$REPORT")"

echo "==> Reporting to $REPORT"

echo
echo "==> Schema"
dotnet tool restore >/dev/null
dotnet ef database update \
  --project src/Editor.Infrastructure \
  --startup-project src/Editor.Infrastructure

echo
echo "==> Building Release"
dotnet build CollaborativeEditor.slnx -c Release --nologo -v q

echo
echo "==> Targets 1 and 4 (server-side segments)"
EDITOR_LOAD=1 dotnet run --project tests/Editor.Api.Tests -c Release --no-build -- \
  -filter "/*/*/PropagationLatencyMeasurement/*" || true
EDITOR_LOAD=1 dotnet run --project tests/Editor.Api.Tests -c Release --no-build -- \
  -filter "/*/*/DocumentLoadMeasurement/*" || true

echo
echo "==> Targets 2 and 3 (a server process of its own)"
cd client
[[ -d node_modules ]] || npm ci --silent
EDITOR_E2E_CONFIGURATION=Release npm run --silent test:load || true

echo
echo "==> Results"
cat "$REPORT"

# Reported, never gated. §8: a missed target is a recorded miss and a decision,
# not a retune — and a script that failed the build on one would make the
# decision by making the number the thing to fix.
