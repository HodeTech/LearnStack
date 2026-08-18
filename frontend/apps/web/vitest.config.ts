import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

/**
 * Per-app, not an export from `@learnstack/config`.
 *
 * `packages/config` declares no `scripts` block at all, so a shared vitest
 * export would be validated by nothing until a second consumer exists. Phase
 * 02d brings the operator-facing surfaces that would be that consumer; hoisting
 * it before then means maintaining a shared config no test run ever loads.
 */
export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
    restoreMocks: true,
  },
  resolve: {
    alias: {
      // Mirrors tsconfig.json's `"@/*": ["./src/*"]`. Vitest does not read
      // tsconfig paths on its own, so an import that typechecks would fail to
      // resolve at test time without this.
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
});
