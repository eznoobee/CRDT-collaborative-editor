#!/usr/bin/env bash
# Runs the .NET test suite.
#
# Why not `dotnet test`: on .NET 10 the SDK routes `dotnet test` through
# Microsoft.Testing.Platform, and its bridge does not discover tests in
# xunit.v3 4.0.0 projects — every project reports "Zero tests ran" (exit code 5)
# while the same assemblies run correctly when executed directly. `dotnet run`
# invokes xunit's in-process runner, reports real results, and propagates exit
# codes (verified: 0 when passing, 1 when failing).
#
# Revisit when either package updates; see AGENTS.md.
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
  if ! dotnet run --project "$csproj" --no-build; then
    failed+=("$name")
  fi
done

echo
if (( ${#failed[@]} > 0 )); then
  echo "FAILED: ${failed[*]}"
  exit 1
fi
echo "All test projects passed."
