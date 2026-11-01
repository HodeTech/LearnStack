// Counterfactual drift controls over copied, qualified package subjects. These
// controls do not claim that a synthetic build manifest was produced by Next.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createServer, request } from 'node:http';
import {
  cpSync,
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { protectFrameworkDevelopmentResponse } from './framework-development.mjs';
import { runFrameworkMetadataControl } from './test-framework-metadata-contention.mjs';
import { runFrameworkFamilyControl } from './test-framework-process-family.mjs';
import {
  acquireFrameworkLease,
  cleanFrameworkOutputs,
  copyQualifiedFrameworkBuild,
  FRAMEWORK_FRONTEND_ROOT,
  FrameworkAdmissionError,
  frameworkInputGraph,
  frameworkOutputGraph,
  frameworkRuntimeEnvironment,
  pnpmPatchHash,
  prepareFrameworkBuild,
  prepareFrameworkNativeCache,
  resolveQualifiedPnpm,
  verifyFrameworkBuild,
  verifyFrameworkDevelopment,
  verifyFrameworkInstallation,
} from './framework-installation.mjs';
import { runFrameworkProcessFamily } from './framework-process-family.mjs';

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
function put(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(
    path,
    typeof value === 'string' || Buffer.isBuffer(value)
      ? value
      : `${JSON.stringify(value, null, 2)}\n`,
  );
}
function copy(source, destination) {
  mkdirSync(dirname(destination), { recursive: true });
  cpSync(source, destination, { recursive: true });
}
function link(source, destination) {
  mkdirSync(dirname(destination), { recursive: true });
  symlinkSync(source, destination, 'dir');
}
function denied(action, reason) {
  assert.throws(
    action,
    (error) => error instanceof FrameworkAdmissionError && error.reason === reason,
  );
}

// This baseline verifies actual current package subjects and the real selected
// pnpm binary. All mutations below affect only a disposable private copy.
const actual = verifyFrameworkInstallation({ appRoot: join(FRAMEWORK_FRONTEND_ROOT, 'apps/web') });
const actualManager = resolveQualifiedPnpm();
const sourceCensus = JSON.parse(
  readFileSync(
    join(FRAMEWORK_FRONTEND_ROOT, 'framework/next-response-cleanup/census.json'),
    'utf8',
  ),
);
const root = mkdtempSync(join(tmpdir(), 'learnstack-framework-provenance-'));
const frontendRoot = join(root, 'frontend');
const appRoot = join(frontendRoot, 'apps/web');
const nextRoot = join(frontendRoot, 'node_modules/next');
mkdirSync(appRoot, { recursive: true });
for (const file of [
  'package.json',
  'pnpm-lock.yaml',
  'pnpm-workspace.yaml',
  '.npmrc',
  '.pnpmfile.cjs',
  '.nvmrc',
])
  copy(join(FRAMEWORK_FRONTEND_ROOT, file), join(frontendRoot, file));
for (const folder of ['framework', 'patches', 'scripts'])
  copy(join(FRAMEWORK_FRONTEND_ROOT, folder), join(frontendRoot, folder));
for (const subject of sourceCensus.files)
  copy(join(actual.nextRoot, subject.path), join(nextRoot, subject.path));
copy(join(actual.nextRoot, 'package.json'), join(nextRoot, 'package.json'));
for (const entry of [
  sourceCensus.packageMetadata.runtimeEntry,
  sourceCensus.packageMetadata.compilerEntry,
])
  copy(join(actual.nextRoot, entry), join(nextRoot, entry));
// Membership is a deliberately closed copied subset for this algorithm control;
// the actual 7k-file distribution has its separate install/qualification check.
const census = structuredClone(sourceCensus);
census.distribution.paths = [
  ...census.files.map((file) => file.path),
  'package.json',
  census.packageMetadata.runtimeEntry,
  census.packageMetadata.compilerEntry,
].sort();
put(join(frontendRoot, 'framework/next-response-cleanup/census.json'), census);
const appPackage = JSON.parse(
  readFileSync(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'), 'utf8'),
);
put(join(appRoot, 'package.json'), appPackage);
link(nextRoot, join(appRoot, 'node_modules/next'));
for (const name of ['sdk', 'ui', 'config']) {
  const target = join(frontendRoot, 'packages', name);
  copy(
    join(FRAMEWORK_FRONTEND_ROOT, 'packages', name, 'package.json'),
    join(target, 'package.json'),
  );
  for (const file of ['tsconfig.json', 'tsconfig.native.json'])
    if (existsSync(join(FRAMEWORK_FRONTEND_ROOT, 'packages', name, file)))
      copy(join(FRAMEWORK_FRONTEND_ROOT, 'packages', name, file), join(target, file));
  for (const folder of ['src', 'scripts', 'tsconfig', 'tailwind', 'eslint'])
    if (existsSync(join(FRAMEWORK_FRONTEND_ROOT, 'packages', name, folder)))
      copy(join(FRAMEWORK_FRONTEND_ROOT, 'packages', name, folder), join(target, folder));
  link(target, join(appRoot, 'node_modules/@learnstack', name));
}
for (const name of [
  'next.config.ts',
  'next-env.d.ts',
  'tsconfig.json',
  'tsconfig.server.json',
  'postcss.config.cjs',
  'tailwind.config.ts',
])
  copy(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web', name), join(appRoot, name));
put(join(appRoot, 'scripts/control.mjs'), 'export const control = true;\n');
put(join(appRoot, 'src/server/control.ts'), 'export const control = true;\n');
put(join(appRoot, 'src/app/page.tsx'), 'export default function Page() { return null; }\n');

function identity() {
  return verifyFrameworkInstallation({ appRoot, frontendRoot });
}
function buildControlManifest() {
  const current = identity();
  put(join(appRoot, '.next/server/control.js'), 'exports.current = true;\n');
  put(join(appRoot, '.next/server/control.json'), { current: true });
  put(join(appRoot, '.next/BUILD_ID'), 'control-build\n');
  put(join(appRoot, '.next/required-server-files.json'), { version: 1 });
  put(join(appRoot, '.server/control.js'), 'export const current = true;\n');
  put(join(frontendRoot, 'packages/sdk/dist/control.js'), 'export const current = true;\n');
  const manifest = {
    format: 1,
    mode: 'production',
    identity: Object.fromEntries(
      ['format', 'tuple', 'censusSha256', 'patchSha256', 'generatorSha256'].map((key) => [
        key,
        current[key],
      ]),
    ),
    producer: {
      node: process.versions.node,
      pnpm: Object.fromEntries(
        ['version', 'sha256', 'metadataSha256'].map((key) => [key, actualManager[key]]),
      ),
    },
    buildOptions: {
      mode: 'production',
      args: [],
      environment: {
        policy: 'closed-compiler-v1',
        NODE_ENV: 'production',
        NODE_PATH: 'workspace/node_modules/.pnpm/node_modules',
        NEXT_TELEMETRY_DISABLED: '1',
        BROWSERSLIST: 'chrome 64, edge 79, firefox 67, opera 51, safari 12',
        SWC_NATIVE_BINDING_CACHE: 'app/.framework-state/swc-native-cache',
      },
    },
    inputs: frameworkInputGraph(current),
    outputs: frameworkOutputGraph(current),
  };
  put(join(appRoot, '.next/framework-build.json'), manifest);
  return manifest;
}
function altered(path, change, assertion) {
  const before = readFileSync(path);
  try {
    put(path, change(before));
    assertion();
  } finally {
    writeFileSync(path, before);
  }
}
try {
  await test('qualified manager probes and actual 59 installed subjects', () => {
    assert.equal(actual.installed.files, 59);
    assert.equal(actualManager.version, actual.tuple.pnpm);
    assert.equal(identity().installed.files, 59);
    verifyFrameworkInstallation({ appRoot, frontendRoot, distribution: true });
  });
  await test('pnpm identity normalizes CRLF but SHA-256 is a distinct carrier identity', () => {
    assert.equal(pnpmPatchHash('a\r\nb\r\n'), pnpmPatchHash('a\nb\n'));
    assert.notEqual(hash('a\r\nb\r\n'), hash('a\nb\n'));
  });
  await test('the actual frontend Node declaration must exist and exactly match the tuple', () => {
    const path = join(frontendRoot, '.nvmrc');
    altered(
      path,
      () => '22.99.0\n',
      () => denied(identity, 'node_declaration'),
    );
    const before = readFileSync(path);
    unlinkSync(path);
    try {
      denied(identity, 'node_declaration');
    } finally {
      writeFileSync(path, before);
    }
  });
  await test('wrong actual Node refuses before package work', () => {
    const descriptor = Object.getOwnPropertyDescriptor(process.versions, 'node');
    try {
      Object.defineProperty(process.versions, 'node', { value: '22.99.0' });
      denied(identity, 'node_version');
    } finally {
      Object.defineProperty(process.versions, 'node', descriptor);
    }
  });
  await test('modified patch refuses', () =>
    altered(
      join(frontendRoot, census.patch.path),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(identity, 'patch_identity'),
    ));
  await test('wrong patch lock identity refuses', () =>
    altered(
      join(frontendRoot, 'pnpm-lock.yaml'),
      (before) =>
        before.toString().replace(census.patch.pnpmHash, 'a'.repeat(census.patch.pnpmHash.length)),
      () => denied(identity, 'patch_identity'),
    ));
  await test('modified generator input refuses', () =>
    altered(
      join(frontendRoot, census.generator.inputs[0].path),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(identity, 'generator_identity'),
    ));
  await test('modified installed JS refuses', () =>
    altered(
      join(nextRoot, census.files.find((file) => file.kind !== 'map').path),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(identity, 'installed_bytes'),
    ));
  await test('modified installed source map refuses', () =>
    altered(
      join(nextRoot, census.files.find((file) => file.kind === 'map').path),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(identity, 'installed_bytes'),
    ));
  await test('extra distribution member refuses only comprehensive mode', () => {
    const path = join(nextRoot, 'unqualified-extra.js');
    put(path, 'module.exports = {};\n');
    try {
      identity();
      denied(
        () => verifyFrameworkInstallation({ appRoot, frontendRoot, distribution: true }),
        'distribution_membership',
      );
    } finally {
      unlinkSync(path);
    }
  });
  await test('reintroduced old map refuses', () => {
    const path = join(nextRoot, census.absentPaths[0]);
    put(path, '{}');
    try {
      denied(identity, 'obsolete_map');
    } finally {
      unlinkSync(path);
    }
  });
  await test('different Next resolution refuses', () => {
    const linkPath = join(appRoot, 'node_modules/next');
    unlinkSync(linkPath);
    link(actual.nextRoot, linkPath);
    try {
      denied(identity, 'next_resolution');
    } finally {
      unlinkSync(linkPath);
      link(nextRoot, linkPath);
    }
  });
  await test('same app lease excludes another producer and retires cleanly', () => {
    const first = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'build' });
    try {
      denied(() => acquireFrameworkLease({ appRoot, frontendRoot, kind: 'native' }), 'owner_busy');
    } finally {
      first.release();
    }
    const next = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'build' });
    next.release();
  });
  await test('unowned executable output refuses without deleting any root', () => {
    put(join(appRoot, '.next/server/old.js'), 'retained');
    try {
      const lease = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'build' });
      try {
        denied(() => cleanFrameworkOutputs(identity(), { owner: lease.record }), 'output_unowned');
      } finally {
        lease.release();
      }
      assert.equal(readFileSync(join(appRoot, '.next/server/old.js'), 'utf8'), 'retained');
    } finally {
      rmSync(join(appRoot, '.next'), { recursive: true });
    }
  });
  await test('only an exact non-executable fresh-install stub may be adopted', () => {
    put(
      join(appRoot, '.next/types/routes.d.ts'),
      '// Auto-generated stub by scripts/ensure-routes-stub.mjs.\n// Replaced by `next dev` / `next build` with real typed-routes definitions.\nexport {};\n',
    );
    const lease = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'build' });
    try {
      cleanFrameworkOutputs(identity(), { owner: lease.record });
      assert.equal(existsSync(join(appRoot, '.next/types/routes.d.ts')), false);
    } finally {
      lease.release();
    }
  });
  await test('synthetic completed manifest validates unchanged exact graphs', () => {
    buildControlManifest();
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('runtime-only local environment values stay outside build identity', () => {
    const path = join(appRoot, '.env.local');
    put(
      path,
      'DEBUG=qualified-runtime-only-control\nLEARNSTACK_PUBLIC_HOP_SECRET=do-not-record-this-control\n',
    );
    try {
      verifyFrameworkBuild({ appRoot, frontendRoot });
      assert.equal(
        readFileSync(join(appRoot, '.next/framework-build.json'), 'utf8').includes(
          'do-not-record-this-control',
        ),
        false,
      );
    } finally {
      unlinkSync(path);
    }
  });
  await test('dotenv compiler flags refuse instead of becoming unbound build inputs', () => {
    const path = join(appRoot, '.env.local');
    put(path, 'NEXT_PRIVATE_LOCAL_WEBPACK=1\n');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'local_environment_keys');
    } finally {
      unlinkSync(path);
    }
  });
  await test('ambient public compile substitutions refuse even formerly conventional names', () => {
    const before = process.env.NEXT_PUBLIC_API_URL;
    process.env.NEXT_PUBLIC_API_URL = 'https://unqualified.invalid';
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'build_environment');
    } finally {
      if (before === undefined) delete process.env.NEXT_PUBLIC_API_URL;
      else process.env.NEXT_PUBLIC_API_URL = before;
    }
  });
  await test('dotenv public substitution refuses without recording the value', () => {
    const path = join(appRoot, '.env.production.local');
    put(path, 'NEXT_PUBLIC_UNREVIEWED=must-not-enter-a-manifest\n');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'local_environment_keys');
    } finally {
      unlinkSync(path);
    }
  });
  await test('a runtime reader excludes install and shared SDK mutation', () => {
    const current = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'production' });
    try {
      denied(() => acquireFrameworkLease({ appRoot, frontendRoot, kind: 'install' }), 'owner_busy');
    } finally {
      current.release();
    }
    const install = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'install' });
    try {
      denied(
        () => acquireFrameworkLease({ appRoot, frontendRoot, kind: 'production' }),
        'owner_busy',
      );
    } finally {
      install.release();
    }
  });
  await test('the actual native bootstrap rejects old output identity before runtime import', () => {
    copy(
      join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/scripts/public-server.mjs'),
      join(appRoot, 'scripts/public-server.mjs'),
    );
    const marker = join(root, 'runtime-imported');
    put(
      join(appRoot, 'scripts/public-server-runtime.mjs'),
      `import {writeFileSync} from 'node:fs'; writeFileSync(${JSON.stringify(marker)}, 'incorrect');\n`,
    );
    const before = readFileSync(join(appRoot, '.next/framework-build.json'));
    const old = JSON.parse(before);
    old.identity.censusSha256 = '0'.repeat(64);
    put(join(appRoot, '.next/framework-build.json'), old);
    try {
      const child = spawnSync(process.execPath, ['scripts/public-server.mjs'], {
        cwd: appRoot,
        encoding: 'utf8',
        timeout: 10_000,
      });
      assert.equal(child.status, 1);
      assert.match(child.stderr, /Framework admission refused: build_manifest/);
      assert.equal(existsSync(marker), false);
    } finally {
      unlinkSync(join(appRoot, 'scripts/public-server.mjs'));
      unlinkSync(join(appRoot, 'scripts/public-server-runtime.mjs'));
      writeFileSync(join(appRoot, '.next/framework-build.json'), before);
    }
  });
  await test('optional executed ESLint configuration presence and bytes are bound', () => {
    const path = join(appRoot, '.eslintrc.cjs');
    put(path, 'module.exports = {root:true};\n');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
      buildControlManifest();
      verifyFrameworkBuild({ appRoot, frontendRoot });
      altered(
        path,
        (before) => Buffer.concat([before, Buffer.from('\n')]),
        () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
      );
      const before = readFileSync(path);
      unlinkSync(path);
      try {
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
      } finally {
        writeFileSync(path, before);
      }
    } finally {
      unlinkSync(path);
      buildControlManifest();
    }
  });
  await test('optional ESLint ignore discovery is bound', () => {
    const path = join(appRoot, '.eslintignore');
    put(path, 'unqualified-control.ts\n');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
    } finally {
      unlinkSync(path);
    }
  });
  for (const name of [
    'next.config.mjs',
    'next.config.js',
    'postcss.config.js',
    '.postcssrc.json',
    'tailwind.config.js',
    '.babelrc',
    'babel.config.cjs',
    'eslint.config.mjs',
    '.browserslistrc',
  ]) {
    await test(`unqualified competing ${name} refuses before Next`, () => {
      const path = join(appRoot, name);
      put(path, 'not evaluated');
      try {
        denied(identity, 'configuration_variant');
      } finally {
        unlinkSync(path);
      }
    });
  }
  for (const field of ['babel', 'postcss', 'eslintConfig', 'browserslist']) {
    await test(`package ${field} loader override refuses before Next`, () =>
      altered(
        join(appRoot, 'package.json'),
        (before) => {
          const value = JSON.parse(before);
          value[field] = {};
          return value;
        },
        () => denied(identity, 'configuration_variant'),
      ));
  }
  await test('native bootstrap rejects a shadow config before application or config evaluation', () => {
    copy(
      join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/scripts/public-server.mjs'),
      join(appRoot, 'scripts/public-server.mjs'),
    );
    const marker = join(root, 'shadow-config-evaluated');
    const runtimeMarker = join(root, 'shadow-runtime-imported');
    put(
      join(appRoot, 'scripts/public-server-runtime.mjs'),
      `import {writeFileSync} from 'node:fs'; writeFileSync(${JSON.stringify(runtimeMarker)}, 'incorrect');\n`,
    );
    buildControlManifest();
    verifyFrameworkBuild({ appRoot, frontendRoot });
    const shadow = join(appRoot, 'next.config.mjs');
    put(
      shadow,
      `import {writeFileSync} from 'node:fs'; writeFileSync(${JSON.stringify(marker)}, 'incorrect'); export default {};\n`,
    );
    try {
      const child = spawnSync(process.execPath, ['scripts/public-server.mjs'], {
        cwd: appRoot,
        encoding: 'utf8',
        timeout: 10_000,
      });
      assert.equal(child.status, 1);
      assert.match(child.stderr, /Framework admission refused: configuration_variant/);
      assert.equal(existsSync(marker), false);
      assert.equal(existsSync(runtimeMarker), false);
    } finally {
      unlinkSync(shadow);
      unlinkSync(join(appRoot, 'scripts/public-server.mjs'));
      unlinkSync(join(appRoot, 'scripts/public-server-runtime.mjs'));
      buildControlManifest();
    }
  });
  await test('old manifest beside corrected package refuses', () =>
    altered(
      join(appRoot, '.next/framework-build.json'),
      (before) => {
        const value = JSON.parse(before);
        value.identity.censusSha256 = '0'.repeat(64);
        return value;
      },
      () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'build_manifest'),
    ));
  for (const relative of [
    '.next/server/control.js',
    '.next/server/control.json',
    '.server/control.js',
  ]) {
    await test(`changed generated ${relative} refuses`, () =>
      altered(
        join(appRoot, relative),
        (before) => Buffer.concat([before, Buffer.from('\n')]),
        () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
      ));
  }
  await test('added generated server code refuses', () => {
    const path = join(appRoot, '.next/server/new.js');
    put(path, 'unqualified');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
    } finally {
      unlinkSync(path);
    }
  });
  await test('deleted generated server code refuses', () => {
    const path = join(appRoot, '.next/server/control.js');
    const before = readFileSync(path);
    unlinkSync(path);
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
    } finally {
      writeFileSync(path, before);
    }
  });
  await test('source edit refuses old generated output', () =>
    altered(
      join(appRoot, 'src/app/page.tsx'),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
    ));
  await test('config edit refuses old generated output', () =>
    altered(
      join(appRoot, 'next.config.ts'),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
    ));
  await test('extra source membership refuses', () => {
    const path = join(appRoot, 'src/app/new.ts');
    put(path, 'export const unqualified = true;');
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
    } finally {
      unlinkSync(path);
    }
  });
  await test('native SDK output substitution refuses', () =>
    altered(
      join(frontendRoot, 'packages/sdk/dist/control.js'),
      (before) => Buffer.concat([before, Buffer.from('\n')]),
      () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
    ));
  await test('post-hoc unknown output directory refuses', () => {
    mkdirSync(join(appRoot, '.next/standalone'));
    try {
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'output_directory');
    } finally {
      rmSync(join(appRoot, '.next/standalone'), { recursive: true });
    }
  });
  await test('unqualified manager executable cannot gain admission by claiming its version', () => {
    const path = join(root, 'pretend-pnpm');
    put(path, `#!${process.execPath}\nconsole.log('9.12.3');\n`);
    denied(
      () => resolveQualifiedPnpm({ frontendRoot, executable: path, lifecycle: true }),
      'manager_binary',
    );
  });
  await test('unqualified manager metadata refuses', () =>
    altered(
      join(frontendRoot, 'framework/next-response-cleanup/pnpm.json'),
      (before) => {
        const value = JSON.parse(before);
        value.files[0].sha256 = '0'.repeat(64);
        return value;
      },
      () =>
        denied(
          () =>
            resolveQualifiedPnpm({
              frontendRoot,
              executable: actualManager.executable,
              lifecycle: true,
            }),
          'manager_metadata',
        ),
    ));
  await test('original completed manifest transfers only to matching fresh fixture', () => {
    const target = join(frontendRoot, 'apps/copied');
    cpSync(appRoot, target, {
      recursive: true,
      filter: (path) =>
        !['.next', '.server', '.framework-state'].some(
          (name) => path === join(appRoot, name) || path.startsWith(join(appRoot, name) + '/'),
        ),
    });
    // cpSync preserves our package links by target rather than materializing them.
    copyQualifiedFrameworkBuild({ sourceAppRoot: appRoot, targetAppRoot: target, frontendRoot });
    assert.deepEqual(
      readFileSync(join(target, '.next/framework-build.json')),
      readFileSync(join(appRoot, '.next/framework-build.json')),
    );
    verifyFrameworkBuild({ appRoot: target, frontendRoot });
    denied(
      () =>
        copyQualifiedFrameworkBuild({
          sourceAppRoot: appRoot,
          targetAppRoot: target,
          frontendRoot,
        }),
      'copy_target_not_empty',
    );
  });
  await test('qualified template copies to a byte-identical fresh isolated SDK without re-signing', () => {
    const target = join(frontendRoot, 'apps/isolated-copy');
    cpSync(appRoot, target, {
      recursive: true,
      filter: (path) =>
        !['.next', '.server', '.framework-state'].some(
          (name) => path === join(appRoot, name) || path.startsWith(join(appRoot, name) + '/'),
        ),
    });
    const sdk = join(frontendRoot, 'packages/isolated-sdk');
    cpSync(join(frontendRoot, 'packages/sdk'), sdk, {
      recursive: true,
      filter: (path) =>
        !['dist', '.framework-state'].some(
          (name) =>
            path === join(frontendRoot, 'packages/sdk', name) ||
            path.startsWith(join(frontendRoot, 'packages/sdk', name) + '/'),
        ),
    });
    unlinkSync(join(target, 'node_modules/@learnstack/sdk'));
    link(sdk, join(target, 'node_modules/@learnstack/sdk'));
    copyQualifiedFrameworkBuild({ sourceAppRoot: appRoot, targetAppRoot: target, frontendRoot });
    assert.deepEqual(
      readFileSync(join(target, '.next/framework-build.json')),
      readFileSync(join(appRoot, '.next/framework-build.json')),
    );
    assert.deepEqual(
      readFileSync(join(sdk, 'dist/control.js')),
      readFileSync(join(frontendRoot, 'packages/sdk/dist/control.js')),
    );
    verifyFrameworkBuild({ appRoot: target, frontendRoot });
  });
  await test('native development refusal acknowledges only unissued write/end callbacks', async () => {
    const outcomes = [];
    const server = createServer((_request, response) => {
      protectFrameworkDevelopmentResponse(response, {
        assert() {
          throw new Error('controlled invalid compilation');
        },
      });
      assert.equal(
        response.write('must not be sent', (error) => outcomes.push(error instanceof Error)),
        false,
      );
      response.end('must not be sent either', (error) => outcomes.push(error instanceof Error));
    });
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    try {
      const observed = await new Promise((resolve, reject) => {
        const call = request(
          { host: '127.0.0.1', port: server.address().port, path: '/', agent: false },
          (response) => {
            let bytes = 0;
            response.on('data', (chunk) => {
              bytes += chunk.length;
            });
            response.on('end', () => resolve({ status: response.statusCode, bytes }));
            response.once('error', reject);
          },
        );
        call.once('error', reject);
        call.end();
      });
      assert.deepEqual(observed, { status: 503, bytes: 0 });
      assert.deepEqual(outcomes, [true, true]);
    } finally {
      await new Promise((resolve, reject) =>
        server.close((error) => (error ? reject(error) : resolve())),
      );
    }
  });
  for (const mode of ['compatible', 'uncleared', 'incompatible', 'retirement']) {
    await test(`real-process metadata mutex ${mode} control`, async () => {
      const result = await runFrameworkMetadataControl(mode);
      assert.equal(result.result, 'pass');
    });
  }
  for (const mode of [
    'sdk-cancel',
    'sdk-direct-death',
    'next-exit',
    'next-success',
    'late-cancel',
    'installer-exit',
    'pnpm-lifecycle',
    'owner-death',
  ]) {
    await test(`real mutable process-family ${mode} retains ownership until actual closure`, async () => {
      const result = await runFrameworkFamilyControl(mode);
      assert.equal(result.result, 'pass');
    });
  }
  for (const mode of ['nonce', 'parent', 'missing-nonce', 'missing-channel']) {
    await test(`private compiler preload refuses invalid ${mode} before command execution`, async () => {
      const marker = join(root, `invalid-preload-${mode}`);
      const script = join(root, `invalid-preload-${mode}.cjs`);
      put(
        script,
        `require('node:fs').writeFileSync(${JSON.stringify(marker)},'must not execute');\n`,
      );
      const environment = {
        PATH: process.env.PATH,
        LEARNSTACK_FRAMEWORK_FAMILY_NONCE: 'a'.repeat(32),
        LEARNSTACK_FRAMEWORK_FAMILY_PARENT: String(process.pid),
      };
      if (mode === 'nonce') environment.LEARNSTACK_FRAMEWORK_FAMILY_NONCE = 'unqualified';
      if (mode === 'parent') environment.LEARNSTACK_FRAMEWORK_FAMILY_PARENT = '0';
      if (mode === 'missing-nonce') delete environment.LEARNSTACK_FRAMEWORK_FAMILY_NONCE;
      const child = spawn(
        process.execPath,
        [
          '--import',
          new URL('./framework-process-family-preload.mjs', import.meta.url).href,
          script,
        ],
        {
          env: environment,
          stdio:
            mode === 'missing-channel'
              ? ['ignore', 'pipe', 'pipe']
              : ['ignore', 'pipe', 'pipe', 'ipc'],
        },
      );
      let stderr = '';
      child.stderr.on('data', (bytes) => {
        stderr += bytes;
      });
      const result = await new Promise((resolve) =>
        child.once('close', (code, signal) => resolve({ code, signal })),
      );
      assert.deepEqual(result, { code: 1, signal: null });
      assert.match(stderr, /Framework private process control refused/);
      assert.equal(existsSync(marker), false);
      unlinkSync(script);
    });
  }
  await test('worker threads skip the main-thread private compiler control', () => {
    const script = join(root, 'preload-worker.mjs');
    const body = join(root, 'preload-worker-body.mjs');
    put(
      body,
      "import {parentPort} from 'node:worker_threads';parentPort.postMessage('worker-ran');\n",
    );
    const preload = new URL('./framework-process-family-preload.mjs', import.meta.url);
    const writeWorker = (path) =>
      put(
        script,
        `import {Worker} from 'node:worker_threads';\n` +
          `const worker=new Worker(${JSON.stringify(body)},` +
          `{execArgv:['--import',${JSON.stringify(path)}],` +
          `env:{LEARNSTACK_FRAMEWORK_FAMILY_NONCE:'invalid',LEARNSTACK_FRAMEWORK_FAMILY_PARENT:'0'}});\n` +
          `worker.on('message',value=>process.stdout.write(value));\n`,
      );
    const mutant = join(root, 'preload-worker-mutant.mjs');
    try {
      writeWorker(preload.href);
      const result = spawnSync(process.execPath, [script], { encoding: 'utf8', timeout: 2500 });
      assert.equal(result.status, 0, result.stderr);
      assert.equal(result.stdout, 'worker-ran');
      // Prove that this actual Worker executes its configured preload. Removing
      // only the main-thread guard must turn the same malformed control into a
      // refusal, rather than silently testing a path that never loads it.
      put(mutant, readFileSync(preload, 'utf8').replace('isMainThread && ', ''));
      writeWorker(mutant);
      const dirty = spawnSync(process.execPath, [script], { encoding: 'utf8', timeout: 2500 });
      assert.notEqual(dirty.status, 0);
      assert.match(dirty.stderr, /Framework private process control refused/);
    } finally {
      unlinkSync(script);
      unlinkSync(body);
      if (existsSync(mutant)) unlinkSync(mutant);
    }
  });
  await test('development compiler rechecks its exact effective browser target', () => {
    const before = process.env.BROWSERSLIST;
    process.env.BROWSERSLIST = 'ie 11';
    try {
      denied(() => verifyFrameworkDevelopment({ appRoot, frontendRoot }), 'build_environment');
    } finally {
      if (before === undefined) delete process.env.BROWSERSLIST;
      else process.env.BROWSERSLIST = before;
    }
  });
  for (const selector of [
    'BROWSERSLIST',
    'BROWSERSLIST_CONFIG',
    'BROWSERSLIST_ENV',
    'BABEL_ENV',
    'ESLINT_USE_FLAT_CONFIG',
    'TS_NODE_PROJECT',
    'SWC_BINARY_PATH',
  ]) {
    await test(`ambient compiler selector ${selector} refuses before producer mutation`, () => {
      const before = process.env[selector];
      process.env[selector] = 'unqualified-control';
      try {
        denied(() => prepareFrameworkBuild({ appRoot, frontendRoot }), 'build_environment');
      } finally {
        if (before === undefined) delete process.env[selector];
        else process.env[selector] = before;
      }
    });
  }
  await test('finite runtime/compiler projection drops arbitrary ambient variables without serializing secrets', () => {
    const names = ['UNQUALIFIED_COMPILER_SELECTOR', 'KEYCLOAK_WEB_CLIENT_SECRET'];
    const before = names.map((key) => process.env[key]);
    process.env.UNQUALIFIED_COMPILER_SELECTOR = 'must never reach the compiler';
    process.env.KEYCLOAK_WEB_CLIENT_SECRET = 'private control value';
    try {
      const value = frameworkRuntimeEnvironment(identity(), 'development');
      assert.equal(value.UNQUALIFIED_COMPILER_SELECTOR, undefined);
      assert.equal(value.KEYCLOAK_WEB_CLIENT_SECRET, 'private control value');
      assert.equal(value.NODE_ENV, 'development');
      assert.equal(value.NEXT_TELEMETRY_DISABLED, '1');
      assert.equal(JSON.stringify(buildControlManifest()).includes('private control value'), false);
    } finally {
      names.forEach((key, index) => {
        if (before[index] === undefined) delete process.env[key];
        else process.env[key] = before[index];
      });
    }
  });
  await test('actual native binding ignores inherited HOME and uses only the owned compiler/runtime cache', async () => {
    const current = identity();
    const lease = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'production' });
    const script = join(root, 'native-binding-owned-probe.mjs');
    const result = join(root, 'native-binding-owned-result.json');
    const cache = prepareFrameworkNativeCache(current, lease.record);
    const parentHome = process.env.HOME;
    process.env.HOME = join(root, 'unqualified-parent-home');
    put(
      script,
      `import {createRequire} from 'node:module';import {writeFileSync} from 'node:fs';\n` +
        `const require=createRequire(${JSON.stringify(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'))});\n` +
        `const plugin=createRequire(require.resolve('next-intl/plugin'));const core=plugin('@swc/core');\n` +
        `const transformed=core.transformSync('const value: number = 1;', {jsc:{parser:{syntax:'typescript'}}});\n` +
        `writeFileSync(${JSON.stringify(result)},JSON.stringify({loaded:typeof core.getBinaryMetadata().target==='string',compiled:typeof transformed.code==='string',ownedCache:process.env.SWC_NATIVE_BINDING_CACHE===${JSON.stringify(cache)},homeAbsent:process.env.HOME===undefined}));\n`,
    );
    try {
      for (const mode of ['production', 'development']) {
        const environment = frameworkRuntimeEnvironment(current, mode);
        assert.equal(environment.HOME, undefined);
        await runFrameworkProcessFamily(process.execPath, [script], {
          cwd: appRoot,
          environment,
          stdio: 'ignore',
        });
        assert.deepEqual(JSON.parse(readFileSync(result)), {
          loaded: true,
          compiled: true,
          ownedCache: true,
          homeAbsent: true,
        });
      }
    } finally {
      if (parentHome === undefined) delete process.env.HOME;
      else process.env.HOME = parentHome;
      lease.release();
      unlinkSync(script);
      if (existsSync(result)) unlinkSync(result);
    }
  });
  for (const mode of ['unknown-populated', 'symlink-root', 'unsafe-mode', 'nested-alias']) {
    await test(`native binding cache refuses ${mode} without clearing or escaping its owner`, () => {
      const target = join(frontendRoot, `cache-${mode}`);
      const outside = join(root, `outside-cache-${mode}`);
      mkdirSync(join(target, 'node_modules/@learnstack'), { recursive: true });
      link(join(frontendRoot, 'packages/sdk'), join(target, 'node_modules/@learnstack/sdk'));
      put(join(outside, 'sentinel'), 'must remain unchanged');
      const lease = acquireFrameworkLease({ appRoot: target, frontendRoot, kind: 'production' });
      const bound = { ...identity(), appRoot: realpathSync(target) };
      const cache = join(target, '.framework-state/swc-native-cache');
      const record = join(target, '.framework-state/native-cache.json');
      try {
        let reason;
        if (mode === 'symlink-root') {
          link(outside, cache);
          reason = 'output_ownership';
        } else if (mode === 'nested-alias') {
          prepareFrameworkNativeCache(bound, lease.record);
          link(outside, join(cache, 'foreign'));
          reason = 'distribution_file';
        } else {
          mkdirSync(cache, { mode: 0o700 });
          if (mode === 'unsafe-mode') chmodSync(cache, 0o755);
          else put(join(cache, 'unknown.node'), 'unowned native bytes');
          reason = 'native_cache_ownership';
        }
        denied(() => prepareFrameworkNativeCache(bound, lease.record), reason);
        if (mode !== 'nested-alias') assert.equal(existsSync(record), false);
        assert.equal(readFileSync(join(outside, 'sentinel'), 'utf8'), 'must remain unchanged');
        if (mode === 'unknown-populated')
          assert.equal(readFileSync(join(cache, 'unknown.node'), 'utf8'), 'unowned native bytes');
      } finally {
        lease.release();
      }
    });
  }
  await test('native binding cache requires the exact current app owner', () => {
    const lease = acquireFrameworkLease({ appRoot, frontendRoot, kind: 'production' });
    try {
      denied(
        () => prepareFrameworkNativeCache(identity(), { ...lease.record, nonce: '0'.repeat(32) }),
        'native_cache_owner',
      );
    } finally {
      lease.release();
    }
  });
  for (const name of ['.browserslistrc', 'browserslist', 'package.json']) {
    await test(`effective browser target ignores unowned ancestor ${name} configuration`, () => {
      const require = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));
      const loader = require('next/dist/compiled/browserslist');
      const { getSupportedBrowsers } = require('next/dist/build/utils.js');
      const path = join(frontendRoot, 'apps', name);
      const old = process.env.BROWSERSLIST;
      const before = JSON.stringify(frameworkInputGraph(identity()));
      put(path, name === 'package.json' ? { browserslist: ['ie 11'] } : 'ie 11\n');
      try {
        delete process.env.BROWSERSLIST;
        loader.clearCaches();
        assert.deepEqual(getSupportedBrowsers(appRoot, false), ['ie 11']);
        process.env.BROWSERSLIST = frameworkRuntimeEnvironment(
          identity(),
          'production',
        ).BROWSERSLIST;
        loader.clearCaches();
        const expected = ['chrome 64', 'edge 79', 'firefox 67', 'opera 51', 'safari 12'];
        assert.deepEqual(getSupportedBrowsers(appRoot, false), expected);
        assert.deepEqual(getSupportedBrowsers(appRoot, true), expected);
        assert.equal(JSON.stringify(frameworkInputGraph(identity())), before);
      } finally {
        unlinkSync(path);
        loader.clearCaches();
        if (old === undefined) delete process.env.BROWSERSLIST;
        else process.env.BROWSERSLIST = old;
      }
    });
  }
  for (const field of ['main', 'exports', 'bin']) {
    await test(`Next package ${field} drift refuses before actual entry resolution/import`, () =>
      altered(
        join(nextRoot, 'package.json'),
        (before) => {
          const value = JSON.parse(before);
          value[field] =
            field === 'main'
              ? '../outside.cjs'
              : field === 'bin'
                ? { next: '../outside.cjs' }
                : { '.': '../outside.cjs' };
          return value;
        },
        () => denied(identity, 'next_metadata'),
      ));
  }
  await test('actual bootstrap refuses an external Next main before executing its marker', () => {
    const external = join(root, 'outside-next-entry.cjs');
    const marker = join(root, 'outside-next-imported');
    put(
      external,
      `require('node:fs').writeFileSync(${JSON.stringify(marker)},'unqualified');module.exports={};\n`,
    );
    copy(
      join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/scripts/public-server.mjs'),
      join(appRoot, 'scripts/public-server.mjs'),
    );
    put(join(appRoot, 'scripts/public-server-runtime.mjs'), "import next from 'next';\n");
    buildControlManifest();
    try {
      altered(
        join(nextRoot, 'package.json'),
        (before) => {
          const value = JSON.parse(before);
          value.main = external;
          return value;
        },
        () => {
          const result = spawnSync(process.execPath, ['scripts/public-server.mjs'], {
            cwd: appRoot,
            encoding: 'utf8',
            timeout: 10000,
          });
          assert.equal(result.status, 1);
          assert.match(result.stderr, /Framework admission refused: next_metadata/);
          assert.equal(existsSync(marker), false);
        },
      );
    } finally {
      unlinkSync(join(appRoot, 'scripts/public-server.mjs'));
      unlinkSync(join(appRoot, 'scripts/public-server-runtime.mjs'));
      unlinkSync(external);
      buildControlManifest();
    }
  });
} finally {
  rmSync(root, { recursive: true, force: true });
}
