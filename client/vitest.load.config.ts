import { defineConfig } from 'vitest/config';

/**
 * §8's measurements that need a server process of their own.
 *
 * Separate from the interop config because these are not tests of behaviour and
 * must not run beside anything: a measurement sharing a machine with another
 * suite measures the other suite. They take minutes, they want the box, and
 * `scripts/load.sh` is the only thing that starts them.
 *
 * The connection target in particular cannot be measured in process with its
 * clients — §8 says "per instance at < 2 GB RSS", and RSS names a process — so
 * the server runs as its own child and its `/proc` entry is what is read.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/load/**/*.load.test.ts'],
    passWithNoTests: false,
    fileParallelism: false,
    testTimeout: 900_000,
    hookTimeout: 900_000,
  },
});
