#!/usr/bin/env bash
# §5's collection, observed through the product against the Compose stack.
#
# Register row 33. Same preparation as the walk, a different stack: this one
# retires, snapshots, collects and truncates on a few seconds rather than on the
# product's hours, so the consequence is visible inside a test.
set -euo pipefail

cd "$(dirname "$0")/.."
source scripts/compose-suite.sh

compose_suite test:gc "§5's collection, seen through the product"
