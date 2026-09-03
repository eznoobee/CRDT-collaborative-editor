import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'jsdom',
    include: ['src/**/*.{test,spec}.{ts,tsx}'],

    // The interop suite needs a running server, a running Postgres and a
    // running Redis, so it is not part of the default run — a suite that can
    // only pass under conditions the developer has not set up is a suite that
    // gets ignored. `npm run test:interop` runs it, and CI runs it as its own
    // step where the infrastructure exists.
    exclude: ['**/node_modules/**', '**/dist/**', 'src/interop/**'],
    // PROJECT_SPEC.md §11: an empty suite passing proves nothing, and vitest
    // exits non-zero with no test files unless told otherwise. That default is
    // deliberately left alone.
    passWithNoTests: false,
  },
});
