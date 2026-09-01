#!/usr/bin/env bash
# Runs the .NET test suite.
#
# Two runners, because two test stacks (see AGENTS.md):
#
#   xunit.v3 / Microsoft.Testing.Platform  -> `dotnet run`
#     On .NET 10 the SDK routes `dotnet test` through MTP, and its bridge does
#     not discover tests in xunit.v3 4.0.0 projects — every project reports
#     "Zero tests ran". `dotnet run` invokes xunit's in-process runner directly
#     and propagates exit codes.
#
#   xunit v2 / VSTest                      -> `dotnet test`
#     Crdt.Core.Tests only, so that Stryker can drive it (stryker-net#3094).
#
# global.json deliberately does NOT pin a test runner: doing so forbids mixing
# the two, and the mix is the point.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> Building solution"
dotnet build CollaborativeEditor.slnx --nologo

failed=()
for proj in tests/*/; do
  name="$(basename "$proj")"
  csproj="${proj}${name}.csproj"
  [[ -f "$csproj" ]] || continue

  echo
  echo "==> $name"
  if grep -q 'Include="xunit.v3"' "$csproj"; then
    dotnet run --project "$csproj" --no-build || failed+=("$name")
  else
    dotnet test "$csproj" --no-build --nologo || failed+=("$name")
  fi
done

echo
if (( ${#failed[@]} > 0 )); then
  echo "FAILED: ${failed[*]}"
  exit 1
fi
echo "All test projects passed."
