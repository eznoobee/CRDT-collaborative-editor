#!/usr/bin/env bash
# Shared setup for the suites that run against the real Compose stack.
#
# Sourced, not executed. Two suites need identical preparation — a certificate
# for a routable address and the trust for exactly that certificate — and
# differ only in which npm script they then run:
#
#   scripts/walk.sh        §13.27's walk         → test:walk
#   scripts/deployment.sh  §7 against Compose    → test:deployment
#
# They are separate CI jobs on purpose. The phase preflight derives its expected
# job list from the workflow files, so a suite that has its own job is a suite
# the preflight can *require*; a suite folded into another job cannot be, and
# could stop running with nothing to notice it. That is register row 13's own
# failure mode, and it would have been reappearing inside the mechanism built to
# prevent it.
#
# NOT PREFLIGHT GATES, deliberately. They need a Docker daemon, which the
# sandbox this project is developed in does not have, and a gate that skips when
# its infrastructure is missing is a check that cannot fail (§13.19). The
# preflight refuses a phase report while any CI job is red, so the coverage is
# the same and the hole is not.

compose_suite() {
    local script=$1
    local label=$2

    if ! docker info >/dev/null 2>&1; then
        echo "FAILED: no Docker daemon. $label tests a deployment; there is nothing to test." >&2
        return 1
    fi

    # The address the stack is reached by. Not loopback: the API runs in a
    # container that cannot route to the host's loopback, and the browser and
    # the API have to agree on one absolute issuer URL (§4).
    local host
    host="$(hostname -I 2>/dev/null | awk '{print $1}')"
    if [[ -z "$host" ]]; then
        echo "FAILED: no routable IPv4 address; a container cannot reach this host." >&2
        return 1
    fi

    echo "==> Certificate for $host"
    ./scripts/dev-cert.sh deploy/tls "IP:$host"

    # Trust for exactly that certificate, and nothing else relaxed. Node reads
    # this at startup, which is why the certificate is made here rather than
    # inside the harness — the same rule as SSL_CERT_FILE for the API and the
    # SPKI pin for Chromium: name the certificate, never disable the check.
    export NODE_EXTRA_CA_CERTS="$PWD/deploy/tls/cert.pem"
    export WALK_HOST="$host"

    echo "==> $label"
    cd client
    [[ -d node_modules ]] || npm ci --silent
    npm run --silent "$script"
}
