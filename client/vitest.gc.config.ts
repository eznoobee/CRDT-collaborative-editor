import { defineConfig } from 'vitest/config';

/**
 * §5's collection observed through the product (register row 33).
 *
 * Its own config and its own CI job, for the reason `compose-suite.sh` gives:
 * only a suite with a job of its own can be required by the phase preflight.
 *
 * Its own stack because collection needs every replica retired or up to date
 * before it can proceed, so this one retires in seconds — which would retire
 * the walk's own tabs between its steps.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/gc/collection.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    sequence: { concurrent: false },
    testTimeout: 900_000,
    hookTimeout: 900_000,
  },
});
