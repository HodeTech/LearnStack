// @vitest-environment node
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { demoIdentityFindings } from './demo-genericity-analysis';
import { omitDemoSourceDirectory, productionDemoSources } from './demo-genericity-sources';
import vocabulary from './fixtures/demo-identities.json';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../..');
function productionSources(): Map<string, string> {
  const files = new Map<string, string>();
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      const name = relative(root, path).replaceAll('\\', '/');
      if (entry.isSymbolicLink() && entry.name !== 'node_modules')
        throw new Error(`Unmodeled production symlink: ${name}`);
      if (entry.isDirectory()) {
        if (!omitDemoSourceDirectory(name)) visit(path);
      } else if (
        /\.(?:[cm]?[jt]sx?|json)$/.test(entry.name) &&
        !/\.d\.[cm]?[jt]sx?$/.test(entry.name)
      ) {
        files.set(name, readFileSync(path, 'utf8'));
      }
    }
  };
  visit(join(root, 'frontend'));
  visit(join(root, 'scripts'));
  return productionDemoSources(files);
}

describe('P02d-7 exact demo identity boundary', () => {
  it('Production_Code_Does_Not_Branch_On_Demo_Tenant_Literals', () => {
    const files = productionSources();
    expect(files.size).toBeGreaterThan(50);
    for (const path of [
      'frontend/apps/web/src/middleware.ts',
      'frontend/apps/web/src/server/ingress.ts',
      'frontend/apps/web/src/server/configured-public-client.ts',
      'frontend/apps/web/src/components/public/lesson.tsx',
      'frontend/apps/web/src/i18n/messages/en/public.json',
      'frontend/packages/sdk/src/server.ts',
      'frontend/apps/web/scripts/public-server.mjs',
      'frontend/apps/web/scripts/development-hmr.mjs',
      'frontend/apps/web/scripts/stop-test-child.mjs',
      'scripts/demo.mjs',
    ])
      expect(files.has(path), `Production census includes ${path}`).toBe(true);
    expect(
      [...files].flatMap(([path, source]) =>
        demoIdentityFindings(path, source, vocabulary).map((value) => `${path}: ${value}`),
      ),
    ).toEqual([]);
  });

  const identity = vocabulary.values.find((row) => row.kind === 'tenant-name')!.value;
  const id = vocabulary.values.find((row) => row.kind === 'id')!.value;
  it.each([
    'src/components/public/lesson.tsx',
    'src/server/configured-public-client.ts',
    'packages/ui/src/helper.ts',
    'src/server/native.mjs',
    'src/server/legacy.js',
  ])('refuses a planted branch in %s', (path) => {
    expect(
      demoIdentityFindings(
        path,
        `if (name === ${JSON.stringify(identity)}) specialize();`,
        vocabulary,
      ),
    ).toEqual([identity]);
    expect(
      demoIdentityFindings(
        path,
        `// ${identity}\nif (name === 'independent') specialize();`,
        vocabulary,
      ),
    ).toEqual([]);
  });
  it('refuses decoded literals, static templates, JSON values and alternate UUID spellings', () => {
    const escaped = `\\u${identity.charCodeAt(0).toString(16).padStart(4, '0')}${identity.slice(1)}`;
    expect(demoIdentityFindings('probe.ts', `const x = '${escaped}';`, vocabulary)).toEqual([
      identity,
    ]);
    expect(demoIdentityFindings('probe.ts', `const x = \`${identity}\`;`, vocabulary)).toEqual([
      identity,
    ]);
    const parts = id.split('-');
    const bytes = (parts[3]! + parts[4]!).match(/../g)!;
    const hex = `{0x${parts[0]},0x${parts[1]},0x${parts[2]},{${bytes.map((byte) => `0x${byte}`).join(',')}}}`;
    for (const value of [
      id,
      id.toUpperCase(),
      id.replaceAll('-', ''),
      `{${id}}`,
      `(${id})`,
      hex,
      ` ${id} `,
    ])
      expect(demoIdentityFindings('probe.ts', `const x = '${value}';`, vocabulary)).toEqual([
        value,
      ]);
    expect(
      demoIdentityFindings('messages.json', JSON.stringify({ title: identity }), vocabulary),
    ).toEqual([identity]);
    expect(
      demoIdentityFindings(
        'probe.ts',
        `const x = ${JSON.stringify(JSON.stringify({ title: identity }))};`,
        vocabulary,
      ),
    ).toEqual([identity]);
  });
  it('preserves canonical platform values and ordinal complete-value matching', () => {
    for (const value of vocabulary.platformValues)
      expect(
        demoIdentityFindings('probe.ts', `const x = ${JSON.stringify(value)};`, vocabulary),
      ).toEqual([]);
    for (const value of [identity.toUpperCase(), `${identity} suffix`, `prefix ${identity}`])
      expect(
        demoIdentityFindings('probe.ts', `const x = ${JSON.stringify(value)};`, vocabulary),
      ).toEqual([]);
  });
  it('reads emitted JSX text, attributes and entities without decoding ordinary strings', () => {
    for (const body of [
      `<h1>${identity}</h1>`,
      `<h1 title="${identity.replace(' ', '&#32;')}">generic</h1>`,
      `<h1 title="${identity.replace(' ', '&#x20;')}">generic</h1>`,
      `<h1>\n  ${identity}\n</h1>`,
    ])
      expect(demoIdentityFindings('probe.tsx', `const view = ${body};`, vocabulary)).toEqual([
        identity,
      ]);
    expect(
      demoIdentityFindings('probe.tsx', 'const view = <h1>independent</h1>;', vocabulary),
    ).toEqual([]);
    expect(
      demoIdentityFindings(
        'probe.tsx',
        `const value = "${identity.replace(' ', '&#32;')}";`,
        vocabulary,
      ),
    ).toEqual([]);
  });
  it('keeps nested runtime fixture helpers and follows runtime imports into test exclusions', () => {
    const entry = 'frontend/apps/web/scripts/public-server.mjs';
    for (const helper of [
      'fixtures/select-theme.mjs',
      'test/select-theme.mjs',
      'select-theme.test.mjs',
      '../src/test/select-theme.ts',
      '../src/dist/select-theme.ts',
      'coverage/select-theme.mjs',
      'verify-public-rendering.mjs',
    ]) {
      const target = relative(root, resolve(root, dirname(entry), helper)).replaceAll('\\', '/');
      const files = productionDemoSources(
        new Map([
          [entry, `import { special } from './${helper}';`],
          [target, `export const special = name => name === ${JSON.stringify(identity)};`],
          [
            'frontend/apps/web/src/test/unrelated.test.ts',
            `const fixture = ${JSON.stringify(identity)};`,
          ],
        ]),
      );
      expect(files.has(entry)).toBe(true);
      expect(files.has(target)).toBe(true);
      expect(files.has('frontend/apps/web/src/test/unrelated.test.ts')).toBe(false);
      expect(
        [...files].flatMap(([path, source]) => demoIdentityFindings(path, source, vocabulary)),
      ).toEqual([identity]);
    }
  });
  it('resolves Node module URL suffixes and refuses computed runtime imports', () => {
    const entry = 'frontend/apps/web/scripts/public-server.mjs';
    const target = 'frontend/apps/web/src/test/special.mjs';
    for (const suffix of ['?runtime=1', '#runtime', '?a=1#runtime']) {
      const files = productionDemoSources(
        new Map([
          [entry, `import '../src/test/special.mjs${suffix}';`],
          [target, `export const special = ${JSON.stringify(identity)};`],
        ]),
      );
      expect(files.has(target)).toBe(true);
    }
    const resolved = productionDemoSources(
      new Map([
        [entry, "require(require.resolve('../src/test/special.mjs'));"],
        [target, `export const special = ${JSON.stringify(identity)};`],
      ]),
    );
    expect(resolved.has(target)).toBe(true);
    expect(() =>
      productionDemoSources(
        new Map([[entry, "require(require.resolve('eslint-plugin-jsx-a11y', { paths: [] }));"]]),
      ),
    ).not.toThrow();
    for (const source of [
      "const path = './special.test.mjs'; await import(path);",
      "const path = './special.test.mjs'; require(path);",
      "const path = './special.test.mjs'; require(require.resolve(path));",
      "await import('file:///outside/helper.mjs');",
    ])
      expect(() => productionDemoSources(new Map([[entry, source]]))).toThrow(
        'Unmodeled runtime import',
      );
  });
  it('omits only known generated roots in the actual filesystem collector', () => {
    for (const path of [
      'frontend/apps/web/.next',
      'frontend/packages/sdk/dist',
      'frontend/node_modules',
    ])
      expect(omitDemoSourceDirectory(path)).toBe(true);
    for (const path of [
      'frontend/apps/web/src/dist',
      'frontend/apps/web/scripts/coverage',
      'frontend/apps/web/src/.server',
    ])
      expect(omitDemoSourceDirectory(path)).toBe(false);
  });
  it('fails loudly on unreadable sources and an empty or malformed vocabulary', () => {
    expect(() => demoIdentityFindings('probe.ts', 'const = ;', vocabulary)).toThrow('Unreadable');
    expect(() => demoIdentityFindings('probe.json', '{', vocabulary)).toThrow();
    expect(() => demoIdentityFindings('probe.py', 'pass', vocabulary)).toThrow('Unsupported');
    expect(() => demoIdentityFindings('probe.ts', '', { ...vocabulary, values: [] })).toThrow(
      'empty',
    );
    expect(() =>
      demoIdentityFindings('probe.ts', '', {
        ...vocabulary,
        values: [{ kind: 'id', value: 'bad' }],
      }),
    ).toThrow('malformed');
  });
});
