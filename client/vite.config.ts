import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'jsdom',
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    // PROJECT_SPEC.md §11: an empty suite passing proves nothing, and vitest
    // exits non-zero with no test files unless told otherwise. That default is
    // deliberately left alone.
    passWithNoTests: false,
  },
});
