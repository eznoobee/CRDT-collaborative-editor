import { defineConfig } from 'vitest/config';

/**
 * §9's offline-window discard against the Compose stack (register row 31).
 *
 * Its own config and its own CI job, for the reason `compose-suite.sh` gives:
 * the phase preflight derives its expected job list from the workflow files, so
 * a suite with its own job is one the preflight can *require*. A suite folded
 * into another job cannot be, and could stop running with nothing to notice.
 *
 * Separate from the walk because it brings up a different stack — §5's
 * `T_retire` shortened to seconds — and a stack that retires a replica after
 * fifteen seconds would retire the walk's own tabs between its steps.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/offline/offlineWindow.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    sequence: { concurrent: false },
    // The test waits out T_retire plus a sweep in real time, and the hook
    // builds two images and brings up five services.
    testTimeout: 900_000,
    hookTimeout: 900_000,
  },
});
