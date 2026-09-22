import { defineConfig } from 'vitest/config';

/**
 * §7 asked of the application as Compose starts it (§7, §11).
 *
 * Its own config, and its own CI job. The job matters as much as the config:
 * the phase preflight derives its expected job list from the workflow files, so
 * only a suite with a job of its own can be required by it. Folded into the
 * walk's job, this suite could stop running with nothing to notice — register
 * row 13's failure mode, reappearing inside the mechanism built to prevent it.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    // Named, for the same reason as the walk's: the two suites are separate CI
    // jobs, and a glob is how they stopped being separate the first time.
    include: ['src/walk/security.e2e.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    sequence: { concurrent: false },
    testTimeout: 180_000,
    hookTimeout: 900_000,
  },
});
