import { defineConfig } from 'vitest/config';

/**
 * The interop suite: the TypeScript core against a real running C# server.
 *
 * Separate from `vite.config.ts` because these tests start processes and open
 * sockets. Node rather than jsdom — the harness spawns the API and serves OIDC
 * metadata over TLS, none of which a browser environment can do — and no
 * parallelism, because every test in the file shares one server.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/interop/**/*.interop.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    testTimeout: 60_000,
    hookTimeout: 120_000,
  },
});
