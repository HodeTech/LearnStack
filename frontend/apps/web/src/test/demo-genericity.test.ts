// @vitest-environment node
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { demoIdentityFindings } from './demo-genericity-analysis';
import vocabulary from './fixtures/demo-identities.json';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../..');
// These are test-owned proof/fixture implementations, not runtime directories.
const testScripts = new Set([
  'frontend/apps/web/scripts/fixture-support.mjs',
  'frontend/apps/web/scripts/public-rendering-assertions.mjs',
  'frontend/apps/web/scripts/stop-test-child.mjs',
  'frontend/apps/web/scripts/verify-ingress.mjs',
  'frontend/apps/web/scripts/verify-ingress-cleanup.mjs',
  'frontend/apps/web/scripts/verify-ingress-controls.mjs',
  'frontend/apps/web/scripts/verify-public-admission.mjs',
  'frontend/apps/web/scripts/verify-public-rendering.mjs',
  'frontend/apps/web/scripts/admission-browser.mjs',
  'frontend/apps/web/scripts/admission-completion.mjs',
  'frontend/apps/web/scripts/admission-hmr.mjs',
  'frontend/apps/web/scripts/admission-hmr-watch.mjs',
  'frontend/apps/web/scripts/development-hmr.mjs',
]);
const developmentData = new Set(['scripts/public-local.mjs']);

function productionSources(): Map<string, string> {
  const files = new Map<string, string>();
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      const name = relative(root, path).replaceAll('\\', '/');
      if (entry.isSymbolicLink() && entry.name !== 'node_modules')
        throw new Error(`Unmodeled production symlink: ${name}`);
      if (entry.isDirectory()) {
        if (
          ![
            'node_modules',
            '.next',
            '.server',
            'dist',
            'coverage',
            'test',
            '__tests__',
            '__fixtures__',
            'fixtures',
          ].includes(entry.name)
        )
          visit(path);
      } else if (
        /\.(?:[cm]?[jt]sx?|json)$/.test(entry.name) &&
        !/\.(?:test|spec|d)\.[cm]?[jt]sx?$/.test(entry.name) &&
        !testScripts.has(name) &&
        !developmentData.has(name)
      ) {
        files.set(name, readFileSync(path, 'utf8'));
      }
    }
  };
  visit(join(root, 'frontend'));
  visit(join(root, 'scripts'));
  return files;
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
    expect(testScripts.has('frontend/apps/web/scripts/public-server.mjs')).toBe(false);
    expect(developmentData.has('scripts/demo.mjs')).toBe(false);
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
