// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { demoIdentityFindings } from './demo-genericity-analysis';
import {
  assertDemoNonRuntimeFiles,
  demoSourceClassification,
  omitDemoSourceDirectory,
  omitIgnoredDemoLocalFile,
  productionDemoSources,
} from './demo-genericity-sources';
import vocabulary from './fixtures/demo-identities.json';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../..');

it('refuses a runtime source planted in the non-runtime ownership list', () => {
  expect(() => assertDemoNonRuntimeFiles()).not.toThrow();
  for (const path of ['scripts/hidden.mjs', 'frontend/apps/web/src/hidden.ts'])
    expect(() => assertDemoNonRuntimeFiles([path])).toThrow(
      'Runtime source cannot have non-runtime ownership',
    );
});
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
      'frontend/apps/web/src/server/auth/session-runtime.ts',
      'frontend/apps/web/src/server/auth/session-store.ts',
      'frontend/apps/web/src/server/auth/native-session-client.ts',
      'frontend/apps/web/src/server/auth/native-administration-client.ts',
      'frontend/apps/web/src/server/configured-public-client.ts',
      'frontend/apps/web/src/components/public/lesson.tsx',
      'frontend/apps/web/src/i18n/messages/en/public.json',
      'frontend/packages/sdk/src/server.ts',
      'frontend/packages/sdk/src/administration-server.ts',
      'frontend/apps/web/scripts/public-server.mjs',
      'frontend/apps/web/scripts/public-server-runtime.mjs',
      'frontend/apps/web/scripts/public-runtime-child.mjs',
      'frontend/scripts/framework-runtime-family.mjs',
      'frontend/scripts/framework-runtime-child-control.mjs',
      'frontend/apps/web/scripts/framework-proof-relay.mjs',
      'frontend/apps/web/scripts/framework-proof-host-broker.mjs',
      'frontend/apps/web/scripts/framework-build.mjs',
      'frontend/apps/web/scripts/framework-proof-support.mjs',
      'frontend/scripts/framework-installation.mjs',
      'frontend/scripts/framework-development.mjs',
      'frontend/framework/next-response-cleanup/owner.mjs',
      'frontend/apps/web/scripts/development-hmr.mjs',
      'frontend/apps/web/scripts/stop-test-child.mjs',
      'frontend/apps/web/scripts/verify-bff-protocol.mjs',
      'scripts/demo.mjs',
      'scripts/require-node-test-cases.mjs',
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
  it('scans the native BFF proof entry instead of exempting planted demo literals', () => {
    const entry = 'frontend/apps/web/scripts/verify-bff-protocol.mjs';
    for (const value of ['independent', identity]) {
      const files = productionDemoSources(
        new Map([[entry, `export const name = ${JSON.stringify(value)};`]]),
      );
      expect(files.has(entry)).toBe(true);
      expect(demoIdentityFindings(entry, files.get(entry)!, vocabulary)).toEqual(
        value === identity ? [identity] : [],
      );
    }
  });
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
  const frameworkGuard = 'frontend/scripts/framework-installation.mjs';
  const frameworkGuardSource = readFileSync(join(root, frameworkGuard), 'utf8');
  const guardSources = (source = frameworkGuardSource) =>
    new Map([
      [frameworkGuard, source],
      ['frontend/scripts/framework-process-family.mjs', 'export const family = true;'],
      ['frontend/scripts/framework-runtime-family.mjs', 'export const runtime = true;'],
    ]);
  const provenanceEntry = 'frontend/scripts/test-framework-provenance.mjs';
  const provenanceSource = readFileSync(join(root, provenanceEntry), 'utf8');
  const provenanceSources = (source = provenanceSource) =>
    guardSources()
      .set(provenanceEntry, source)
      .set('frontend/scripts/framework-development.mjs', 'export const development = true;')
      .set(
        'frontend/scripts/test-framework-metadata-contention.mjs',
        'export const metadata = true;',
      )
      .set('frontend/scripts/test-framework-process-family.mjs', 'export const family = true;');
  it('resolves the actual guard loaders from the checked application package and retains the guard source', () => {
    const observed: { from: string; edge: string; anchors: string[] }[] = [];
    const selected = productionDemoSources(guardSources(), (from, edge, anchors) => {
      observed.push({ from, edge, anchors });
      return `frontend/apps/web/node_modules/${edge}/index.js`;
    });
    expect(selected.has(frameworkGuard)).toBe(true);
    expect(selected.has('frontend/scripts/framework-process-family.mjs')).toBe(true);
    expect(observed).toEqual([
      { from: 'frontend/apps/web/package.json', edge: 'next', anchors: [] },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/bin/next', anchors: [] },
      { from: 'frontend/apps/web/package.json', edge: 'typescript/bin/tsc', anchors: [] },
    ]);
  });
  it('follows extra guard loader edges through local and externally resolved first-party test helpers', () => {
    const local = 'frontend/apps/web/src/test/guard-local.ts';
    const external = 'frontend/apps/web/src/test/guard-external.ts';
    const nested = 'frontend/apps/web/src/test/guard-nested.test.ts';
    const marker = "  const require = createRequire(join(appRoot, 'package.json'));";
    const source = frameworkGuardSource.replace(
      marker,
      `${marker}\n  require('./src/test/guard-local.ts');\n  require.resolve('guard-helper');`,
    );
    expect(source).not.toBe(frameworkGuardSource);
    const files = guardSources(source)
      .set(local, "export * from './guard-nested.test';")
      .set(external, `export const identity = ${JSON.stringify(identity)};`)
      .set(nested, `export const identity = ${JSON.stringify(identity)};`);
    const selected = productionDemoSources(files, (from, edge) => {
      expect(from).toBe('frontend/apps/web/package.json');
      return edge === 'guard-helper' ? external : `frontend/apps/web/node_modules/${edge}/index.js`;
    });
    for (const helper of [local, external, nested]) expect(selected.has(helper)).toBe(true);
    for (const helper of [external, nested])
      expect(demoIdentityFindings(helper, selected.get(helper)!, vocabulary)).toEqual([identity]);
  });
  it('refuses unknown, unchecked, rebound and shadowed guard loader roots or loader bindings', () => {
    const marker = "  const require = createRequire(join(appRoot, 'package.json'));";
    for (const [before, after] of [
      [marker, "  const require = createRequire(join(otherRoot, 'package.json'));"],
      ["join(identity.appRoot, 'package.json')", "join(identity.otherRoot, 'package.json')"],
      ['appRoot = checkedRoot(appRoot);', 'appRoot = otherRoot;'],
      ['appRoot = checkedRoot(appRoot);', 'appRoot = checkedRoot(otherRoot);'],
      [marker, `appRoot = otherRoot;\n${marker}`],
      [marker, `${marker}\n  appRoot = otherRoot;`],
      [marker, `${marker}\n  ({ root: appRoot } = other);`],
      [marker, `${marker}\n  ([...appRoot] = other);`],
      [marker, `${marker}\n  for (appRoot of otherRoots) { require('./hidden'); }`],
      [marker, `${marker}\n  function nested(appRoot) { require('./hidden'); }`],
      [marker, `${marker}\n  function nested(require) { require('./hidden'); }`],
      [marker, `${marker}\n  require = otherRequire;`],
      [marker, `${marker}\n  require.resolve = otherResolve;`],
      [marker, `${marker}\n  const loader = require; loader('./hidden');`],
      [marker, `${marker}\n  const loader = createRequire; loader(otherRoot)('./hidden');`],
      [marker, `${marker}\n  join = otherJoin;`],
      [marker, `${marker}\n  checkedRoot = otherCheck;`],
      [marker, `${marker}\n  createRequire = otherFactory;`],
      [marker, "  const require = createRequire(join(appRoot, 'other.json'));"],
      [marker, '  const require = createRequire(import.meta.url);'],
      [
        "  const require = createRequire(join(identity.appRoot, 'package.json'));",
        "  const require = createRequire(join(identity.appRoot, 'package.json'));\n  identity.appRoot = otherRoot;",
      ],
      [marker, `${marker}\n  const extra = createRequire(join(otherRoot, 'package.json'));`],
      [
        "import { createRequire } from 'node:module';",
        "import { createRequire } from './loader.mjs';",
      ],
    ]) {
      const source = frameworkGuardSource.replace(before!, after!);
      expect(source).not.toBe(frameworkGuardSource);
      expect(
        () =>
          productionDemoSources(guardSources(source), () => 'frontend/node_modules/clean/index.js'),
        after,
      ).toThrow('Unmodeled runtime import');
    }
  });
  it.each([
    [
      'aliased named factory',
      "import { createRequire as hiddenFactory } from 'node:module';",
      'hiddenFactory',
    ],
    [
      'namespace factory',
      "import * as hiddenModule from 'node:module';",
      'hiddenModule.createRequire',
    ],
    [
      'bare builtin aliased factory',
      "import { createRequire as hiddenFactory } from 'module';",
      'hiddenFactory',
    ],
  ])(
    'refuses an additional %s that would hide a reachable first-party helper',
    (_name, factoryImport, factory) => {
      const helper = 'frontend/apps/web/src/test/concealed-helper.mjs';
      const source =
        `${factoryImport}\n${frameworkGuardSource}\n` +
        `const hiddenLoader = ${factory}(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));\n` +
        "hiddenLoader('./src/test/concealed-helper.mjs');\n";
      const files = guardSources(source).set(
        helper,
        `export const branch = ${JSON.stringify(identity)};`,
      );
      expect(demoIdentityFindings(helper, files.get(helper)!, vocabulary)).toEqual([identity]);
      expect(() =>
        productionDemoSources(files, () => 'frontend/node_modules/clean/index.js'),
      ).toThrow('Unmodeled runtime import');
    },
  );
  it.each([
    ['dynamic prefixed module', "const hiddenModule = await import('node:module');", ''],
    ['dynamic bare module', "const hiddenModule = await import('module');", ''],
    ['required prefixed module', "const hiddenModule = require('node:module');", ''],
    ['required bare module', "const hiddenModule = require('module');", ''],
    ['bare builtin getter', "const hiddenModule = process.getBuiltinModule('module');", ''],
    [
      'prefixed builtin getter',
      "const hiddenModule = process.getBuiltinModule('node:module');",
      '',
    ],
    ['literal computed getter', "const hiddenModule = process['getBuiltinModule']('module');", ''],
    [
      'computed getter and module',
      "const hiddenModule = process['get' + 'BuiltinModule']('node:' + 'module');",
      '',
    ],
    [
      'aliased builtin getter',
      "const getter = process.getBuiltinModule; const hiddenModule = getter('node:module');",
      '',
    ],
    [
      'destructured builtin getter',
      "const { getBuiltinModule: getter } = process; const hiddenModule = getter('module');",
      '',
    ],
    [
      'aliased process with computed getter',
      "const hiddenProcess = process; const hiddenModule = hiddenProcess['get' + 'BuiltinModule']('module');",
      '',
    ],
    [
      'reflected builtin getter',
      "const getter = Reflect.get(process, 'getBuiltinModule'); const hiddenModule = getter('module');",
      '',
    ],
    [
      'aliased global process',
      "const hiddenGlobal = globalThis; const hiddenModule = hiddenGlobal['process']['getBuiltinModule']('module');",
      '',
    ],
    [
      'dynamically acquired process',
      "const hiddenProcess = (await import('node:process')).default; const hiddenModule = hiddenProcess.getBuiltinModule('module');",
      '',
    ],
    [
      'required process',
      "const hiddenProcess = require('process'); const hiddenModule = hiddenProcess.getBuiltinModule('module');",
      '',
    ],
    [
      'statically imported process',
      "const hiddenModule = hiddenProcess['getBuiltinModule']('module');",
      "import hiddenProcess from 'node:process';",
    ],
  ])(
    'refuses %s in both closed sources while Node actually loads the concealed helper',
    (_name, acquisition, preamble) => {
      const privateRoot = mkdtempSync(join(tmpdir(), 'learnstack-source-factory-'));
      const appRoot = join(privateRoot, 'frontend/apps/web');
      const helper = 'frontend/apps/web/src/test/concealed-helper.cjs';
      const helperSource = `module.exports = ${JSON.stringify(identity)};`;
      try {
        mkdirSync(join(appRoot, 'src/test'), { recursive: true });
        writeFileSync(join(appRoot, 'package.json'), '{"type":"commonjs"}\n');
        writeFileSync(join(privateRoot, helper), helperSource);
        const load =
          "const hiddenLoader = hiddenModule['create' + 'Require'](join(appRoot, 'package.json'));\n" +
          "hiddenLoader('./src/test/concealed-helper.cjs');\n";
        const actual = execFileSync(
          process.execPath,
          [
            '--input-type=module',
            '-e',
            `${preamble}\nimport { createRequire } from 'node:module';\n` +
              "import { join } from 'node:path';\n" +
              `const appRoot = ${JSON.stringify(appRoot)};\n` +
              "const require = createRequire(join(appRoot, 'package.json'));\n" +
              acquisition +
              '\n' +
              load.replace(
                "hiddenLoader('./src/test/concealed-helper.cjs');",
                "console.log(hiddenLoader('./src/test/concealed-helper.cjs'));",
              ),
          ],
          { encoding: 'utf8', cwd: privateRoot },
        );
        expect(actual.trim()).toBe(identity);
        for (const [entry, source, marker, sources] of [
          [
            frameworkGuard,
            frameworkGuardSource,
            "  const require = createRequire(join(appRoot, 'package.json'));",
            guardSources,
          ],
          [
            provenanceEntry,
            provenanceSource,
            "const actualRequire = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));",
            provenanceSources,
          ],
        ] as const) {
          const selectedLoad =
            entry === provenanceEntry
              ? load.replace(
                  "join(appRoot, 'package.json')",
                  "join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json')",
                )
              : load;
          const addition = `${acquisition}\n${selectedLoad}`.replaceAll(
            /\brequire\(/g,
            entry === provenanceEntry ? 'actualRequire(' : 'require(',
          );
          const dirty = `${preamble}\n${source.replace(marker, `${marker}\nvoid (async () => {\n${addition}\n})();`)}`;
          expect(source).toContain(marker);
          const files = sources(dirty).set(helper, helperSource);
          expect(demoIdentityFindings(helper, helperSource, vocabulary)).toEqual([identity]);
          expect(() =>
            productionDemoSources(files, () => 'frontend/node_modules/clean/index.js'),
          ).toThrow(`Unmodeled runtime import: ${entry}`);
        }
      } finally {
        rmSync(privateRoot, { recursive: true, force: true });
      }
    },
  );
  it('keeps ordinary builtin imports outside the special loader model unchanged', () => {
    const entry = 'frontend/apps/web/scripts/ordinary.mjs';
    const source =
      "import process from 'node:process'; import { createRequire } from 'node:module';\n" +
      "await import('module'); require('node:module'); process.getBuiltinModule('module');";
    expect(productionDemoSources(new Map([[entry, source]])).get(entry)).toBe(source);
  });
  it('refuses unmodeled guard edges even after recognizing the app-relative loader', () => {
    const marker = "  const require = createRequire(join(appRoot, 'package.json'));";
    for (const edge of [
      'require.resolve(selectedModule);',
      "require('./src/test/missing.ts');",
      "require['resolve']('next');",
      'await import(selectedModule);',
      "require.resolve('outside');",
    ]) {
      const source = frameworkGuardSource.replace(marker, `${marker}\n  ${edge}`);
      expect(() =>
        productionDemoSources(guardSources(source), (_from, name) =>
          name === 'outside' ? '../foreign/helper.mjs' : `frontend/node_modules/${name}/index.js`,
        ),
      ).toThrow('Unmodeled runtime import');
    }
  });
  it('models the actual provenance control app anchor and refuses a changed or rebound control root', () => {
    const entry = provenanceEntry;
    const source = provenanceSource;
    const sources = provenanceSources;
    const observed: { from: string; edge: string }[] = [];
    const external = (from: string, edge: string) => {
      observed.push({ from, edge });
      return `frontend/apps/web/node_modules/${edge}/index.js`;
    };
    expect(productionDemoSources(sources(source), external).has(entry)).toBe(true);
    expect(observed).toEqual([
      { from: 'frontend/apps/web/package.json', edge: 'next' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/bin/next' },
      { from: 'frontend/apps/web/package.json', edge: 'typescript/bin/tsc' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/build/create-compiler-aliases' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/compiled/webpack/webpack' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/server/lib/find-page-file' },
      { from: 'frontend/apps/web/package.json', edge: 'typescript' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/lib/find-pages-dir' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/lib/find-pages-dir' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/lib/find-pages-dir' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/lib/find-pages-dir' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/lib/get-files-in-dir' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/build/utils.js' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/compiled/browserslist' },
      { from: 'frontend/apps/web/package.json', edge: 'next/dist/build/utils.js' },
    ]);
    const marker =
      "const actualRequire = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));";
    expect(source).toContain(marker);
    for (const dirty of [
      source.replace(
        marker,
        "const actualRequire = createRequire(join(otherRoot, 'apps/web/package.json'));",
      ),
      source.replace(
        marker,
        "const actualRequire = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/other/package.json'));",
      ),
      source.replace(marker, `${marker}\nFRAMEWORK_FRONTEND_ROOT = otherRoot;`),
      source.replace(
        marker,
        `${marker}\nfunction nested(FRAMEWORK_FRONTEND_ROOT) { actualRequire('./hidden'); }`,
      ),
      source.replace(marker, `${marker}\nactualRequire = otherRequire;`),
      source.replace(
        marker,
        `${marker}\nfunction nested(actualRequire) { actualRequire('./hidden'); }`,
      ),
      source.replace(
        marker,
        `${marker}\nconst hiddenLoader = actualRequire; hiddenLoader('./hidden');`,
      ),
      source.replace(marker, `${marker}\nrequire('./hidden');`),
      source.replace(marker, marker.replace('const actualRequire', 'let actualRequire')),
      source.replace(marker, `function nested() { ${marker} }`),
      source.replace(marker, `${marker}\nactualRequire.resolve(selectedModule);`),
      source.replace('  FRAMEWORK_FRONTEND_ROOT,', '  FRAMEWORK_FRONTEND_ROOT as OTHER_ROOT,'),
      source + '\nconst FRAMEWORK_FRONTEND_ROOT = otherRoot;',
    ]) {
      expect(dirty).not.toBe(source);
      expect(() => productionDemoSources(sources(dirty), external)).toThrow(
        'Unmodeled runtime import',
      );
    }
  });
  it('follows the hoisted provenance loader into local and transitive first-party helpers', () => {
    const local = 'frontend/apps/web/src/test/provenance-local.cjs';
    const nested = 'frontend/apps/web/src/test/provenance-nested.test.cjs';
    const marker =
      "const actualRequire = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));";
    expect(provenanceSource).toContain(marker);
    const source = provenanceSource.replace(
      marker,
      `${marker}\nactualRequire('./src/test/provenance-local.cjs');`,
    );
    const files = provenanceSources(source)
      .set(local, "module.exports = require('./provenance-nested.test.cjs');")
      .set(nested, `module.exports = ${JSON.stringify(identity)};`);
    const selected = productionDemoSources(
      files,
      () => 'frontend/apps/web/node_modules/clean/index.js',
    );
    for (const helper of [local, nested]) expect(selected.has(helper)).toBe(true);
    expect(demoIdentityFindings(nested, selected.get(nested)!, vocabulary)).toEqual([identity]);
  });
  it('models only the copied BFF observation harness generated imports and their actual sources', () => {
    const harness = 'frontend/apps/web/scripts/test-bff-proof-runtime-observation.mjs';
    const runtime = 'frontend/apps/web/src/server/auth/request-runtime.ts';
    const fixture = 'frontend/apps/web/src/test/page-response-fixture.ts';
    const source = readFileSync(join(root, harness), 'utf8');
    const host = 'frontend/apps/web/scripts/bff-proof-host.mjs';
    const instrumenter = readFileSync(join(root, host), 'utf8');
    const files = new Map([
      [harness, source],
      [runtime, 'export const runtime = true;'],
      [fixture, `export const special = ${JSON.stringify(identity)};`],
      [host, instrumenter],
      ['frontend/apps/web/scripts/fixture-support.mjs', 'export const helper = true;'],
      ['frontend/apps/web/scripts/bff-proof-support.mjs', 'export const helper = true;'],
      ['frontend/apps/web/scripts/framework-proof-support.mjs', 'export const helper = true;'],
      ['frontend/apps/web/scripts/framework-proof-host-broker.mjs', 'export const helper = true;'],
    ]);
    const external = () => 'frontend/node_modules/typescript/index.js';
    const selected = productionDemoSources(files, external);
    expect(selected.has(runtime)).toBe(true);
    expect(selected.has(fixture)).toBe(true);
    expect(demoIdentityFindings(fixture, selected.get(fixture)!, vocabulary)).toEqual([identity]);
    const toCrlf = (text: string) => text.replace(/\r?\n/g, '\r\n');
    for (const text of [source, instrumenter]) expect(toCrlf(toCrlf(text))).toBe(toCrlf(text));
    for (const generatorText of [source.replace(/\r\n/g, '\n'), toCrlf(source)])
      for (const ownerText of [instrumenter.replace(/\r\n/g, '\n'), toCrlf(instrumenter)]) {
        const form = new Map(files).set(harness, generatorText).set(host, ownerText);
        const formSelected = productionDemoSources(form, external);
        expect(formSelected.has(runtime)).toBe(true);
        expect(demoIdentityFindings(fixture, formSelected.get(fixture)!, vocabulary)).toEqual([
          identity,
        ]);
      }
    const changedInstrumenter = instrumenter.replace(
      'export function instrumentBffProofRequestRuntime(runtime) {',
      "export function instrumentBffProofRequestRuntime(runtime) {\n  runtime += '\\n' + readFileSync(new URL('../src/test/unmodeled-fixture.ts', import.meta.url), 'utf8');",
    );
    expect(changedInstrumenter).not.toBe(instrumenter);
    expect(() =>
      productionDemoSources(new Map(files).set(host, changedInstrumenter), external),
    ).toThrow('Unmodeled runtime import');
    const missingInstrumenter = new Map(files);
    missingInstrumenter.delete(host);
    expect(() => productionDemoSources(missingInstrumenter, external)).toThrow(
      'Unmodeled runtime import',
    );
    for (const dirty of [
      source.replace(/\r?\n/g, '\r'),
      source.replace("'server/auth/request-runtime.js'", "'server/auth/other.js'"),
      source.replace("'test/page-response-fixture.js'", "'test/other.js'"),
      source.replace("'learnstack-bff-observation-'", "'unowned-'"),
      source.replace("readFileSync(source, 'utf8')", "readFileSync(other, 'utf8')"),
      source.replace('const path = join(input, name);', 'const path = join(other, name);'),
      source.replace(
        "path === source ? instrumented : readFileSync(path, 'utf8')",
        "readFileSync(path, 'utf8')",
      ),
      source.replace(
        '  const { createBffRequestRuntime }',
        "  writeFileSync(join(root, 'test/page-response-fixture.js'), readFileSync(join(web, 'src/test/unmodeled-fixture.ts'), 'utf8'));\n  const { createBffRequestRuntime }",
      ),
      source.replace(
        "text = readFileSync(input, 'utf8')",
        "text = readFileSync(join(web, 'src/test/unmodeled-fixture.ts'), 'utf8')",
      ),
      source.replace(
        '    const result = ts.transpileModule(text, {',
        "    text = readFileSync(join(web, 'src/test/unmodeled-fixture.ts'), 'utf8');\n    const result = ts.transpileModule(text, {",
      ),
      source + "\nawait import('./other.mjs');",
      source + "\nawait import(pathToFileURL(join(root, 'server/auth/request-runtime.js')));",
      source.replace(
        "pathToFileURL(join(root, 'test/page-response-fixture.js'))",
        "pathToFileURL(join(other, 'test/page-response-fixture.js'))",
      ),
    ]) {
      expect(dirty).not.toBe(source);
      expect(() => productionDemoSources(new Map(files).set(harness, dirty), external)).toThrow(
        'Unmodeled runtime import',
      );
    }
    const missing = new Map(files);
    missing.delete(fixture);
    expect(() => productionDemoSources(missing, external)).toThrow('Unmodeled runtime import');
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
      'scripts/test-realm-node-gate.py',
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
    const nested = 'frontend/apps/web/src/server/auth/session-runtime.ts';
    const native = 'frontend/apps/web/src/server/auth/native-administration-client.ts';
    const sdk = 'frontend/packages/sdk/src/administration-server.ts';
    const helper = 'frontend/apps/web/src/server/auth/owned-helper.ts';
    const config = {
      compilerOptions: { rootDir: 'src/server', outDir: '.server' },
      include: [
        'src/server/ingress.ts',
        'src/server/public-admission-runtime.ts',
        'src/server/auth/account-client.ts',
        'src/server/auth/configuration.ts',
        'src/server/auth/http-protocol.ts',
        'src/server/auth/logout-response.ts',
        'src/server/auth/native-administration-client.ts',
        'src/server/auth/native-session-client.ts',
        'src/server/auth/provider-core.ts',
        'src/server/auth/request-runtime.ts',
        'src/server/auth/session-runtime.ts',
      ],
    };
    const files = new Map([
      [
        entry,
        "import '../frontend/apps/web/.server/ingress.js'; import '../frontend/apps/web/.server/auth/session-runtime.js'; import '../frontend/apps/web/.server/auth/native-administration-client.js'; import '../frontend/apps/web/src/app/globals.css';",
      ],
      [source, 'export const clean = true;'],
      [nested, "import './owned-helper.js';"],
      [helper, 'export const owned = true;'],
      [native, "import '@learnstack/sdk/administration-server';"],
      [sdk, `export const special = ${JSON.stringify(identity)};`],
      [
        'frontend/packages/sdk/package.json',
        JSON.stringify({
          name: '@learnstack/sdk',
          exports: { './administration-server': './dist/administration-server.js' },
        }),
      ],
      [
        'frontend/packages/sdk/tsconfig.native.json',
        JSON.stringify({
          compilerOptions: { rootDir: 'src', outDir: 'dist' },
          include: ['src/authenticated-server.ts', 'src/administration-server.ts'],
        }),
      ],
      ['frontend/apps/web/src/app/globals.css', ':root { color: black }'],
      ['frontend/apps/web/tsconfig.server.json', JSON.stringify(config)],
    ]);
    const selected = productionDemoSources(files);
    for (const input of [source, nested, helper, native, sdk])
      expect(selected.has(input)).toBe(true);
    expect(demoIdentityFindings(sdk, selected.get(sdk)!, vocabulary)).toEqual([identity]);
    for (const patch of [
      {
        compilerOptions: { rootDir: 'src', outDir: 'unmodeled' },
        include: ['src/authenticated-server.ts', 'src/administration-server.ts'],
      },
      {
        compilerOptions: { rootDir: 'src', outDir: 'dist' },
        include: ['src/authenticated-server.ts', 'src/administration-server.ts', 'src/hidden.ts'],
      },
      {
        compilerOptions: { rootDir: 'src', outDir: 'dist' },
        include: ['src/authenticated-server.ts'],
      },
    ]) {
      const dirty = new Map(files);
      dirty.set('frontend/packages/sdk/tsconfig.native.json', JSON.stringify(patch));
      expect(() => productionDemoSources(dirty)).toThrow(
        'Unmodeled runtime import: tsconfig.native.json',
      );
    }
    const unknown = new Map(files);
    unknown.set(
      'frontend/apps/web/tsconfig.server.json',
      JSON.stringify({ ...config, include: [...config.include, 'src/server/unmodeled.ts'] }),
    );
    expect(() => productionDemoSources(unknown)).toThrow('Unmodeled');
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
