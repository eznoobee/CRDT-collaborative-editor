import { defineConfig } from 'vitest/config';

/**
 * The end-to-end suite: a real browser against the real application (§11).
 *
 * Separate from the interop config because this one also builds the client and
 * drives Chromium, which is slower and needs a browser present. Node, not
 * jsdom: the environment under test is the browser Playwright launches, and a
 * DOM in the test process would only be a second, wrong one.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/e2e/**/*.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    testTimeout: 120_000,
    hookTimeout: 240_000,
  },
});
