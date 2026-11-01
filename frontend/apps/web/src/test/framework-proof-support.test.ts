// @vitest-environment node
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, expect, it } from 'vitest';

import {
  frameworkProofBuildArguments,
  prepareFrameworkProof,
} from '../../scripts/framework-proof-support.mjs';

let root: string;
let sourceApp: string;
let app: string;
let sourceSDK: string;
beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'learnstack-framework-proof-inputs-'));
  sourceApp = join(root, 'source/frontend/apps/web');
  app = join(root, 'copy/frontend/apps/web');
  mkdirSync(join(sourceApp, 'scripts'), { recursive: true });
  mkdirSync(join(root, 'source/frontend/scripts'), { recursive: true });
  sourceSDK = join(root, 'source/frontend/packages/sdk');
  for (const directory of ['src', 'scripts', 'node_modules', 'dist'])
    mkdirSync(join(sourceSDK, directory), { recursive: true });
  writeFileSync(join(sourceSDK, 'src/native.ts'), 'export const source = true;\n');
  writeFileSync(join(sourceSDK, 'scripts/build-native.mjs'), '// actual producer\n');
  writeFileSync(join(sourceSDK, 'dist/old.js'), '// stale source output is never copied\n');
  for (const file of ['package.json', 'tsconfig.json', 'tsconfig.native.json'])
    writeFileSync(join(sourceSDK, file), '{}\n');
  mkdirSync(join(sourceApp, 'node_modules/@learnstack'), { recursive: true });
  symlinkSync(sourceSDK, join(sourceApp, 'node_modules/@learnstack/sdk'), 'dir');
  for (const name of ['next', '@types']) {
    const dependency = join(root, 'source/frontend/node_modules', name);
    mkdirSync(dependency, { recursive: true });
    symlinkSync(dependency, join(sourceApp, 'node_modules', name), 'dir');
  }

  for (const name of [
    'framework-build.mjs',
    'public-server.mjs',
    'public-runtime-child.mjs',
    'framework-proof-relay.mjs',
    'framework-proof-ingress-bind.mjs',
    'ensure-routes-stub.mjs',
  ])
    writeFileSync(
      join(sourceApp, 'scripts', name),
      ['public-server.mjs', 'public-runtime-child.mjs'].includes(name)
        ? readFileSync(new URL(`../../scripts/${name}`, import.meta.url), 'utf8')
        : `// ${name}\n`,
    );
});
afterEach(() => rmSync(root, { recursive: true, force: true }));

it('copies the exact supported bootstrap/producer and links only qualified dependency scripts', () => {
  prepareFrameworkProof({ sourceApp, app });
  for (const name of [
    'framework-build.mjs',
    'framework-proof-relay.mjs',
    'framework-proof-ingress-bind.mjs',
    'ensure-routes-stub.mjs',
  ])
    expect(readFileSync(join(app, 'scripts', name), 'utf8')).toBe(
      readFileSync(join(sourceApp, 'scripts', name), 'utf8'),
    );
  expect(readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8')).toContain(
    "createCopiedParentProof('none')",
  );
  expect(readFileSync(join(app, 'scripts/public-runtime-child.mjs'), 'utf8')).toContain(
    "createCopiedChildProof('none')",
  );
  expect(realpathSync(join(root, 'copy/frontend/scripts'))).toBe(
    realpathSync(join(root, 'source/frontend/scripts')),
  );
  expect(existsSync(join(app, '.next'))).toBe(false);
  expect(existsSync(join(app, '.server'))).toBe(false);
  expect(frameworkProofBuildArguments(app)).toEqual([
    join(app, 'scripts/framework-build.mjs'),
    '--app-root',
    app,
  ]);
  expect(() => prepareFrameworkProof({ sourceApp, app })).not.toThrow();
});

it('refuses writing into the actual application frontend root', () => {
  expect(() => prepareFrameworkProof({ sourceApp, app: sourceApp })).toThrow(
    'Proof output must be isolated',
  );
});

