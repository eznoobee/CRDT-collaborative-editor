#!/usr/bin/env bash
# §13.27's walk against the real Compose stack (PROJECT_SPEC.md §11, Phase 5b).
#
# Brings the artefact up the way a deployment does and follows what a user does
# from a cold start, with nothing seeded and nothing run by hand. The step it
# stops at is the output: a green walk with no recorded stopping point is either
# a finished product or a walk trimmed to what passes.
#
# NOT A PREFLIGHT GATE, deliberately. It needs a Docker daemon, which the
# sandbox this project is developed in does not have, and a gate that skips when
# its infrastructure is missing is a check that cannot fail — the exact shape
# §13.19 is about. It runs in CI as its own job instead, and the preflight
# already refuses a phase report while any CI job is red, so the coverage is the
# same and the hole is not.
set -euo pipefail

cd "$(dirname "$0")/.."

if ! docker info >/dev/null 2>&1; then
    echo "FAILED: no Docker daemon. The walk tests a deployment; there is nothing to test." >&2
    exit 1
fi

# The address the stack is reached by. Not loopback: the API runs in a container
# that cannot route to the host's loopback, and the browser and the API have to
# agree on one absolute issuer URL (§4).
host="$(hostname -I 2>/dev/null | awk '{print $1}')"
if [[ -z "$host" ]]; then
    echo "FAILED: no routable IPv4 address; a container cannot reach this host." >&2
    exit 1
fi

echo "==> Certificate for $host"
./scripts/dev-cert.sh deploy/tls "IP:$host"

# Trust for exactly that certificate, and nothing else relaxed. Node reads this
# at startup, which is why the certificate is made here rather than inside the
# harness — and it is the same rule as SSL_CERT_FILE for the API and the SPKI
# pin for Chromium: name the certificate, never disable the check.
export NODE_EXTRA_CA_CERTS="$PWD/deploy/tls/cert.pem"
export WALK_HOST="$host"

echo "==> Walking"
cd client
[[ -d node_modules ]] || npm ci --silent
npm run --silent test:walk
