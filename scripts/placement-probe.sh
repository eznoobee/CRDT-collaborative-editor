#!/usr/bin/env bash
# Which tests actually guard element placement? (register row 34, §13.47)
#
# Inverts the sibling tie-break — `compareElementId` in both cores — and reports
# which suites notice. That single change reorders every user's text whenever
# two people type at the same position, so any suite that stays green under it
# is a suite with no opinion about where characters go.
#
# WHY THIS IS THE PROBE AND NOT A READING EXERCISE. Row 34 was opened to audit
# the convergence assertions for §13.42's shape by hand. The audit found
# something a reading could not have established:
#
#   A convergence assertion is invariant under ANY consistent ordering rule.
#   Both replicas run the same comparator, so they agree on whatever it says.
#
# So no two-party comparison in this repository can detect a placement bug — not
# because any of them is written badly, but because convergence and correct
# placement are different properties and only the second needs an oracle the
# replicas did not compute. The probe is what makes that checkable rather than
# argued, and re-runnable when the suites change.
#
# Read the output as a census, not a pass/fail: a suite reported as blind is
# usually blind correctly, because placement belongs to the core and to the
# conformance corpus. What matters is that the total is not zero, and that the
# suites a contributor runs by default are not all in the blind column.
#
#   scripts/placement-probe.sh [output-file]
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

OUT=${1:-/dev/stdout}
SCRATCH=$(mktemp -d)
trap 'rm -rf -- "$SCRATCH"' EXIT

: "${EDITOR_TEST_POSTGRES:?set EDITOR_TEST_POSTGRES}"
: "${EDITOR_TEST_REDIS:?set EDITOR_TEST_REDIS}"

cat > "$SCRATCH/invert-csharp.sh" <<'PATCH'
#!/usr/bin/env sh
python3 - <<'PY'
import pathlib
p = pathlib.Path('src/Crdt.Core/ElementId.cs')
s = p.read_text()
old = "        var replica = Replica.CompareTo(other.Replica);\n        return replica != 0 ? replica : Seq.CompareTo(other.Seq);"
new = "        var replica = other.Replica.CompareTo(Replica);\n        return replica != 0 ? replica : other.Seq.CompareTo(Seq);"
assert old in s, "the C# comparator has moved; update this probe"
p.write_text(s.replace(old, new))
PY
PATCH

cat > "$SCRATCH/invert-typescript.sh" <<'PATCH'
#!/usr/bin/env sh
python3 - <<'PY'
import pathlib
p = pathlib.Path('client/src/crdt/elementId.ts')
s = p.read_text()
old = """  const replica = compareReplicaId(a.replica, b.replica);
  if (replica !== 0) {
    return replica;
  }

  return a.seq === b.seq ? 0 : a.seq < b.seq ? -1 : 1;"""
new = """  const replica = compareReplicaId(b.replica, a.replica);
  if (replica !== 0) {
    return replica;
  }

  return a.seq === b.seq ? 0 : a.seq < b.seq ? 1 : -1;"""
assert old in s, "the TypeScript comparator has moved; update this probe"
p.write_text(s.replace(old, new))
PY
PATCH

chmod +x "$SCRATCH"/invert-*.sh

# Each runner reports failures differently, so each is parsed on its own terms.
# A single regex over all three is how "to" and "Tests" ended up counted as
# failing test names in this script's first run.
failing_names() {
    local kind=$1 log=$2 names

    case "$kind" in
        vstest)
            # dotnet test: "  Failed Namespace.Class.Method [12 ms]"
            names=$(grep -oE '^[[:space:]]+Failed [A-Za-z0-9_.]+' "$log" \
                | sed 's/^[[:space:]]*Failed //' | sed 's/.*\.//' | sort -u) ;;
        mtp)
            # Microsoft.Testing.Platform: "    Namespace.Class.Method [FAIL]"
            names=$(sed 's/\x1b\[[0-9;]*m//g' "$log" \
                | grep -oE '[A-Za-z0-9_.]+\.[A-Za-z0-9_]+ \[FAIL\]' \
                | sed 's/ \[FAIL\]//' | sed 's/.*\.//' | sort -u) ;;
        vitest)
            # vitest: "     × the test name 4ms"
            names=$(sed 's/\x1b\[[0-9;]*m//g' "$log" \
                | grep -E '^[[:space:]]+× ' \
                | sed 's/^[[:space:]]*× //; s/[[:space:]][0-9]+ms$//; s/[[:space:]][0-9]*ms$//' \
                | sed 's/[[:space:]]*$//' | sort -u) ;;
    esac

    printf '%s\n' "$names" | grep '[^[:space:]]' | sort -u
}