it.each(['directory', 'different-link'] as const)(
  'refuses an ambiguous dependency scripts %s instead of replacing it',
  (kind) => {
    const target = join(root, 'copy/frontend/scripts');
    mkdirSync(join(root, 'copy/frontend'), { recursive: true });
    if (kind === 'directory') mkdirSync(target);
    else {
      const wrong = join(root, 'other-scripts');
      mkdirSync(wrong);
      symlinkSync(wrong, target, 'dir');
    }
    expect(() => prepareFrameworkProof({ sourceApp, app })).toThrow();
    expect(existsSync(join(app, 'scripts/public-server.mjs'))).toBe(false);
  },
);

it('owns independent SDK sources/output for concurrent proof app roots', () => {
  const second = join(root, 'other/frontend/apps/web');
  prepareFrameworkProof({ sourceApp, app });
  prepareFrameworkProof({ sourceApp, app: second });
  const firstSDK = realpathSync(join(app, 'node_modules/@learnstack/sdk'));
  const secondSDK = realpathSync(join(second, 'node_modules/@learnstack/sdk'));
  expect(firstSDK).not.toBe(realpathSync(sourceSDK));
  expect(secondSDK).not.toBe(firstSDK);
  expect(readFileSync(join(firstSDK, 'src/native.ts'), 'utf8')).toBe(
    readFileSync(join(sourceSDK, 'src/native.ts'), 'utf8'),
  );
  for (const sdk of [firstSDK, secondSDK]) expect(existsSync(join(sdk, 'dist'))).toBe(false);
  mkdirSync(join(firstSDK, 'dist'));
  writeFileSync(join(firstSDK, 'dist/private.js'), '// private emitted output\n');
  expect(existsSync(join(secondSDK, 'dist/private.js'))).toBe(false);
  expect(existsSync(join(sourceSDK, 'dist/private.js'))).toBe(false);
  expect(realpathSync(join(app, 'node_modules/next'))).toBe(
    realpathSync(join(sourceApp, 'node_modules/next')),
  );
});

it('converts only the declared app dependency link without changing canonical dependencies', () => {
  mkdirSync(app, { recursive: true });
  symlinkSync(join(sourceApp, 'node_modules'), join(app, 'node_modules'), 'dir');
  prepareFrameworkProof({ sourceApp, app });
  expect(realpathSync(join(sourceApp, 'node_modules/@learnstack/sdk'))).toBe(
    realpathSync(sourceSDK),
  );
  expect(realpathSync(join(app, 'node_modules/@learnstack/sdk'))).not.toBe(realpathSync(sourceSDK));
  expect(existsSync(join(sourceSDK, 'dist/old.js'))).toBe(true);
});

it('refuses an edited SDK source instead of overwriting it on repeated preparation', () => {
  prepareFrameworkProof({ sourceApp, app });
  const sdk = realpathSync(join(app, 'node_modules/@learnstack/sdk'));
  writeFileSync(join(sdk, 'src/native.ts'), '// intentionally different source\n');
  expect(() => prepareFrameworkProof({ sourceApp, app })).toThrow('SDK source bytes must match');
  expect(readFileSync(join(sdk, 'src/native.ts'), 'utf8')).toBe(
    '// intentionally different source\n',
  );
});

it('refuses an aliased private packages directory before writing into the source SDK', () => {
  const frontend = join(root, 'copy/frontend');
  mkdirSync(frontend, { recursive: true });
  symlinkSync(join(root, 'source/frontend/packages'), join(frontend, 'packages'), 'dir');
  expect(() => prepareFrameworkProof({ sourceApp, app })).toThrow(
    'Fixture directory must be owned',
  );
  expect(existsSync(join(sourceSDK, 'dist/private.js'))).toBe(false);
  expect(readFileSync(join(sourceSDK, 'src/native.ts'), 'utf8')).toBe(
    'export const source = true;\n',
  );
});
