#!/usr/bin/env bash
# Cross-implementation conformance check (PROJECT_SPEC.md §9).
#
# Two runners, one corpus. Each replays every trace and writes a normalised
# result file; this compares the two byte for byte. The `implementation` field is
# the one field legitimately different, so it is stripped before comparing.
#
# Each runner also checks its own traces' stated expectations, because two
# implementations agreeing with each other proves nothing if both are wrong.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> C# runner"
dotnet build tests/Conformance --nologo -v q
dotnet run --project tests/Conformance/Conformance.csproj --no-build

echo
echo "==> TypeScript runner"
(cd client && npm run --silent test -- src/crdt/conformance.test.ts)

echo
echo "==> Comparing"
csharp=artifacts/conformance/csharp.json
typescript=artifacts/conformance/typescript.json

for f in "$csharp" "$typescript"; do
  [[ -f "$f" ]] || { echo "Missing $f — did its runner produce output?"; exit 1; }
done

if diff -u <(grep -v '"implementation"' "$csharp") \
           <(grep -v '"implementation"' "$typescript"); then
  echo "Implementations agree byte for byte."
else
  echo
  echo "DIVERGENCE between the C# and TypeScript cores. Any difference fails the build (§9)."
  exit 1
fi
