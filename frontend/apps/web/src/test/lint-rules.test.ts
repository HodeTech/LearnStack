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
  const ui = resolve(app, '..', '..', 'packages', 'ui');

  const lint = async (code: string, pkg: string = app) => {
    const eslint = new LegacyESLint({
      cwd: pkg,
      // pnpm's isolated layout puts the plugins beside the preset that declares them,
      // which is the package `.eslintrc.cjs` extends. `next lint` resolves them the same
      // way; without this the preset fails to load and nothing is checked at all.
      resolvePluginsRelativeTo: resolve(app, '..', '..', 'packages', 'config', 'eslint'),
      // The fixtures below are strings rather than files, so they belong to no tsconfig
      // project and the type-aware parser refuses them outright. Both rules under test are
      // syntactic; turning the project off is what lets them run at all.
      overrideConfig: { parserOptions: { project: null } },
    });

    const results = await eslint.lintText(code, { filePath: join(pkg, 'src', 'probe.tsx') });

    // One result per linted text, and an empty array means the file was ignored rather than
    // clean — which is how this companion could pass while checking nothing.
    expect(results).toHaveLength(1);

    // Severity travels with the rule id. A rule downgraded to `'warn'` still reports, so a
    // companion that only collected ids would stay green while `pnpm lint` exited 0 and the
    // build shipped the call it is supposed to refuse.
    return results[0]!.messages.map((message) =>
      message.ruleId ? `${message.ruleId}:${message.severity}` : `parse: ${message.message}`,
    );
  };

  // What a blocking violation of each rule looks like: reported, at error severity.
  const RESTRICTED_SYNTAX = 'no-restricted-syntax:2';

  it('Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml refuses the JSX attribute', async () => {
    // architecture/32 § 8.5: the sanitised-HTML primitive is the only component allowed to
    // call it, and only on the sanitiser's output. Nothing in the app calls it today, which
    // is why the rule lands before the first caller rather than after it.
    const rules = await lint(
      'export const Block = ({ html }: { html: string }) => <div dangerouslySetInnerHTML={{ __html: html }} />;\n',
    );

    expect(rules).toContain(RESTRICTED_SYNTAX);
  });

  it('refuses it in an object passed to createElement too', async () => {
    // The attribute is the common spelling; the property is the one a helper writes.
    const rules = await lint(
      'import { createElement } from "react";\n' +
        'export const Block = (html: string) =>\n' +
        '  createElement("div", { dangerouslySetInnerHTML: { __html: html } });\n',
    );

    expect(rules).toContain(RESTRICTED_SYNTAX);
  });

  it('refuses the quoted key, which is the same object written differently', async () => {
    // `{ 'dangerouslySetInnerHTML': … }` parses to a Literal key, not an Identifier one, so a
    // selector written only for `key.name` reads this object as clean.
    const rules = await lint(
      'import { createElement } from "react";\n' +
        'export const Block = (html: string) =>\n' +
        '  createElement("div", { "dangerouslySetInnerHTML": { __html: html } });\n',
    );

    expect(rules).toContain(RESTRICTED_SYNTAX);
  });

  it('refuses the key held in a variable, which no identifier selector can see', async () => {
    // The shape a generic field renderer takes, and the one that defeated every selector
    // keyed on an identifier: the property name never appears at the construction site.
    // What catches it is the name itself, wherever it is written as a string.
    const rules = await lint(
      "const key = 'dangerouslySetInnerHTML';\n" +
        'export const Block = (html: string) => {\n' +
        '  const props: Record<string, unknown> = { [key]: { __html: html } };\n' +
        '  return <div {...props} />;\n' +
        '};\n',
    );

    expect(rules).toContain(RESTRICTED_SYNTAX);
  });

  it('refuses the property assigned onto a props object before it is spread', async () => {
    // The spelling that reaches the element through neither an attribute nor a literal: build
    // the props, assign the key, spread. Both halves of it — the dotted assignment and the
    // computed one — are the same call with the same consequence.
    const dotted = await lint(
      'export const Block = (html: string) => {\n' +
        '  const props: Record<string, unknown> = {};\n' +
        '  props.dangerouslySetInnerHTML = { __html: html };\n' +
        '  return <div {...props} />;\n' +
        '};\n',
    );

    expect(dotted).toContain(RESTRICTED_SYNTAX);

    const computed = await lint(
      'export const Block = (html: string) => {\n' +
        '  const props: Record<string, unknown> = {};\n' +
        '  props["dangerouslySetInnerHTML"] = { __html: html };\n' +
        '  return <div {...props} />;\n' +
        '};\n',
    );

    expect(computed).toContain(RESTRICTED_SYNTAX);
  });

  it('leaves an ordinary component alone', async () => {
    // The control: without it, a rule that flagged everything would pass every case above.
    // It carries a REAL attribute, because the first version of this fixture was `<p>{text}</p>`
    // — no JSX attribute at all — and a selector broadened from
    // `JSXAttribute[name.name="…"]` to bare `JSXAttribute`, which flags every prop in the app,
    // left the whole file green.
    const rules = await lint(
      'export const Block = ({ text }: { text: string }) => <p className="lede">{text}</p>;\n',
    );

    expect(rules).toEqual([]);
  });

  it('leaves a read of the prop alone, because a read reaches no element', async () => {
    // The rule refuses the ways the prop REACHES an element. Asking whether it was supplied
    // renders nothing, and an unqualified member selector refused it — which costs a
    // suppression on code doing nothing wrong and teaches people to disable the rule.
    const rules = await lint(
      'export const supplied = (props: { dangerouslySetInnerHTML?: unknown }) =>\n' +
        '  Boolean(props.dangerouslySetInnerHTML);\n',
    );

    expect(rules).toEqual([]);
  });

  it('leaves the strip-before-spread idiom alone, which is the safe one', async () => {
    // `const { dangerouslySetInnerHTML, ...safe } = props` is how a component GUARANTEES the
    // prop is not forwarded. ESTree gives a destructuring binding the same `Property` node as
    // a constructed key, so the first selector refused the safe spelling along with the
    // dangerous one — and `no-unused-vars` refused it a second time, since the binding is
    // deliberately unused. A guard that does not compile is a guard nobody writes.
    const rules = await lint(
      'export const Block = (props: Record<string, unknown>) => {\n' +
        '  const { dangerouslySetInnerHTML, ...safe } = props;\n' +
        '  return <div {...safe} />;\n' +
        '};\n',
    );

    expect(rules).toEqual([]);
  });

  it('answers in packages/ui too, where the primitive will live', async () => {
    // `pnpm lint` walks every package that declares the script, and until Packet 10 only
    // apps/web did — so the package ADR-0009 § Decision sends an extracted primitive to was linted by
    // nothing at all. The rule is worth least in the app and most here.
    const rules = await lint(
      'export const Block = ({ html }: { html: string }) => <div dangerouslySetInnerHTML={{ __html: html }} />;\n',
      ui,
    );

    expect(rules).toContain(RESTRICTED_SYNTAX);
  });

  it('bars a direct fetch, which Standards 03 § Forbidden names', async () => {
    // The other rule in the same preset, checked for the same reason: green proves nothing
    // unless something has been shown to fail.
    const rules = await lint('export const load = async () => fetch("/api/v1/health");\n');

    expect(rules).toContain('no-restricted-globals:2');
  });
});
