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

    // ADR-0018 § Renderer architecture and architecture/32 § 8.5: the sanitised-HTML
    // primitive is the ONE component allowed to call `dangerouslySetInnerHTML`, and it
    // does so on the sanitiser's output alone. This is
    // `Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml` — the rule that keeps
    // the sanitisation contract from being bypassed by a convenient one-off. The
    // primitive's own file carries the single sanctioned suppression when Phase 04 ships
    // it; until then nothing in the app calls it at all, which is why the rule lands now:
    // the first call arrives as a red build rather than as a review someone has to catch.
    'no-restricted-syntax': [
      'error',
      {
        selector: 'JSXAttribute[name.name="dangerouslySetInnerHTML"]',
        message:
          'Only the sanitised-HTML primitive may call dangerouslySetInnerHTML, and only ' +
          'on the sanitiser output (architecture/32 § 8.5). Render through a primitive.',
      },
      {
        // An identifier key — `{ dangerouslySetInnerHTML: … }` — and a quoted one, which is
        // the same object written differently.
        selector:
          'Property[key.name="dangerouslySetInnerHTML"], Property[key.value="dangerouslySetInnerHTML"]',
        message:
          'Only the sanitised-HTML primitive may call dangerouslySetInnerHTML, and only ' +
          'on the sanitiser output (architecture/32 § 8.5). Render through a primitive.',
      },
      {
        // And the assignment: `props.dangerouslySetInnerHTML = …` before a spread reaches the
        // element through neither an attribute nor a property literal.
        selector:
          'MemberExpression[property.name="dangerouslySetInnerHTML"], MemberExpression[property.value="dangerouslySetInnerHTML"]',
        message:
          'Only the sanitised-HTML primitive may call dangerouslySetInnerHTML, and only ' +
          'on the sanitiser output (architecture/32 § 8.5). Render through a primitive.',
      },
    ],

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
