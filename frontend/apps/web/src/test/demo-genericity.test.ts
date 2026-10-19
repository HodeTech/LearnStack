// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { demoIdentityFindings } from './demo-genericity-analysis';
import {
  demoSourceClassification,
  omitDemoSourceDirectory,
  omitIgnoredDemoLocalFile,
  productionDemoSources,
} from './demo-genericity-sources';
import vocabulary from './fixtures/demo-identities.json';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../..');
function productionSources(): Map<string, string> {
  const inventory = execFileSync(
    'git',
    [
      'ls-files',
      '--cached',
      '--others',
      '--exclude-standard',
      '--exclude=.claude/worktrees/',
      '-z',
    ],
    { cwd: root, encoding: 'utf8' },
  )
    .split('\0')
    .filter(Boolean);
  for (const path of inventory) demoSourceClassification(path);
  const files = new Map<string, string>();
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      const name = relative(root, path).replaceAll('\\', '/');
      if (
        entry.isFile() &&
        omitIgnoredDemoLocalFile(name, (candidate) => {
          const result = spawnSync('git', ['check-ignore', '--quiet', '--', candidate], {
            cwd: root,
            stdio: 'ignore',
          });
          if (result.status !== 0 && result.status !== 1)
            throw new Error('Git ignored-local-file admission failed');
          return result.status === 0;
        })
      )
        continue;
      if (entry.isSymbolicLink() && !omitDemoSourceDirectory(name))
        throw new Error(`Unmodeled production symlink: ${name}`);
      if (entry.isDirectory()) {
        if (!omitDemoSourceDirectory(name)) visit(path);
      } else if (entry.isSymbolicLink()) {
        if (!omitDemoSourceDirectory(name))
          throw new Error(`Unmodeled production symlink: ${name}`);
      } else if (
        demoSourceClassification(name) === 'runtime' ||
        name === 'frontend/apps/web/src/app/globals.css'
      ) {
        files.set(name, readFileSync(path, 'utf8'));
      }
    }
  };
  visit(join(root, 'frontend'));
  visit(join(root, 'scripts'));
  return productionDemoSources(files, (from, specifier, anchors) => {
    let resolver = createRequire(join(root, from));
    for (const anchor of anchors) resolver = createRequire(resolver.resolve(anchor));
    return relative(root, resolver.resolve(specifier)).replaceAll('\\', '/');
  });
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
        new Map([
          [
            entry,
            "require(require.resolve('eslint-plugin-jsx-a11y', { paths: [require.resolve('eslint-config-next')] }));",
          ],
        ]),
        (_from, name) => `frontend/node_modules/${name}/index.js`,
      ),
    ).not.toThrow();
    for (const source of [
      "const path = './special.test.mjs'; await import(path);",
      "const path = './special.test.mjs'; require(path);",
      "const path = './special.test.mjs'; require(require.resolve(path));",
      "require(require.resolve('./fixtures/demo-identities.json', { paths: [new URL('../src/test', import.meta.url).pathname] }));",
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
      'scripts/node_modules',
      'frontend/apps/web/src/node_modules',
    ])
      expect(omitDemoSourceDirectory(path)).toBe(false);
  });
  it('omits only ignored known machine files and retains ignored source and unknown formats', () => {
    for (const name of ['.env.local', '.DS_Store', 'Thumbs.db', 'editor.swp', 'runtime.log']) {
      const path = `frontend/apps/web/${name}`;
      expect(omitIgnoredDemoLocalFile(path, () => true)).toBe(true);
      expect(omitIgnoredDemoLocalFile(path, () => false)).toBe(false);
      const result = spawnSync('git', ['check-ignore', '--quiet', '--', path], {
        cwd: root,
        stdio: 'ignore',
      });
      expect(result.status).toBe(0);
    }
    for (const name of [
      '.env.hidden.ts',
      '.env.hidden.json',
      'hidden.vue',
      'hidden.resx',
      'Probe.csproj.user',
    ])
      expect(omitIgnoredDemoLocalFile(`frontend/apps/web/${name}`, () => true)).toBe(false);
    // MSBuild automatically imports this ignored format; it is not inert machine data.
    expect(
      spawnSync('git', ['check-ignore', '--quiet', '--', 'frontend/apps/web/Probe.csproj.user'], {
        cwd: root,
        stdio: 'ignore',
      }).status,
    ).toBe(0);
    expect(() => demoSourceClassification('frontend/apps/web/hidden.vue')).toThrow('Unclassified');
  });
  it('refuses unclassified production roots and formats while retaining declared non-runtime ownership', () => {
    for (const path of [
      'shared/probe.mjs',
      'frontend/apps/web/src/probe.vue',
      'scripts/probe.runtime',
      'scripts/new-probe.py',
      'scripts/probes/hangfire-lifecycle/NewProbe.cs',
      'third-party/hangfire/runtime.mjs',
      'third-party/new-vendor/README.md',
      'shared/hidden.d.ts',
      'backend/feature/Probe.cs',
    ])
      expect(() => demoSourceClassification(path)).toThrow('Unclassified');
    for (const path of [
      'scripts/demo.mjs',
      'scripts/node_modules/probe/index.js',
      'frontend/apps/web/src/test/probe.test.ts',
    ])
      expect(demoSourceClassification(path)).toBe('runtime');
    for (const path of [
      'README.md',
      'docs/example.md',
      'scripts/seed.sh',
      'scripts/ci-native-provisioning.py',
      'scripts/ci-native-tests.py',
      'scripts/ci-test-shards.py',
      'scripts/ci-test-shards-plan.json',
      'scripts/ci-test-shard-results.py',
      'scripts/install-leakwatch.py',
      'scripts/test-ci-native-provisioning.py',
      'scripts/test-ci-native-tests.py',
      'scripts/test-ci-test-shards.py',
      'scripts/test-ci-test-shard-results.py',
      'scripts/test-ci-supply-chain.py',
      'scripts/probe-hangfire-lifecycle.py',
      'scripts/probes/hangfire-lifecycle/Program.cs',
      'third-party/hangfire/verify-distribution.py',
      'third-party/hangfire/core/corresponding-source.tar.gz',
      'third-party/hangfire/qualification/MaintenanceProbe.cs',
      'frontend/apps/web/src/app/globals.css',
    ])
      expect(demoSourceClassification(path)).toBe('other');
  });
  it('follows aliases, workspace exports, import-equals and transitive re-exports into exemptions', () => {
    const entry = 'frontend/apps/web/src/server/probe.ts';
    const helper = 'frontend/apps/web/src/test/helper.ts';
    const configHelper = 'frontend/packages/config/helper.test.mjs';
    for (const source of [
      "import '@/test/helper';",
      "export * from '@/test/helper';",
      "import helper = require('@/test/helper');",
      "require.resolve('@/test/helper');",
    ]) {
      const files = productionDemoSources(
        new Map([
          ['frontend/apps/web/tsconfig.json', '{"compilerOptions":{"paths":{"@/*":["./src/*"]}}}'],
          [
            'frontend/packages/config/package.json',
            '{"name":"@learnstack/config","exports":{"./helper":"./helper.test.mjs"}}',
          ],
          [entry, source],
          [helper, "export * from '@learnstack/config/helper';"],
          [configHelper, `export const branch = ${JSON.stringify(identity)};`],
        ]),
      );
      expect(files.has(helper)).toBe(true);
      expect(files.has(configHelper)).toBe(true);
      expect(
        [...files].flatMap(([path, source]) => demoIdentityFindings(path, source, vocabulary)),
      ).toEqual([identity]);
    }
  });
  it('includes ambiguous alias and native JS/TS targets and ignores fixture package identities', () => {
    const entry = 'frontend/apps/web/src/server/probe.ts';
    const alias = 'frontend/apps/web/src/test/dirty.ts';
    const native = 'frontend/apps/web/src/test/native.js';
    const workspace = 'frontend/packages/sdk/src/runtime.test.ts';
    const files = productionDemoSources(
      new Map([
        [
          'frontend/apps/web/tsconfig.json',
          JSON.stringify({
            compilerOptions: {
              paths: {
                '@/*': ['./src/*'],
                '@/special/*': ['./src/test/*'],
              },
            },
          }),
        ],
        [entry, "import '@/special/dirty'; import '@learnstack/sdk/runtime';"],
        ['frontend/apps/web/src/special/dirty.ts', 'export const clean = true;'],
        [alias, `export const special = ${JSON.stringify(identity)};`],
        ['scripts/probe.cjs', "require('../frontend/apps/web/src/test/native');"],
        ['frontend/apps/web/src/test/native.ts', 'export const clean = true;'],
        [native, `exports.special = ${JSON.stringify(identity)};`],
        [
          'frontend/apps/web/src/test/package-fixture/package.json',
          JSON.stringify({
            name: '@learnstack/sdk',
            exports: { './runtime': './clean.ts' },
          }),
        ],
        ['frontend/apps/web/src/test/package-fixture/clean.ts', 'export const clean = true;'],
        [
          'frontend/packages/sdk/package.json',
          JSON.stringify({
            name: '@learnstack/sdk',
            exports: { './runtime': './src/runtime.test.ts' },
          }),
        ],
        [workspace, `export const special = ${JSON.stringify(identity)};`],
      ]),
    );
    for (const path of [alias, native, workspace]) {
      expect(files.has(path)).toBe(true);
      expect(demoIdentityFindings(path, files.get(path)!, vocabulary)).toEqual([identity]);
    }
    for (const path of [alias, native, workspace]) files.set(path, 'export const clean = true;');
    expect(
      [...files].flatMap(([path, source]) => demoIdentityFindings(path, source, vocabulary)),
    ).toEqual([]);
    expect(() =>
      productionDemoSources(
        new Map([
          [entry, "import '@learnstack/sdk/runtime';"],
          ['frontend/packages/sdk/package.json', '{"name":"@learnstack/sdk"}'],
          ['frontend/packages/duplicate/package.json', '{"name":"@learnstack/sdk"}'],
        ]),
      ),
    ).toThrow('Unmodeled');
  });
  it('refuses missing, unknown-format, inherited-alias and untrusted external edges', () => {
    const entry = 'frontend/apps/web/scripts/entry.mjs';
    for (const edge of ['./missing.mjs', './helper.runtime', 'unknown-package', '#local'])
      expect(() =>
        productionDemoSources(
          new Map([
            [entry, `require(${JSON.stringify(edge)})`],
            ['frontend/apps/web/scripts/helper.runtime', `specialize('${identity}')`],
          ]),
        ),
      ).toThrow('Unmodeled');
    expect(() =>
      productionDemoSources(
        new Map([[entry, "import 'linked-package';"]]),
        () => '../foreign/index.js',
      ),
    ).toThrow('Unmodeled');
    expect(() =>
      productionDemoSources(
        new Map([
          [entry, "import 'external-package';"],
          ['frontend/apps/web/tsconfig.json', '{"extends":"./src/test/base"}'],
          ['frontend/apps/web/src/test/base.ts', '{}'],
          [
            'frontend/apps/web/src/test/base.json',
            '{"compilerOptions":{"paths":{"hidden/*":["./*"]}}}',
          ],
        ]),
        () => 'frontend/node_modules/external-package/index.js',
      ),
    ).toThrow('Unmodeled');
    const helper = 'frontend/apps/web/src/test/helper.ts';
    expect(
      productionDemoSources(
        new Map([
          [entry, "import 'linked-package';"],
          [helper, 'export const clean = 1;'],
        ]),
        () => helper,
      ).has(helper),
    ).toBe(true);
    expect(() =>
      productionDemoSources(
        new Map([
          [entry, "import 'external-package';"],
          ['frontend/apps/web/tsconfig.json', '{"extends":"./base.json"}'],
          [
            'frontend/apps/web/base.json',
            '{"compilerOptions":{"paths":{"hidden/*":["./src/test/*"]}}}',
          ],
        ]),
        () => 'frontend/node_modules/external-package/index.js',
      ),
    ).toThrow('Unmodeled');
  });
  it('admits only the validated native generated-output mapping and known stylesheet', () => {
    const entry = 'scripts/demo.mjs';
    const source = 'frontend/apps/web/src/server/ingress.ts';
    const config = {
      compilerOptions: { rootDir: 'src/server', outDir: '.server' },
      include: ['src/server/ingress.ts', 'src/server/public-admission-runtime.ts'],
    };
    const files = new Map([
      [
        entry,
        "import '../frontend/apps/web/.server/ingress.js'; import '../frontend/apps/web/src/app/globals.css';",
      ],
      [source, 'export const clean = true;'],
      ['frontend/apps/web/src/app/globals.css', ':root { color: black }'],
      ['frontend/apps/web/tsconfig.server.json', JSON.stringify(config)],
    ]);
    expect(productionDemoSources(files).has(source)).toBe(true);
    files.set(
      'frontend/apps/web/tsconfig.server.json',
      JSON.stringify({
        ...config,
        compilerOptions: { ...config.compilerOptions, outDir: 'unmodeled' },
      }),
    );
    expect(() => productionDemoSources(files)).toThrow('Unmodeled');
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
