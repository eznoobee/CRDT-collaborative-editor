#!/usr/bin/env bash
# The browser half of PROJECT_SPEC.md §8's document-load measurement.
#
# Reports, never asserts. §8 gives the browser figure no threshold in this phase
# because setting a bound before anyone has seen the number is how §8 acquired a
# 500 ms server-side target nothing had measured.
#
# §8 names the runner class as part of the number, so the figure that counts is
# the one CI produces on a standard ubuntu-latest runner. Running this locally
# tells you the shape of the answer, not the answer.
set -euo pipefail

cd "$(dirname "$0")/../tests/browser"

if [[ ! -d node_modules ]]; then
  # The sandbox that ships a Chromium also forbids downloading one; CI has
  # neither restriction and installs the browser in its own step.
  PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD="${PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD:-}" npm ci --silent \
    || npm install --silent
fi

node measure.mjs
