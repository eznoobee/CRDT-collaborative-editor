import { defineConfig } from 'vitest/config';

/**
 * §13.27's walk against the real Compose stack (§11's Phase 5b).
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
    include: ['src/walk/**/*.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    sequence: { concurrent: false },
    testTimeout: 180_000,
    hookTimeout: 900_000,
  },
});
