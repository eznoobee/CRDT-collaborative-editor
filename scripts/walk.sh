#!/usr/bin/env bash
# §13.27's walk against the real Compose stack (PROJECT_SPEC.md §11, Phase 5b).
#
# Brings the artefact up the way a deployment does and follows what a user does
# from a cold start, with nothing seeded and nothing run by hand. The step it
# stops at is the output: a green walk with no recorded stopping point is either
# a finished product or a walk trimmed to what passes.
#
# §7's deployment conformance used to run inside this script, because both
# suites live under src/walk and one vitest config matched both. It is now
# scripts/deployment.sh with its own CI job — see scripts/compose-suite.sh for
# why that separation is load-bearing rather than tidiness.
set -euo pipefail

cd "$(dirname "$0")/.."
# shellcheck source=scripts/compose-suite.sh
source ./scripts/compose-suite.sh

compose_suite test:walk "Walking"
