// Counterfactual drift controls over copied, qualified package subjects. These
// controls do not claim that a synthetic build manifest was produced by Next.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createServer, request } from 'node:http';
import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { protectFrameworkDevelopmentResponse } from './framework-development.mjs';
import { runFrameworkMetadataControl } from './test-framework-metadata-contention.mjs';
import {
  acquireFrameworkLease,
  cleanFrameworkOutputs,
  copyQualifiedFrameworkBuild,
  FRAMEWORK_FRONTEND_ROOT,
  FrameworkAdmissionError,
  frameworkInputGraph,
  frameworkOutputGraph,
  pnpmPatchHash,
  resolveQualifiedPnpm,
  verifyFrameworkBuild,
  verifyFrameworkInstallation,
} from './framework-installation.mjs';

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
// Membership is a deliberately closed copied subset for this algorithm control;
// the actual 7k-file distribution has its separate install/qualification check.
const census = structuredClone(sourceCensus);
census.distribution.paths = [...census.files.map((file) => file.path), 'package.json'].sort();
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
        NODE_ENV: 'production',
        NODE_PATH: 'workspace/node_modules/.pnpm/node_modules',
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
} finally {
  rmSync(root, { recursive: true, force: true });
}
