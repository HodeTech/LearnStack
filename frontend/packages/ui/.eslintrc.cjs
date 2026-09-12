// The shared preset, applied to the package the design-system primitives land in.
// `apps/web` is linted by `next lint`; without this file nothing lints `packages/`, and the
// sanitised-HTML primitive that `Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml`
// governs is destined for exactly this package (ADR-0009 § Decision).
//
// `ESLINT_USE_FLAT_CONFIG=false` in the package script is what `next lint` does for apps/web:
// ESLint 9's CLI looks for `eslint.config.js` first and stops when it finds none. Both go away
// together in the flat-config migration apps/web/.eslintrc.cjs already carries a TODO for.
//
// `require.resolve` for the same reason apps/web needs it: a subpath export of a workspace
// package does not resolve through the legacy config resolver under pnpm's isolated layout.
module.exports = {
  root: true,
  extends: [require.resolve('@learnstack/config/eslint')],
  parserOptions: {
    project: ['./tsconfig.json'],
    tsconfigRootDir: __dirname,
  },
};
