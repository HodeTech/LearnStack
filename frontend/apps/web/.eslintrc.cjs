// TODO(2026-05-19, @platform, phase-02a): migrate to ESLint flat config
// (`eslint.config.mjs`). `next lint` is deprecated in Next 15.5+ and removed
// in Next 16 — the codemod is `npx @next/codemod@canary next-lint-to-eslint-cli .`
// Flat config also lets us reference shared presets without `require.resolve`.

// `require.resolve` is needed because legacy `.eslintrc.cjs` extends resolution
// only handles bare package names + the `eslint-config-*` convention; subpath
// exports from a workspace package (`@learnstack/config/eslint`) do not
// resolve through ESLint's built-in resolver under pnpm-isolated layouts.
module.exports = {
  root: true,
  extends: [require.resolve('@learnstack/config/eslint'), 'next/core-web-vitals'],
  parserOptions: {
    project: ['./tsconfig.json'],
    tsconfigRootDir: __dirname,
  },
  overrides: [
    {
      // ADR-0053: this is the sole configured API transport; pages use its SDK.
      files: ['src/server/configured-public-client.ts'],
      rules: { 'no-restricted-globals': 'off' },
    },
    {
      files: ['scripts/**/*.mjs', '.eslintrc.cjs'],
      env: { node: true, es2022: true },
      // Native Node launch/test scripts are JavaScript, outside the TS program.
      // Keep the ordinary security/import rules; only the TS type-import rule
      // requires a parser service these files cannot supply.
      parserOptions: { project: null },
      rules: { '@typescript-eslint/consistent-type-imports': 'off' },
    },
  ],
};
