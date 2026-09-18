#!/usr/bin/env bash
# §13.37's mechanical check, for every configured limit (register row 27).
#
#   "If the test would still pass after halving the configured number, it is
#    testing the wrong thing."
#
# Each limit is lowered through CONFIGURATION — the same path a deployment
# uses, via environment variables — and the largest-legitimate-use suite is run
# against it. No source file is touched, so this needs no sabotage harness and,
# more importantly, it exercises the number where an operator would set it: a
# check that edited a compiled-in default would prove the default's tests work
# and say nothing about the configuration path that ships.
#
# WHY IT DIVIDES RATHER THAN HALVES ONCE. Halving is a sound check for a limit
# set AT the largest legitimate use and the wrong check for one set at a stated
# multiple of it — and most of these are the second kind. "Survived halving"
# then reads as a failure when it is a margin, and the two are only
# distinguishable by how far the number can actually fall. So each limit is
# divided until a use test goes red, and the factor is the answer: 2 means the
# number sits on top of the use, 64 means it is nowhere near it and nothing in
# this suite is watching it.
#
# THREE OUTCOMES, AND ONLY ONE OF THEM IS RED.
#   red at N      the use survives down to 1/N and breaks below it
#   refused       options validation rejected the value; NOT a result, and the
#                 floor it names may be a stronger guarantee than any test
#   host failed   the suite could not run — a dead Postgres looks exactly like
#                 a sabotage that bit everywhere, and reading it as the latter
#                 is how a pass vindicates itself (§13.41, 7b.5's log buffer)
#
#   scripts/limit-headroom.sh [output-file]
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

OUT=${1:-/dev/stdout}
FILTER='/*/*/LargestLegitimateUseTests/*'
PROJECT=tests/Editor.Api.Tests/Editor.Api.Tests.csproj
MAX_FACTOR=${MAX_FACTOR:-64}

: "${EDITOR_TEST_POSTGRES:?set EDITOR_TEST_POSTGRES}"
: "${EDITOR_TEST_REDIS:?set EDITOR_TEST_REDIS}"

# name | environment variable | kind (int|seconds) | default
LIMITS=$(cat <<'ROWS'
Ingest.MaxDocumentBytes|Ingest__MaxDocumentBytes|int|5242880
Ingest.MaxOperationsPerBatch|Ingest__MaxOperationsPerBatch|int|256
Ingest.MaxRunCodePoints|Ingest__MaxRunCodePoints|int|256
Ingest.MaxMessageBytes|Ingest__MaxMessageBytes|int|65536
Ingest.MaxReplicasPerDocument|Ingest__MaxReplicasPerDocument|int|50
RateLimits.CodePointsPerConnection|RateLimits__CodePointsPerConnection|int|10000
RateLimits.CodePointsPerUser|RateLimits__CodePointsPerUser|int|30000
DocumentApiRateLimits.WritesPerUser|DocumentApiRateLimits__WritesPerUser|int|120
ConnectionLimits.MaxPerUser|ConnectionLimits__MaxPerUser|int|32
ConnectTicket.Lifetime|ConnectTicket__Lifetime|seconds|60
ReplicaClaims.Lifetime|ReplicaClaims__Lifetime|seconds|120
ROWS
)

# A value divided by a factor, in the form its option expects.
divided() {
    local kind=$1 default=$2 factor=$3
    if [ "$kind" = int ]; then
        echo $(( default / factor ))
    else
        local s=$(( default / factor ))
        printf '%02d:%02d:%02d\n' $(( s / 3600 )) $(( s % 3600 / 60 )) $(( s % 60 ))
    fi
}

# Prints REFUSED, HOST-FAILED, or the names of the tests that failed.
run() {
    local output
    output=$(dotnet run --project "$PROJECT" -c Release --no-build -- -filter "$FILTER" 2>&1 \
        | sed 's/\x1b\[[0-9;]*m//g')

    # Checked before the failure list, because a refused value fails every test
    # in the file and is indistinguishable from a limit that broke everything.
    if grep -q 'OptionsValidationException' <<<"$output"; then
        grep -oE "The field [A-Za-z]+ must be between [^']+" <<<"$output" | head -1 \
            | sed 's/^/REFUSED /'
        return
    fi

    if grep -q "threw in InitializeAsync" <<<"$output"; then
        echo HOST-FAILED
        return
    fi

    local total
    total=$(grep -oE 'Total: [0-9]+' <<<"$output" | head -1 | grep -oE '[0-9]+')
    if [ -z "$total" ] || [ "$total" -eq 0 ]; then
        echo HOST-FAILED
        return
    fi

    grep -oE 'LargestLegitimateUseTests\.[A-Za-z_]+ \[FAIL\]' <<<"$output" \
        | sed 's/LargestLegitimateUseTests\.//; s/ \[FAIL\]//'
}

dotnet build "$PROJECT" -c Release > /dev/null || exit 1

{
    echo "# Halving pass — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo

    baseline=$(run)
    case "$baseline" in
        HOST-FAILED) echo "BASELINE DID NOT RUN. Nothing below would mean anything."; exit 1 ;;
        REFUSED*)    echo "BASELINE WAS REFUSED: $baseline"; exit 1 ;;
        ?*)          echo "BASELINE IS NOT GREEN:"; echo "$baseline" | sed 's/^/  /'; exit 1 ;;
    esac
    echo "Baseline: all green."
    echo

    while IFS='|' read -r name var kind default; do
        [ -z "$name" ] && continue
        echo "## $name (default $default)"

        factor=2
        while [ "$factor" -le "$MAX_FACTOR" ]; do
            value=$(divided "$kind" "$default" "$factor")
            if [ "$kind" = int ] && [ "$value" -lt 1 ]; then
                echo "  bottomed out at 1/$factor without a red test."
                break
            fi

            result=$(env "$var=$value" bash -c \
                "$(declare -f run); PROJECT='$PROJECT' FILTER='$FILTER'; run")

            case "$result" in
                HOST-FAILED)
                    echo "  1/$factor = $value: THE SUITE DID NOT RUN. Not a result; investigate."
                    break ;;
                REFUSED*)
                    echo "  1/$factor = $value: ${result#REFUSED }"
                    echo "  -> cannot be lowered this far by configuration at all."
                    break ;;
                ?*)
                    echo "  1/$factor = $value: red"
                    echo "$result" | sed 's/^/       /'
                    break ;;
                *)
                    echo "  1/$factor = $value: still green"
                    factor=$(( factor * 2 )) ;;
            esac
        done

        if [ "$factor" -gt "$MAX_FACTOR" ]; then
            echo "  NOTHING WENT RED down to 1/$MAX_FACTOR — no use in this suite watches this number."
        fi
        echo
    done <<<"$LIMITS"
} | tee "$OUT"
