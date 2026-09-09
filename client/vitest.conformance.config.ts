import { defineConfig } from 'vitest/config';

/**
 * The conformance corpus (PROJECT_SPEC.md §9).
 *
 * Separate from the default suite because this one is not self-contained: §9's
 * generated corpus is materialised by the C# runner from the committed seed,
 * and replaying it requires that runner to have gone first. A test whose input
 * another process produces is not a unit test, and leaving it in `npm test`
 * made the client job depend on an artefact nothing in that job creates.
 *
 * `scripts/conformance.sh` runs the two in the right order.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/crdt/conformance.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    testTimeout: 120_000,
  },
});
