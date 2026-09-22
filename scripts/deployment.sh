#!/usr/bin/env bash
# §7 asked of the application as Compose starts it (PROJECT_SPEC.md §7, §11).
#
# Every check here is a request that entered through the published port of a
# stack brought up from docker-compose.yml and a .env, with nothing
# reconfigured by the test. What earns its minute is the dependency on the
# deployed configuration: a token wrong in exactly one claim, a header that only
# exists because the proxy and ForwardedHeaders agree, a policy the application
# has to work under. A test host proves none of that.
#
# Its own compose bring-up rather than sharing the walk's, and now its own CI
# job as well. Sharing would be faster and would make a walk failure cascade
# into spurious failures here (§13.23); two independent bring-ups also show the
# artefact starting correctly twice. See scripts/compose-suite.sh for why the
# job split matters to the preflight.
set -euo pipefail

cd "$(dirname "$0")/.."
# shellcheck source=scripts/compose-suite.sh
source ./scripts/compose-suite.sh

compose_suite test:deployment "§7 against the deployed stack"
