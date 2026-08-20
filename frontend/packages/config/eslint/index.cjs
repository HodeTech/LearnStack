/**
 * Shared ESLint preset. Standards 03 § Language Settings drives the strict rules below.
 * Extending app overrides Next.js-specific rules in apps/web/.eslintrc.cjs.
 */
module.exports = {
  root: false,
  parser: '@typescript-eslint/parser',
  parserOptions: {
    ecmaVersion: 2022,
    sourceType: 'module',
    ecmaFeatures: { jsx: true },
  },
  plugins: ['@typescript-eslint', 'import'],
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:import/recommended',
    'plugin:import/typescript',
    'prettier',
  ],
  settings: {
    'import/resolver': {
      typescript: { project: ['./tsconfig.json'] },
    },
  },
  rules: {
    '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
    '@typescript-eslint/no-unused-vars': [
      'error',
      { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
    ],
    '@typescript-eslint/no-explicit-any': 'error',
    'import/no-cycle': 'error',
    'import/order': [
      'warn',
      {
        groups: ['builtin', 'external', 'internal', ['parent', 'sibling', 'index']],
        'newlines-between': 'always',
        alphabetize: { order: 'asc', caseInsensitive: true },
      },
    ],
    'no-console': ['warn', { allow: ['warn', 'error'] }],

    // Standards 03 § Forbidden bars direct `fetch`, and architecture/14 names
    // the SDK as the only sanctioned way to reach the API. This is the rule
    // that makes those true. It is `no-restricted-globals` and not
    // `no-restricted-imports`, which the corpus used to name: `fetch` is a
    // global, so an import rule could never have caught a single call.
    'no-restricted-globals': [
      'error',
      {
        name: 'fetch',
        message:
          'Call the API through @learnstack/sdk (Standards 03 § Forbidden). For a ' +
          'non-API request, disable this rule on the line with a reason.',
      },
    ],
  },
};