# A BASELINE RUN, AND WHY IT COSTS THE RUNTIME IT COSTS.
#
# This script used to count the tests that were red with the sabotage applied
# and call that the detections. It cannot: a test that would have failed anyway
# is red under the sabotage too, and gets counted as an oracle it is not. That
# is not hypothetical — 9.1's run reported `PeriodicSnapshotTests` detecting the
# inversion, and the test turned out to have no placement oracle at all. It was
# red for register row 37's reason, the shared database's global ranking, which
# only bites under full-suite load. Run alone under the same sabotage it is
# green.
#
# So the census is the DIFFERENCE: red with the sabotage, green without it.
# Doubling the runtime is the price of the number meaning what it says, and this
# script is run once per audit.
#
# The baseline failures are reported rather than subtracted silently. A suite
# that is red on its own is a fact about the repository that this script is in a
# position to notice, and quietly removing it from both sides would hide it.
run_suite() {
    local label=$1 patch=$2 kind=$3 command=$4 log="$SCRATCH/$5" base="$SCRATCH/base-$5"

    # Unsabotaged first. Same command, same environment, same parser.
    bash -c "cd '$PWD' && $command > '$base' 2>&1" > /dev/null 2>&1

    ./scripts/sabotage.sh "$SCRATCH/invert-$patch.sh" \
        bash -c "cd '$PWD' && $command > '$log' 2>&1" > /dev/null 2>&1

    if [ ! -s "$log" ] || [ ! -s "$base" ]; then
        echo "| $label | **the suite did not run** | — | — |"
        return
    fi

    local sabotaged baseline detected
    sabotaged=$(failing_names "$kind" "$log")
    baseline=$(failing_names "$kind" "$base")

    # Red with the sabotage and green without it. comm needs sorted input,
    # which failing_names guarantees.
    detected=$(comm -23 <(printf '%s\n' "$sabotaged") <(printf '%s\n' "$baseline"))

    local red joined already
    red=$(printf '%s\n' "$detected" | grep -c '[^[:space:]]')
    joined=$(printf '%s\n' "$detected" | grep '[^[:space:]]' | paste -sd'; ' -)
    already=$(printf '%s\n' "$baseline" | grep '[^[:space:]]' | paste -sd'; ' -)

    echo "| $label | $red | ${joined:-—} | ${already:-none} |"
}

{
    echo "# Placement probe — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo
    echo "The sibling tie-break inverted; which suites notice."
    echo
    echo "Each suite is run twice: once unsabotaged, once with the tie-break"
    echo "inverted. A detection is a test red in the second and green in the"
    echo "first, because a test that fails anyway is red under any sabotage and"
    echo "is not evidence of an oracle."
    echo
    echo "| suite | detected the inversion | which | red at baseline |"
    echo "|---|---|---|---|"

    run_suite "Crdt.Core.Tests" csharp vstest \
        "dotnet test tests/Crdt.Core.Tests/Crdt.Core.Tests.csproj -c Release" core.log

    run_suite "Conformance (C#)" csharp mtp \
        "dotnet build tests/Conformance -c Release >/dev/null && dotnet run --project tests/Conformance/Conformance.csproj -c Release --no-build" conf.log

    run_suite "Editor.Api.Tests" csharp mtp \
        "dotnet build tests/Editor.Api.Tests/Editor.Api.Tests.csproj -c Release >/dev/null && dotnet run --project tests/Editor.Api.Tests/Editor.Api.Tests.csproj -c Release --no-build" api.log

    run_suite "client (npm test)" typescript vitest \
        "cd client && npx vitest run" client.log

    echo
    echo "Every detection is a comparison against a value committed to this"
    echo "repository. No two-party comparison appears above, and none can."
    echo
    echo "A non-empty *red at baseline* column is not noise this script removed:"
    echo "it is a suite failing on its own, which is worth more attention than"
    echo "the census it would otherwise have inflated."
} | tee "$OUT"
