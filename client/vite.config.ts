import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'jsdom',
    include: ['src/**/*.{test,spec}.{ts,tsx}'],

    // The interop and end-to-end suites need a running server, a running
    // Postgres, a running Redis — and, for the browser tests, a built client
    // and a Chromium — so neither is part of the default run — a suite that can
    // only pass under conditions the developer has not set up is a suite that
    // gets ignored. `npm run test:interop` runs it, and CI runs it as its own
    // step where the infrastructure exists.
    exclude: ['**/node_modules/**', '**/dist/**', 'src/interop/**', 'src/e2e/**', 'src/walk/**'],
    // PROJECT_SPEC.md §11: an empty suite passing proves nothing, and vitest
    // exits non-zero with no test files unless told otherwise. That default is
    // deliberately left alone.
    passWithNoTests: false,
  },
});
