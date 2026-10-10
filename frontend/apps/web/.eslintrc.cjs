// TODO(2026-05-19, @platform, phase-02a): migrate to ESLint flat config
// (`eslint.config.mjs`). `next lint` is deprecated in Next 15.5+ and removed
// in Next 16 — the codemod is `npx @next/codemod@canary next-lint-to-eslint-cli .`
// Flat config also lets us reference shared presets without `require.resolve`.

// `require.resolve` is needed because legacy `.eslintrc.cjs` extends resolution
// only handles bare package names + the `eslint-config-*` convention; subpath
// exports from a workspace package (`@learnstack/config/eslint`) do not
// resolve through ESLint's built-in resolver under pnpm-isolated layouts.
// Next already installs this plugin. Resolve it beside eslint-config-next so
// pnpm's isolated dependency layout does not require a duplicate app dependency.
// eslint-disable-next-line @typescript-eslint/no-require-imports -- This legacy CommonJS config must load the plugin's rule map.
const jsxA11yRecommended = require(
  require.resolve('eslint-plugin-jsx-a11y', { paths: [require.resolve('eslint-config-next')] }),
).configs.recommended.rules;

// Next enables a small JSX accessibility subset as warnings. Apply every active
// recommended rule at blocking severity while retaining the preset's options.
const jsxA11yErrors = Object.fromEntries(
  Object.entries(jsxA11yRecommended)
    .filter(([, setting]) => (Array.isArray(setting) ? setting[0] : setting) !== 'off')
    .filter(([, setting]) => (Array.isArray(setting) ? setting[0] : setting) !== 0)
    .map(([name, setting]) => [
      name,
      Array.isArray(setting) ? ['error', ...setting.slice(1)] : 'error',
    ]),
);

module.exports = {
  root: true,
  extends: [require.resolve('@learnstack/config/eslint'), 'next/core-web-vitals'],
  rules: {
    ...jsxA11yErrors,
    // Next's inherited options narrow this rule to img. Restore every native
    // recommended element while retaining its Next Image component mapping.
    'jsx-a11y/alt-text': [
      'error',
      { elements: ['img', 'object', 'area', 'input[type="image"]'], img: ['Image'] },
    ],
  },
  parserOptions: {
    project: ['./tsconfig.json'],
    tsconfigRootDir: __dirname,
  },
  ignorePatterns: ['.next/', '.server/', 'next-env.d.ts'],
  overrides: [
    {
      // Native ESM/CJS TypeScript helpers are outside the Next TS program.
      // Parse their syntax explicitly, without relying on parser leniency.
      files: ['**/*.mts', '**/*.cts'],
      parserOptions: { project: null },
      parser: require.resolve('@typescript-eslint/parser', {
        paths: [require.resolve('@learnstack/config/eslint')],
      }),
    },
    {
      // ADR-0053: this is the sole configured API transport; pages use its SDK.
      files: ['src/server/configured-public-client.ts'],
      rules: { 'no-restricted-globals': 'off' },
    },
    {
      files: ['**/*.mjs', '**/*.cjs'],
      env: { node: true, es2022: true },
      // Native Node launch/test scripts are JavaScript, outside the TS program.
      // Keep the ordinary security/import rules; only the TS type-import rule
      // requires a parser service these files cannot supply.
      parserOptions: { project: null },
      rules: { '@typescript-eslint/consistent-type-imports': 'off' },
    },
  ],
};
