import { defineConfig } from 'vitest/config';

/**
 * §13.27's walk against the real Compose stack (§11's Phase 5b).
 *
 * §7's deployment conformance has its own config and its own CI job; see
 * vitest.deployment.config.ts.
 *
 * Its own config because it is slower than everything else by an order of
 * magnitude — it builds two images and brings up five services — and because it
 * is the one suite that must not share infrastructure with any other: the whole
 * point is a cold start.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    // Named, not globbed. The glob also matched security.e2e.test.ts, which is
    // how §7's deployment suite came to run inside the walk's CI job — and a
    // suite inside another job is a suite the phase preflight cannot require.
    // walkConfigCoverage.test.ts fails if a file under src/walk is matched by
    // neither config or by both.
    include: ['src/walk/walk.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    sequence: { concurrent: false },
    testTimeout: 180_000,
    hookTimeout: 900_000,
  },
});
