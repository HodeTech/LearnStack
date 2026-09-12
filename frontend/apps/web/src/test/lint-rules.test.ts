import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// ESLint 9's legacy API: `.eslintrc.cjs` is this app's configuration until the flat-config
// migration that file's TODO names lands, and the flat `ESLint` class cannot read one.
import { LegacyESLint } from 'eslint/use-at-your-own-risk';
import { describe, expect, it } from 'vitest';

/**
 * The companion to the lint rules the architecture-test catalogue names — run, not assumed.
 *
 * `pnpm lint` is green whether a rule is configured or not: a rule nothing violates and a
 * rule that does not exist look identical from the outside. These cases lint a source
 * string through the app's own configuration and assert which rule answers, so the day a
 * preset is reordered, a selector stops matching, or an extends list drops the preset, this
 * goes red instead of the app quietly losing a guard.
 */
describe('the lint rules the architecture-test catalogue names', () => {
  const app = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

  const lint = async (code: string) => {
    const eslint = new LegacyESLint({
      cwd: app,
      // pnpm's isolated layout puts the plugins beside the preset that declares them,
      // which is the package `.eslintrc.cjs` extends. `next lint` resolves them the same
      // way; without this the preset fails to load and nothing is checked at all.
      resolvePluginsRelativeTo: resolve(app, '..', '..', 'packages', 'config', 'eslint'),
      // The fixtures below are strings rather than files, so they belong to no tsconfig
      // project and the type-aware parser refuses them outright. Both rules under test are
      // syntactic; turning the project off is what lets them run at all.
      overrideConfig: { parserOptions: { project: null } },
    });

    const results = await eslint.lintText(code, { filePath: join(app, 'src', 'probe.tsx') });

    // One result per linted text, and an empty array means the file was ignored rather than
    // clean — which is how this companion could pass while checking nothing.
    expect(results).toHaveLength(1);

    return results[0]!.messages.map((message) => message.ruleId ?? `parse: ${message.message}`);
  };

  it('Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml refuses the JSX attribute', async () => {
    // architecture/32 § 8.5: the sanitised-HTML primitive is the only component allowed to
    // call it, and only on the sanitiser's output. Nothing in the app calls it today, which
    // is why the rule lands before the first caller rather than after it.
    const rules = await lint(
      'export const Block = ({ html }: { html: string }) => <div dangerouslySetInnerHTML={{ __html: html }} />;\n',
    );

    expect(rules).toContain('no-restricted-syntax');
  });

  it('refuses it in an object passed to createElement too', async () => {
    // The attribute is the common spelling; the property is the one a helper writes.
    const rules = await lint(
      'import { createElement } from "react";\n' +
        'export const Block = (html: string) =>\n' +
        '  createElement("div", { dangerouslySetInnerHTML: { __html: html } });\n',
    );

    expect(rules).toContain('no-restricted-syntax');
  });

  it('leaves an ordinary component alone', async () => {
    // The control: without it, a rule that flagged everything would pass the two above.
    const rules = await lint(
      'export const Block = ({ text }: { text: string }) => <p>{text}</p>;\n',
    );

    expect(rules).toEqual([]);
  });

  it('bars a direct fetch, which Standards 03 § Forbidden names', async () => {
    // The other rule in the same preset, checked for the same reason: green proves nothing
    // unless something has been shown to fail.
    const rules = await lint('export const load = async () => fetch("/api/v1/health");\n');

    expect(rules).toContain('no-restricted-globals');
  });
});
