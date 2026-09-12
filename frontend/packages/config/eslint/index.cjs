/**
 * Shared ESLint preset. Standards 03 § Language Settings drives the strict rules below.
 * Extending app overrides Next.js-specific rules in apps/web/.eslintrc.cjs.
 */

/** One message for every spelling of the same violation. */
const DANGEROUS_HTML =
  'Only the sanitised-HTML primitive may call dangerouslySetInnerHTML, and only on the ' +
  'sanitiser output (architecture/32 § 8.5). Render through a primitive. To inspect or ' +
  'strip the prop, disable this rule on the line with a reason.';

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
      // `ignoreRestSiblings` is what makes `const { secret, ...safe } = props` writable: the
      // binding is unused ON PURPOSE — omitting it from the spread is the whole point. It is
      // not this rule's default here, so without the flag the idiom that strips a dangerous
      // prop before forwarding the rest is itself an error, and the guard gets deleted rather
      // than written.
      { argsIgnorePattern: '^_', varsIgnorePattern: '^_', ignoreRestSiblings: true },
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
    // primitive's own file carries the single sanctioned suppression when Phase 05 ships
    // the `embed-html` sanitisation contract; until then nothing calls it at all, which is
    // why the rule lands now: the first call arrives as a red build rather than as a
    // review someone has to catch.
    'no-restricted-syntax': [
      'error',
      // Four shapes, chosen so the rule refuses the ways the prop REACHES an element and
      // leaves alone the ways code inspects or strips it. Measured against the real
      // configuration; the companion in apps/web lints each of them.
      {
        // The component's spelling.
        selector: 'JSXAttribute[name.name="dangerouslySetInnerHTML"]',
        message: DANGEROUS_HTML,
      },
      {
        // The helper's spelling — an object being BUILT. Scoped to `ObjectExpression` on
        // purpose: an `ObjectPattern` is the same node type, so an unscoped selector also
        // flagged `const { dangerouslySetInnerHTML, ...safe } = props`, which is the idiom
        // that guarantees the prop is not forwarded. Refusing the safe spelling teaches
        // people to delete the guard.
        selector: 'ObjectExpression > Property[key.name="dangerouslySetInnerHTML"]',
        message: DANGEROUS_HTML,
      },
      {
        // The assignment onto a props object that is spread afterwards. Scoped to the
        // assignment's left-hand side, because the unscoped form also refused
        // `if (props.dangerouslySetInnerHTML)` — a read that reaches nothing.
        selector:
          'AssignmentExpression > MemberExpression.left[property.name="dangerouslySetInnerHTML"]',
        message: DANGEROUS_HTML,
      },
      {
        // And the name itself, wherever it is written as a string. A selector keyed on an
        // identifier cannot see through a variable: `const k = 'dangerouslySetInnerHTML'`
        // then `{ [k]: … }` defeated all three above and rendered unsanitised HTML, which
        // is not an exotic bypass but what a generic field renderer looks like. This arm
        // covers the quoted key, the computed key, `props["…"]`, and the variable that
        // carries the name. What is left is a name assembled from fragments at runtime —
        // unambiguous obfuscation rather than an ordinary pattern.
        //
        // It is the one arm that can refuse code doing nothing wrong: a quoted destructure
        // or a string used in a message. Those take a line disable with a reason, exactly
        // as the `fetch` ban below expects.
        selector: 'Literal[value="dangerouslySetInnerHTML"]',
        message: DANGEROUS_HTML,
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
