#!/usr/bin/env bash
# §9's offline-window discard, in a browser, against the Compose stack.
#
# Register row 31. Same preparation as the walk — a certificate for a routable
# address and trust for exactly that certificate — and a different stack: this
# one shortens §5's T_retire so the window closes inside a test's lifetime.
set -euo pipefail

cd "$(dirname "$0")/.."
source scripts/compose-suite.sh

compose_suite test:offline "§9's offline-window discard"
