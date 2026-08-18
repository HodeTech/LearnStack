import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/**
 * Per-app, not an export from `@learnstack/config`.
 *
 * Not because a shared export could not work — `packages/config` already
 * exports `./eslint`, `./tsconfig/*` and `./tailwind`, and `apps/web` consuming
 * them is what validates those. The reason is that there is no second consumer
 * by design: ADR-0009 keeps one Next.js app in this repository, the operator
 * portal lives in `LearnStack-Hub`, and the architecture test
 * `Frontend_Has_Only_The_Web_App` (Implemented) fails the build if a second app
 * appears here. A shared config with exactly one consumer is indirection with
 * no payer.
 */
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    // Off, and the suite depends on it being off. Both test files import
    // describe/it/expect/afterEach from 'vitest' explicitly, so the
    // injected form is a knob nothing uses. It is also load-bearing in
    // reverse: with globals on, @testing-library/react detects a global
    // `afterEach` and registers its own cleanup, which silently makes
    // setup.ts's explicit afterEach(cleanup) redundant. Turning this back
    // on means adding "types": ["vitest/globals"] to tsconfig.json.
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    restoreMocks: true,
  },
  resolve: {
    alias: {
      // Mirrors tsconfig.json's `"@/*": ["./src/*"]`. Vitest does not read
      // tsconfig paths on its own, so an import that typechecks would fail to
      // resolve at test time without this.
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
});
