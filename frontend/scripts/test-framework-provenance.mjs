// Counterfactual drift controls over copied, qualified package subjects. These
// controls do not claim that a synthetic build manifest was produced by Next.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { createServer, request } from 'node:http';
import {
  cpSync,
  chmodSync,
  existsSync,
  lstatSync,
  linkSync,
  mkdirSync,
  readdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import nodeTest from 'node:test';
import { protectFrameworkDevelopmentResponse } from './framework-development.mjs';
import { runFrameworkMetadataControl } from './test-framework-metadata-contention.mjs';
import { runFrameworkFamilyControl } from './test-framework-process-family.mjs';
import {
  acquireFrameworkLease,
  cleanFrameworkOutputs,
  copyQualifiedFrameworkBuild,
  FRAMEWORK_FRONTEND_ROOT,
  FrameworkAdmissionError,
  frameworkDevelopmentRestartGraph,
  frameworkInputGraph,
  frameworkLeaseSnapshot,
  frameworkOutputGraph,
  frameworkRuntimeEnvironment,
  pnpmPatchHash,
  prepareFrameworkBuild,
  prepareFrameworkNativeCache,
  produceFrameworkBuild,
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
const actualRequire = createRequire(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/package.json'));
const sourceCensus = JSON.parse(
  readFileSync(
    join(FRAMEWORK_FRONTEND_ROOT, 'framework/next-response-cleanup/census.json'),
    'utf8',
  ),
);
const root = mkdtempSync(join(tmpdir(), 'learnstack-framework-provenance-'));
let retainRoot = false;
const activeRuntimeLaunches = new Set();
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

async function test(name, work) {
  const result = await nodeTest(name, work);
  if (retainRoot || activeRuntimeLaunches.size)
    throw new Error('Provenance runtime ownership is uncertain; retained fixture cannot be reused');
  return result;
}
function runtimeCleanup(work) {
  if (!retainRoot && activeRuntimeLaunches.size === 0) work();
}
function copyRuntimeEntries() {
  for (const name of ['public-server.mjs', 'public-runtime-child.mjs'])
    copy(join(FRAMEWORK_FRONTEND_ROOT, 'apps/web/scripts', name), join(appRoot, 'scripts', name));
}
function removeRuntimeEntries() {
  for (const name of ['public-server.mjs', 'public-runtime-child.mjs', 'public-server-runtime.mjs'])
    unlinkSync(join(appRoot, 'scripts', name));
}
function inertRuntimeSource(marker, imported = '') {
  return `import {writeFileSync} from 'node:fs'; import {createServer} from 'node:http';
${imported || "const value = 'admitted';"}
export async function startPublicRuntime(control) {
  let server; let listening; let stopped;
  control.registerShutdown(() => stopped ??= (async () => {
    if (listening) await listening;
    if (server?.listening) {
      const closed = new Promise((resolve,reject) => server.close(error => error ? reject(error) : resolve()));
      server.closeAllConnections(); await closed;
    }
  })());
  if (control.stopRequested()) return;
  server = createServer((_request,response) => response.end(value));
  server.on('error', () => control.requestStop('listener-fault'));
  listening = new Promise((resolve,reject) => {
    server.once('error', reject);
    server.listen(0,'127.0.0.1',() => {
      if (!control.stopRequested()) {
        if (!control.listenerReady(server)) return reject(new Error('Inert listener refused'));
        writeFileSync(${JSON.stringify(marker)}, JSON.stringify({pid:process.pid,parentPid:process.ppid,port:server.address().port,value}));
      }
      resolve();
    });
  });
  await listening;
}
`;
}
function ownerEnvironment(extra = {}) {
  return { PATH: process.env.PATH, NODE_ENV: 'production', NEXT_TELEMETRY_DISABLED: '1', ...extra };
}
function refusedOwner(args = [], environment = {}) {
  const child = spawnSync(process.execPath, [...args, 'scripts/public-server.mjs'], {
    cwd: appRoot,
    encoding: 'utf8',
    timeout: 10_000,
    env: ownerEnvironment(environment),
  });
  if (child.error || child.signal || child.status === null) retainRoot = true;
  return child;
}
function familyAbsent(pid) {
  try {
    process.kill(-pid, 0);
    return false;
  } catch (error) {
    if (error.code === 'ESRCH') return true;
    throw error;
  }
}
async function until(predicate, failure, timeout = 10_000) {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    if (failure?.()) throw new Error('Runtime control exited before its expected observation');
    if (Date.now() >= deadline) throw new Error('Runtime control observation deadline exceeded');
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
function runtimeProcess(entry, options = {}) {
  const child = spawn(process.execPath, [entry], {
    cwd: appRoot,
    env: ownerEnvironment(),
    stdio: ['ignore', 'pipe', 'pipe'],
    ...options,
  });
  activeRuntimeLaunches.add(child);
  let stdout = '';
  let stderr = '';
  let result;
  child.stdout.on('data', (bytes) => {
    stdout += bytes;
  });
  child.stderr.on('data', (bytes) => {
    stderr += bytes;
  });
  child.once('error', () => {
    retainRoot = true;
  });
  const closed = new Promise((resolve) =>
    child.once('close', (code, signal) => {
      result = { code, signal };
      resolve(result);
    }),
  );
  return { child, closed, result: () => result, stderr: () => stderr, stdout: () => stdout };
}
async function actualHttp(marker, expectedParent) {
  const observed = JSON.parse(readFileSync(marker, 'utf8'));
  assert.equal(observed.parentPid, expectedParent);
  assert.equal(Number.isSafeInteger(observed.pid) && observed.pid > 0, true);
  assert.equal(
    Number.isInteger(observed.port) && observed.port > 0 && observed.port <= 65535,
    true,
  );
  const response = await new Promise((resolve, reject) => {
    const call = request(
      { host: '127.0.0.1', port: observed.port, path: '/', agent: false },
      (response) => {
        let body = '';
        response.setEncoding('utf8');
        response.on('data', (bytes) => {
          body += bytes;
        });
        response.once('end', () => resolve({ status: response.statusCode, body }));
        response.once('error', reject);
      },
    );
    call.once('error', reject);
    call.end();
  });
  assert.deepEqual(response, { status: 200, body: 'admitted' });
  assert.equal(observed.value, 'admitted');
  return observed;
}
async function gracefulOwner(marker) {
  const launch = runtimeProcess('scripts/public-server.mjs');
  let runtime;
  try {
    await until(() => existsSync(marker), launch.result);
    runtime = await actualHttp(marker, launch.child.pid);
    assert.equal(launch.result(), undefined);
    assert.equal(launch.child.kill('SIGTERM'), true);
    await until(launch.result);
    assert.deepEqual(await launch.closed, { code: 0, signal: null }, launch.stderr());
    assert.equal(familyAbsent(runtime.pid), true);
    activeRuntimeLaunches.delete(launch.child);
    return runtime;
  } catch (error) {
    retainRoot = true;
    if (!launch.result()) launch.child.kill('SIGTERM');
    throw error;
  }
}
function metadataSnapshot() {
  const entries = [];
  function visit(path) {
    const entry = lstatSync(path);
    assert.equal(entry.isSymbolicLink(), false);
    entries.push({
      path,
      mode: entry.mode & 0o777,
      links: entry.nlink,
      bytes: entry.isFile() ? hash(readFileSync(path)) : null,
    });
    if (entry.isDirectory()) for (const name of readdirSync(path).sort()) visit(join(path, name));
  }
  for (const base of [frontendRoot, appRoot, join(frontendRoot, 'packages/sdk')])
    visit(join(base, '.framework-state'));
  return entries;
}
async function privateRuntime(record, marker, overrides = {}, ipc = true, detached = true) {
  const challenge = randomBytes(16).toString('hex');
  const launch = runtimeProcess('scripts/public-runtime-child.mjs', {
    detached,
    env: {
      ...frameworkRuntimeEnvironment(controlIdentity, record.kind),
      LEARNSTACK_RUNTIME_CHALLENGE: challenge,
      LEARNSTACK_RUNTIME_PARENT: String(process.pid),
      LEARNSTACK_RUNTIME_MODE: record.kind,
      LEARNSTACK_RUNTIME_LEASE_NONCE: record.nonce,
      LEARNSTACK_RUNTIME_INCARNATION: randomBytes(16).toString('hex'),
      ...overrides,
    },
    stdio: ipc ? ['ignore', 'pipe', 'pipe', 'ipc'] : ['ignore', 'pipe', 'pipe'],
  });
  let ready = false;
  let handshake = false;
  launch.child.on('message', (message) => {
    assert.equal(message.tag, 'learnstack-runtime-v1');
    assert.equal(message.challenge, challenge);
    if (message.kind === 'hello') {
      assert.equal(message.pid, launch.child.pid);
      assert.equal(message.parentPid, process.pid);
      handshake = true;
      launch.child.send({ tag: message.tag, kind: 'start', challenge });
    } else if (message.kind === 'ready') ready = true;
  });
  try {
    await until(() => launch.result() || (ready && existsSync(marker)));
    if (ready) {
      const runtime = await actualHttp(marker, process.pid);
      assert.equal(runtime.pid, launch.child.pid);
      launch.child.send({ tag: 'learnstack-runtime-v1', kind: 'stop', challenge, reason: 'abort' });
    }
    await until(launch.result);
    const result = await launch.closed;
    if (detached) await until(() => familyAbsent(launch.child.pid));
    else {
      // This negative created no child-led group. It must refuse before the
      // private handshake/runtime import; its PID is not a family handle.
      assert.equal(result.code, 1);
      assert.equal(handshake, false);
      assert.equal(ready, false);
    }
    activeRuntimeLaunches.delete(launch.child);
    return { ...result, ready, handshake, stderr: launch.stderr() };
  } catch (error) {
    retainRoot = true;
    if (!launch.result()) launch.child.kill('SIGTERM');
    throw error;
  }
}
function identity() {
  return verifyFrameworkInstallation({ appRoot, frontendRoot });
}
// This admitted fixture definition constructs only synthetic expected records
// and pure projections. Every actual positive/refusal gate below still invokes
// the unchanged verifier; no production verification result is cached or skipped.
const controlIdentity = identity();
function buildControlManifest() {
  const current = controlIdentity;
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
function buildControlDevelopmentManifest() {
  const current = controlIdentity;
  const manifest = {
    format: 1,
    mode: 'development',
    identity: Object.fromEntries(
      ['format', 'tuple', 'censusSha256', 'patchSha256', 'generatorSha256'].map((key) => [
        key,
        current[key],
      ]),
    ),
    producer: { node: process.versions.node, pnpm: { version: actualManager.version } },
    resolutions: {
      next: realpathSync(nextRoot),
      '@learnstack/sdk': realpathSync(join(frontendRoot, 'packages/sdk')),
      '@learnstack/ui': realpathSync(join(frontendRoot, 'packages/ui')),
      '@learnstack/config': realpathSync(join(frontendRoot, 'packages/config')),
    },
    inputs: frameworkInputGraph(current, { development: true }),
  };
  put(join(appRoot, '.framework-state/development.json'), manifest);
  return manifest;
}
function pinnedNextResolution(dir, request, development = false, from = dir) {
  const { createWebpackAliases } = actualRequire('next/dist/build/create-compiler-aliases');
  const webpack = actualRequire('next/dist/compiled/webpack/webpack');
  webpack.init();
  const aliases = createWebpackAliases({
    distDir: join(dir, '.next'),
    isClient: true,
    isEdgeServer: false,
    dev: development,
    config: { pageExtensions: ['tsx', 'ts', 'jsx', 'js'], images: {} },
    appDir: join(dir, 'src/app'),
    dir,
    reactProductionProfiling: false,
  });
  // Construct the actual pinned resolver without running a compiler, loading
  // a route, or producing output. The qualified Next config retains this list.
  const compiler = webpack.webpack({
    mode: 'none',
    context: dir,
    entry: {},
    resolve: {
      alias: {
        'private-next-instrumentation-client': aliases['private-next-instrumentation-client'],
        'private-next-empty-module': false,
      },
      extensions: ['.js', '.mjs', '.tsx', '.ts', '.jsx', '.json', '.wasm'],
      extensionAlias: { '.js': ['.ts', '.tsx', '.js'] },
    },
  });
  return compiler.resolverFactory
    .get('normal', { useSyncFileSystemCalls: true })
    .resolveSync({}, from, request);
}
function pinnedClientInstrumentation(dir, development = false) {
  return pinnedNextResolution(dir, 'private-next-instrumentation-client', development);
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
function onlyInputChanged(before, after, path) {
  assert.notDeepEqual(
    before.filter((file) => file.path === path),
    after.filter((file) => file.path === path),
  );
  assert.deepEqual(
    before.filter((file) => file.path !== path),
    after.filter((file) => file.path !== path),
  );
}
try {
  await test('qualified manager probes and actual 59 installed subjects', () => {
    assert.equal(actual.installed.files, 59);
    assert.equal(actualManager.version, actual.tuple.pnpm);
    assert.equal(controlIdentity.installed.files, 59);
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
        denied(
          () => cleanFrameworkOutputs(controlIdentity, { owner: lease.record }),
          'output_unowned',
        );
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
      cleanFrameworkOutputs(controlIdentity, { owner: lease.record });
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
    copyRuntimeEntries();
    const marker = join(root, 'runtime-imported');
    put(join(appRoot, 'scripts/public-server-runtime.mjs'), inertRuntimeSource(marker));
    const before = readFileSync(join(appRoot, '.next/framework-build.json'));
    const old = JSON.parse(before);
    old.identity.censusSha256 = '0'.repeat(64);
    put(join(appRoot, '.next/framework-build.json'), old);
    try {
      const child = refusedOwner();
      assert.equal(child.status, 1);
      assert.match(child.stderr, /Framework admission refused: build_manifest/);
      assert.equal(existsSync(marker), false);
    } finally {
      runtimeCleanup(() => {
        removeRuntimeEntries();
        writeFileSync(join(appRoot, '.next/framework-build.json'), before);
      });
    }
  });
  await test('optional executed ESLint configuration presence and bytes are bound', () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
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
      writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
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
  await test('actual app imports bind test-named helper bytes and membership', () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const { createValidFileMatcher } = actualRequire('next/dist/server/lib/find-page-file');
    const helpers = [
      'test/helper.mjs',
      '__tests__/helper.mjs',
      'shared/js-helper.test.js',
      'shared/ts-helper.test.ts',
      'shared/tsx-helper.test.tsx',
      'shared/jsx-helper.test.jsx',
    ];
    const original = "export const value = 'before';\n";
    const page = join(appRoot, 'src/app/helper-binding/page.js');
    for (const name of helpers) put(join(appRoot, 'src', name), original);
    put(
      page,
      helpers
        .slice(0, 3)
        .map((name, index) => `import {value as value${index}} from '../../${name}';\n`)
        .join('') + 'export default function Page(){return [value0,value1,value2];}\n',
    );
    try {
      assert.equal(
        createValidFileMatcher(
          ['tsx', 'ts', 'jsx', 'js'],
          join(appRoot, 'src/app'),
        ).isAppRouterPage(page),
        true,
      );
      const child = spawnSync(
        process.execPath,
        [
          '--input-type=module',
          '--eval',
          `import Page from ${JSON.stringify(page)};process.stdout.write(JSON.stringify(Page()));`,
        ],
        { encoding: 'utf8', timeout: 2500 },
      );
      assert.equal(child.status, 0, child.stderr);
      assert.deepEqual(JSON.parse(child.stdout), ['before', 'before', 'before']);
      for (const name of helpers) {
        const request = `../../${name.replace(/\.tsx?$/, '.js')}`;
        assert.equal(
          pinnedNextResolution(appRoot, request, false, dirname(page)),
          realpathSync(join(appRoot, 'src', name)),
        );
      }
      const manifest = buildControlManifest();
      verifyFrameworkBuild({ appRoot, frontendRoot });
      const bound = { appRoot: realpathSync(appRoot), frontendRoot: realpathSync(frontendRoot) };
      for (const [index, name] of helpers.entries()) {
        assert.equal(
          manifest.inputs.some((file) => file.path === `app/src/${name}`),
          true,
        );
        const action = ['edit', 'delete', 'add'][index % 3];
        const changed = action === 'add' ? name.replace(/([^/]+)$/, 'extra-$1') : name;
        const path = join(appRoot, 'src', changed);
        if (action === 'delete') unlinkSync(path);
        else put(path, "export const value = 'after';\n");
        try {
          onlyInputChanged(manifest.inputs, frameworkInputGraph(bound), `app/src/${changed}`);
          if (index < 3)
            denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
        } finally {
          if (action === 'add') unlinkSync(path);
          else put(path, original);
        }
      }
      verifyFrameworkBuild({ appRoot, frontendRoot });
    } finally {
      for (const name of helpers) unlinkSync(join(appRoot, 'src', name));
      rmSync(dirname(page), { recursive: true });
      writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
    }
  });
  await test('actual NodeNext native helper resolution binds production, dev and restart inputs', () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const ts = actualRequire('typescript');
    const helpers = ['test/helper.ts', '__tests__/helper.ts', 'shared/helper.test.ts'];
    const original = "export const value = 'before';\n";
    const entry = join(appRoot, 'src/server/ingress.ts');
    const oldBrowser = process.env.BROWSERSLIST;
    for (const name of helpers) put(join(appRoot, 'src/server', name), original);
    put(
      entry,
      helpers
        .map(
          (name, index) =>
            `export {value as value${index}} from './${name.replace(/\.ts$/, '.js')}';\n`,
        )
        .join(''),
    );
    try {
      const config = ts.getParsedCommandLineOfConfigFile(
        join(appRoot, 'tsconfig.server.json'),
        {},
        {
          ...ts.sys,
          onUnRecoverableConfigFileDiagnostic(error) {
            throw new Error(String(error.code));
          },
        },
      );
      assert.equal(config.options.module, ts.ModuleKind.NodeNext);
      assert.equal(config.options.moduleResolution, ts.ModuleResolutionKind.NodeNext);
      assert.equal(config.options.rootDir, join(appRoot, 'src/server'));
      for (const name of helpers) {
        const resolved = ts.resolveModuleName(
          `./${name.replace(/\.ts$/, '.js')}`,
          entry,
          config.options,
          ts.sys,
        ).resolvedModule;
        assert.equal(resolved?.resolvedFileName, join(appRoot, 'src/server', name));
      }
      const manifest = buildControlManifest();
      process.env.BROWSERSLIST = frameworkRuntimeEnvironment(
        { appRoot, frontendRoot },
        'development',
      ).BROWSERSLIST;
      const development = buildControlDevelopmentManifest();
      verifyFrameworkBuild({ appRoot, frontendRoot });
      verifyFrameworkDevelopment({ appRoot, frontendRoot });
      const bound = { appRoot: realpathSync(appRoot), frontendRoot: realpathSync(frontendRoot) };
      const restart = frameworkDevelopmentRestartGraph(bound);
      for (const [index, name] of helpers.entries()) {
        const action = ['edit', 'delete', 'add'][index];
        const changed = action === 'add' ? name.replace(/([^/]+)$/, 'extra-$1') : name;
        const path = join(appRoot, 'src/server', changed);
        if (action === 'delete') unlinkSync(path);
        else put(path, "export const value = 'after';\n");
        try {
          const input = `app/src/server/${changed}`;
          onlyInputChanged(manifest.inputs, frameworkInputGraph(bound), input);
          onlyInputChanged(
            development.inputs,
            frameworkInputGraph(bound, { development: true }),
            input,
          );
          onlyInputChanged(restart, frameworkDevelopmentRestartGraph(bound), input);
          if (index === 2)
            denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
          denied(() => verifyFrameworkDevelopment({ appRoot, frontendRoot }), 'graph_identity');
        } finally {
          if (action === 'add') unlinkSync(path);
          else put(path, original);
        }
      }
      verifyFrameworkBuild({ appRoot, frontendRoot });
      verifyFrameworkDevelopment({ appRoot, frontendRoot });
    } finally {
      for (const name of helpers) unlinkSync(join(appRoot, 'src/server', name));
      unlinkSync(entry);
      if (oldBrowser === undefined) delete process.env.BROWSERSLIST;
      else process.env.BROWSERSLIST = oldBrowser;
      writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
    }
  });
  await test('root dotenv rotation stays private while every reserved source entry refuses', () => {
    verifyFrameworkBuild({ appRoot, frontendRoot });
    const bound = { appRoot: realpathSync(appRoot), frontendRoot: realpathSync(frontendRoot) };
    const production = frameworkInputGraph(bound);
    const development = frameworkInputGraph(bound, { development: true });
    const restart = frameworkDevelopmentRestartGraph(bound);
    const rootEnvironment = join(appRoot, '.env.local');
    assert.equal(existsSync(rootEnvironment), false);
    put(rootEnvironment, 'DEBUG=private-fixture-before\n');
    try {
      assert.deepEqual(frameworkInputGraph(bound), production);
      assert.deepEqual(frameworkInputGraph(bound, { development: true }), development);
      assert.deepEqual(frameworkDevelopmentRestartGraph(bound), restart);
      verifyFrameworkBuild({ appRoot, frontendRoot });
      put(rootEnvironment, 'DEBUG=private-fixture-after\n');
      assert.deepEqual(frameworkInputGraph(bound), production);
      assert.deepEqual(frameworkInputGraph(bound, { development: true }), development);
      assert.deepEqual(frameworkDevelopmentRestartGraph(bound), restart);
      verifyFrameworkBuild({ appRoot, frontendRoot });
      const importedRaw = join(appRoot, 'src/test/.env.local');
      const page = join(appRoot, 'src/app/private-source/page.js');
      put(importedRaw, "export const value='reserved-raw-code';\n");
      put(
        page,
        "import {value} from '../../test/.env.local';export default function Page(){return value;}\n",
      );
      try {
        assert.equal(
          pinnedNextResolution(appRoot, '../../test/.env.local', false, dirname(page)),
          realpathSync(importedRaw),
        );
      } finally {
        rmSync(dirname(page), { recursive: true });
      }
      chmodSync(importedRaw, 0o000);
      try {
        denied(() => frameworkInputGraph(bound), 'private_source');
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'private_source');
        const outputs = frameworkOutputGraph(bound);
        const manifest = readFileSync(join(appRoot, '.next/framework-build.json'));
        assert.equal(existsSync(join(appRoot, '.framework-state/writer.json')), false);
        denied(() => prepareFrameworkBuild({ appRoot, frontendRoot }), 'private_source');
        assert.deepEqual(frameworkOutputGraph(bound), outputs);
        assert.deepEqual(readFileSync(join(appRoot, '.next/framework-build.json')), manifest);
        assert.equal(existsSync(join(appRoot, '.framework-state/writer.json')), false);
        // Ordinary UI source stays outside the native dev/restart graph.
        assert.deepEqual(frameworkInputGraph(bound, { development: true }), development);
        assert.deepEqual(frameworkDevelopmentRestartGraph(bound), restart);
      } finally {
        chmodSync(importedRaw, 0o600);
        unlinkSync(importedRaw);
      }
      const executable = join(appRoot, 'src/server/.env.mjs');
      put(executable, "export const value='reserved-code';\n");
      const child = spawnSync(
        process.execPath,
        [
          '--input-type=module',
          '--eval',
          `import {value} from ${JSON.stringify(executable)};process.stdout.write(value);`,
        ],
        { encoding: 'utf8', timeout: 2500 },
      );
      assert.equal(child.status, 0, child.stderr);
      assert.equal(child.stdout, 'reserved-code');
      unlinkSync(executable);
      for (const name of [
        '.env',
        '.env.local',
        '.ENV.development',
        '.env.mjs',
        '.ENV.ts',
        '.env.private.json',
      ]) {
        const path = join(appRoot, 'src/server', name);
        put(path, 'never read as source');
        chmodSync(path, 0o000);
        try {
          denied(() => frameworkInputGraph(bound), 'private_source');
          denied(() => frameworkInputGraph(bound, { development: true }), 'private_source');
          denied(() => frameworkDevelopmentRestartGraph(bound), 'private_source');
        } finally {
          chmodSync(path, 0o600);
          unlinkSync(path);
        }
      }
      const directory = join(appRoot, 'src/server/.env.private');
      put(join(directory, 'helper.mjs'), 'never traverse this reserved directory');
      try {
        denied(() => frameworkInputGraph(bound), 'private_source');
      } finally {
        rmSync(directory, { recursive: true });
      }
      const alias = join(appRoot, 'src/server/.env.alias');
      link(join(root, 'absent-private-target'), alias);
      try {
        denied(() => frameworkInputGraph(bound), 'private_source');
      } finally {
        unlinkSync(alias);
      }
    } finally {
      unlinkSync(rootEnvironment);
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('the actual pinned router selects the admitted canonical src/app layout', () => {
    const { findPagesDir } = actualRequire('next/dist/lib/find-pages-dir');
    verifyFrameworkBuild({ appRoot, frontendRoot });
    assert.deepEqual(findPagesDir(appRoot), {
      pagesDir: undefined,
      appDir: join(appRoot, 'src/app'),
    });
  });
  await test('all actual competing Next route selections refuse the same admitted manifest', () => {
    const { findPagesDir } = actualRequire('next/dist/lib/find-pages-dir');
    // The preceding canonical-layout control admitted this exact manifest.
    const manifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    for (const name of ['app', 'pages', 'src/pages']) {
      const path = join(appRoot, name);
      put(join(path, 'page.tsx'), 'export default function Page(){return "one"}\n');
      try {
        const selection = findPagesDir(appRoot);
        assert.equal(name === 'app' ? selection.appDir : selection.pagesDir, path);
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
        denied(() => frameworkInputGraph({ appRoot, frontendRoot }), 'route_layout');
        put(join(path, 'page.tsx'), 'export default function Page(){return "two"}\n');
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
        assert.deepEqual(readFileSync(join(appRoot, '.next/framework-build.json')), manifest);
      } finally {
        rmSync(path, { recursive: true });
      }
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('source-less installation remains possible but graph, producer and startup refuse', () => {
    // The preceding route table restored and admitted the original manifest.
    const source = join(appRoot, 'src');
    const removed = join(root, 'temporarily-removed-src');
    renameSync(source, removed);
    try {
      identity();
      denied(() => frameworkInputGraph({ appRoot, frontendRoot }), 'route_layout');
      denied(() => prepareFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
      denied(() => frameworkDevelopmentRestartGraph({ appRoot, frontendRoot }), 'route_layout');
    } finally {
      renameSync(removed, source);
    }
  });
  await test('qualified source directories cannot be aliases', () => {
    verifyFrameworkBuild({ appRoot, frontendRoot });
    for (const name of ['src', 'src/app']) {
      const path = join(appRoot, name);
      const displaced = join(root, `displaced-${name.replaceAll('/', '-')}`);
      renameSync(path, displaced);
      link(displaced, path);
      try {
        denied(identity, 'route_layout');
      } finally {
        unlinkSync(path);
        renameSync(displaced, path);
      }
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('all live and dangling competing router aliases refuse without following them', () => {
    // Share the exact baseline just restored/admitted by the source-alias table.
    for (const name of ['app', 'pages', 'src/pages']) {
      for (const dangling of [false, true]) {
        const path = join(appRoot, name);
        link(dangling ? join(root, 'absent-router-target') : join(appRoot, 'src/app'), path);
        try {
          denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
        } finally {
          unlinkSync(path);
        }
      }
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('actual case-insensitive router selection cannot bypass lowercase probes', () => {
    const { findPagesDir } = actualRequire('next/dist/lib/find-pages-dir');
    for (const name of ['App', 'Pages', 'src/Pages']) {
      const path = join(appRoot, name);
      put(join(path, 'page.tsx'), 'export default function Page(){return null}\n');
      try {
        const lower = join(appRoot, name.toLowerCase());
        const selected = findPagesDir(appRoot);
        if (selected.appDir === lower || selected.pagesDir === lower)
          denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
        else {
          assert.equal(existsSync(lower), false);
          if (name === 'src/Pages') {
            // Not a selected router on this filesystem, but still part of the
            // complete production source graph: the old manifest must refuse.
            denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
            const manifestPath = join(appRoot, '.next/framework-build.json');
            const previousManifest = readFileSync(manifestPath);
            try {
              const manifest = buildControlManifest();
              assert.equal(
                manifest.inputs.some((file) => file.path === 'app/src/Pages/page.tsx'),
                true,
              );
              verifyFrameworkBuild({ appRoot, frontendRoot });
            } finally {
              writeFileSync(manifestPath, previousManifest);
            }
          } else verifyFrameworkBuild({ appRoot, frontendRoot });
        }
      } finally {
        rmSync(path, { recursive: true });
      }
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('every actual client instrumentation root file fallback refuses', () => {
    for (const extension of ['', '.js', '.mjs', '.tsx', '.ts', '.jsx', '.json', '.wasm']) {
      assert.equal(pinnedClientInstrumentation(appRoot), false);
      const path = join(appRoot, `instrumentation-client${extension}`);
      put(path, 'not evaluated');
      try {
        assert.equal(pinnedClientInstrumentation(appRoot), realpathSync(path));
        assert.equal(pinnedClientInstrumentation(appRoot, true), realpathSync(path));
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
        put(path, 'changed, never evaluated');
        denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'route_layout');
      } finally {
        unlinkSync(path);
      }
    }
    verifyFrameworkBuild({ appRoot, frontendRoot });
  });
  await test('actual client instrumentation directory fallback and dangling alias refuse', () => {
    const path = join(appRoot, 'instrumentation-client');
    put(join(path, 'index.js'), 'not evaluated');
    try {
      assert.equal(pinnedClientInstrumentation(appRoot), realpathSync(join(path, 'index.js')));
      denied(identity, 'route_layout');
    } finally {
      rmSync(path, { recursive: true });
    }
    link(join(root, 'absent-instrumentation-target'), path);
    try {
      denied(identity, 'route_layout');
    } finally {
      unlinkSync(path);
    }
  });
  await test('selected source hooks are bound and unrelated root server hooks remain unreachable', async () => {
    verifyFrameworkBuild({ appRoot, frontendRoot });
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const { findPagesDir } = actualRequire('next/dist/lib/find-pages-dir');
    const { getFilesInDir } = actualRequire('next/dist/lib/get-files-in-dir');
    const { getPossibleMiddlewareFilenames, getPossibleInstrumentationHookFilenames } =
      actualRequire('next/dist/build/utils.js');
    const extensions = ['tsx', 'ts', 'jsx', 'js'];
    const sourceFiles = ['middleware.ts', 'instrumentation.ts', 'instrumentation-client.ts'];
    const rootFiles = ['middleware.ts', 'instrumentation.ts'];
    const beforeDevelopment = JSON.stringify(
      frameworkInputGraph(controlIdentity, { development: true }),
    );
    try {
      for (const name of sourceFiles) put(join(appRoot, 'src', name), 'export {};\n');
      for (const name of rootFiles) put(join(appRoot, name), 'not evaluated');
      const selected = findPagesDir(appRoot);
      const hookRoot = dirname(selected.pagesDir || selected.appDir);
      assert.equal(hookRoot, join(appRoot, 'src'));
      const productionHooks = [...(await getFilesInDir(hookRoot))].filter((name) =>
        /^(?:middleware|instrumentation)\.(?:tsx|ts|jsx|js)$/.test(name),
      );
      assert.deepEqual(productionHooks.sort(), ['instrumentation.ts', 'middleware.ts']);
      const developmentHooks = [
        ...getPossibleMiddlewareFilenames(hookRoot, extensions),
        ...getPossibleInstrumentationHookFilenames(hookRoot, extensions),
      ];
      for (const name of rootFiles)
        assert.equal(developmentHooks.includes(join(appRoot, name)), false);
      assert.equal(
        pinnedClientInstrumentation(appRoot),
        realpathSync(join(appRoot, 'src/instrumentation-client.ts')),
      );
      assert.equal(
        JSON.stringify(frameworkInputGraph(controlIdentity, { development: true })),
        beforeDevelopment,
      );
      denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity');
      buildControlManifest();
      verifyFrameworkBuild({ appRoot, frontendRoot });
      altered(
        join(appRoot, 'src/instrumentation.ts'),
        () => 'export const changed = true;\n',
        () => denied(() => verifyFrameworkBuild({ appRoot, frontendRoot }), 'graph_identity'),
      );
    } finally {
      for (const name of sourceFiles) unlinkSync(join(appRoot, 'src', name));
      for (const name of rootFiles) unlinkSync(join(appRoot, name));
      writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
    }
  });
  await test('development compiler/restart and fresh-template copy refuse unsupported layout', () => {
    const old = process.env.BROWSERSLIST;
    process.env.BROWSERSLIST = frameworkRuntimeEnvironment(
      controlIdentity,
      'development',
    ).BROWSERSLIST;
    buildControlDevelopmentManifest();
    const target = join(frontendRoot, 'apps/route-layout-copy');
    cpSync(appRoot, target, {
      recursive: true,
      filter: (path) =>
        !['.next', '.server', '.framework-state'].some(
          (name) => path === join(appRoot, name) || path.startsWith(join(appRoot, name) + '/'),
        ),
    });
    try {
      verifyFrameworkDevelopment({ appRoot, frontendRoot });
      const restart = JSON.stringify(frameworkDevelopmentRestartGraph(controlIdentity));
      put(join(appRoot, 'src/app/ordinary-hmr.tsx'), 'export const ordinary = true;\n');
      verifyFrameworkDevelopment({ appRoot, frontendRoot });
      assert.equal(JSON.stringify(frameworkDevelopmentRestartGraph(controlIdentity)), restart);
      unlinkSync(join(appRoot, 'src/app/ordinary-hmr.tsx'));
      for (const name of [
        'app/page.tsx',
        'pages/page.tsx',
        'src/pages/page.tsx',
        'instrumentation-client.ts',
      ]) {
        const path = join(appRoot, name);
        put(path, 'not evaluated');
        try {
          denied(() => verifyFrameworkDevelopment({ appRoot, frontendRoot }), 'route_layout');
          denied(() => frameworkDevelopmentRestartGraph({ appRoot, frontendRoot }), 'route_layout');
        } finally {
          if (name === 'instrumentation-client.ts') unlinkSync(path);
          else rmSync(dirname(path), { recursive: true });
        }
      }
      verifyFrameworkDevelopment({ appRoot, frontendRoot });
      verifyFrameworkBuild({ appRoot, frontendRoot });
      // Source is admitted first; only the fresh target gains the competing
      // root. Its refused transfer must never create executable output.
      const baseline = frameworkInputGraph(
        verifyFrameworkInstallation({ appRoot: target, frontendRoot }),
      );
      assert.deepEqual(baseline, frameworkInputGraph(controlIdentity));
      put(join(target, 'app/page.tsx'), 'not evaluated');
      denied(
        () =>
          copyQualifiedFrameworkBuild({
            sourceAppRoot: appRoot,
            targetAppRoot: target,
            frontendRoot,
          }),
        'route_layout',
      );
      assert.equal(existsSync(join(target, '.next')), false);
      assert.equal(existsSync(join(target, '.server')), false);
    } finally {
      rmSync(target, { recursive: true, force: true });
      if (old === undefined) delete process.env.BROWSERSLIST;
      else process.env.BROWSERSLIST = old;
    }
  });
  await test('unchanged native bootstrap refuses root routes and imported helper drift before its marker', async () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    copyRuntimeEntries();
    const marker = join(root, 'route-runtime-imported');
    const helper = join(appRoot, 'src/test/runtime-helper.mjs');
    put(helper, "export const value='admitted';\n");
    put(
      join(appRoot, 'scripts/public-server-runtime.mjs'),
      inertRuntimeSource(marker, "import {value} from '../src/test/runtime-helper.mjs';"),
    );
    buildControlManifest();
    const run = () => refusedOwner();
    try {
      await gracefulOwner(marker);
      unlinkSync(marker);
      const manifest = readFileSync(join(appRoot, '.next/framework-build.json'));
      for (const name of [
        'app/page.tsx',
        'pages/page.tsx',
        'src/pages/page.tsx',
        'instrumentation-client.ts',
      ]) {
        const path = join(appRoot, name);
        put(path, 'never imported');
        try {
          const result = run();
          assert.equal(result.status, 1);
          assert.match(result.stderr, /Framework admission refused: route_layout/);
          assert.equal(existsSync(marker), false);
          assert.deepEqual(readFileSync(join(appRoot, '.next/framework-build.json')), manifest);
        } finally {
          if (name === 'instrumentation-client.ts') unlinkSync(path);
          else rmSync(dirname(path), { recursive: true });
        }
      }
      const added = join(appRoot, 'src/__tests__/extra.test.js');
      for (const action of ['edit', 'delete', 'add']) {
        if (action === 'edit') put(helper, "export const value='changed';\n");
        else if (action === 'delete') unlinkSync(helper);
        else put(added, 'export const extra=true;\n');
        try {
          const result = run();
          assert.equal(result.status, 1);
          assert.match(result.stderr, /Framework admission refused: graph_identity/);
          assert.equal(existsSync(marker), false);
          assert.deepEqual(readFileSync(join(appRoot, '.next/framework-build.json')), manifest);
        } finally {
          if (action === 'add') unlinkSync(added);
          else put(helper, "export const value='admitted';\n");
        }
      }
    } finally {
      runtimeCleanup(() => {
        removeRuntimeEntries();
        unlinkSync(helper);
        if (existsSync(marker)) unlinkSync(marker);
        writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
      });
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
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    copyRuntimeEntries();
    const marker = join(root, 'shadow-config-evaluated');
    const runtimeMarker = join(root, 'shadow-runtime-imported');
    put(join(appRoot, 'scripts/public-server-runtime.mjs'), inertRuntimeSource(runtimeMarker));
    buildControlManifest();
    verifyFrameworkBuild({ appRoot, frontendRoot });
    const shadow = join(appRoot, 'next.config.mjs');
    put(
      shadow,
      `import {writeFileSync} from 'node:fs'; writeFileSync(${JSON.stringify(marker)}, 'incorrect'); export default {};\n`,
    );
    try {
      const child = refusedOwner();
      assert.equal(child.status, 1);
      assert.match(child.stderr, /Framework admission refused: configuration_variant/);
      assert.equal(existsSync(marker), false);
      assert.equal(existsSync(runtimeMarker), false);
    } finally {
      runtimeCleanup(() => {
        unlinkSync(shadow);
        removeRuntimeEntries();
        writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
      });
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
    const qualified = copyQualifiedFrameworkBuild({
      sourceAppRoot: appRoot,
      targetAppRoot: target,
      frontendRoot,
    });
    assert.equal(qualified.identity.appRoot, realpathSync(target));
    assert.deepEqual(
      readFileSync(join(target, '.next/framework-build.json')),
      readFileSync(join(appRoot, '.next/framework-build.json')),
    );
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
    const qualified = copyQualifiedFrameworkBuild({
      sourceAppRoot: appRoot,
      targetAppRoot: target,
      frontendRoot,
    });
    assert.equal(qualified.identity.appRoot, realpathSync(target));
    assert.deepEqual(
      readFileSync(join(target, '.next/framework-build.json')),
      readFileSync(join(appRoot, '.next/framework-build.json')),
    );
    assert.deepEqual(
      readFileSync(join(sdk, 'dist/control.js')),
      readFileSync(join(frontendRoot, 'packages/sdk/dist/control.js')),
    );
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
      const value = frameworkRuntimeEnvironment(controlIdentity, 'development');
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
    const current = controlIdentity;
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
      const bound = { ...controlIdentity, appRoot: realpathSync(target) };
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
        () =>
          prepareFrameworkNativeCache(controlIdentity, { ...lease.record, nonce: '0'.repeat(32) }),
        'native_cache_owner',
      );
    } finally {
      lease.release();
    }
  });
  for (const name of ['.browserslistrc', 'browserslist', 'package.json']) {
    await test(`effective browser target ignores unowned ancestor ${name} configuration`, () => {
      const loader = actualRequire('next/dist/compiled/browserslist');
      const { getSupportedBrowsers } = actualRequire('next/dist/build/utils.js');
      const path = join(frontendRoot, 'apps', name);
      const old = process.env.BROWSERSLIST;
      const before = JSON.stringify(frameworkInputGraph(controlIdentity));
      put(path, name === 'package.json' ? { browserslist: ['ie 11'] } : 'ie 11\n');
      try {
        delete process.env.BROWSERSLIST;
        loader.clearCaches();
        assert.deepEqual(getSupportedBrowsers(appRoot, false), ['ie 11']);
        process.env.BROWSERSLIST = frameworkRuntimeEnvironment(
          controlIdentity,
          'production',
        ).BROWSERSLIST;
        loader.clearCaches();
        const expected = ['chrome 64', 'edge 79', 'firefox 67', 'opera 51', 'safari 12'];
        assert.deepEqual(getSupportedBrowsers(appRoot, false), expected);
        assert.deepEqual(getSupportedBrowsers(appRoot, true), expected);
        assert.equal(JSON.stringify(frameworkInputGraph(controlIdentity)), before);
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
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const external = join(root, 'outside-next-entry.cjs');
    const marker = join(root, 'outside-next-imported');
    put(
      external,
      `require('node:fs').writeFileSync(${JSON.stringify(marker)},'unqualified');module.exports={};\n`,
    );
    copyRuntimeEntries();
    put(
      join(appRoot, 'scripts/public-server-runtime.mjs'),
      "import 'next';\n" + inertRuntimeSource(marker),
    );
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
          const result = refusedOwner();
          assert.equal(result.status, 1);
          assert.match(result.stderr, /Framework admission refused: next_metadata/);
          assert.equal(existsSync(marker), false);
        },
      );
    } finally {
      runtimeCleanup(() => {
        removeRuntimeEntries();
        unlinkSync(external);
        writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
      });
    }
  });
  for (const mode of ['production', 'development']) {
    await test(`private runtime ${mode} verifies unchanged parent ownership and refuses binding drift`, async () => {
      const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
      const developmentPath = join(appRoot, '.framework-state/development.json');
      const previousDevelopment = existsSync(developmentPath)
        ? readFileSync(developmentPath)
        : undefined;
      const marker = join(root, `private-runtime-${mode}`);
      copyRuntimeEntries();
      put(join(appRoot, 'scripts/public-server-runtime.mjs'), inertRuntimeSource(marker));
      buildControlManifest();
      buildControlDevelopmentManifest();
      const lease = acquireFrameworkLease({ appRoot, frontendRoot, kind: mode });
      try {
        prepareFrameworkNativeCache(controlIdentity, lease.record);
        const evidence = frameworkLeaseSnapshot(controlIdentity, lease.record);
        assert.equal(Object.isFrozen(evidence), true);
        assert.equal(Object.isFrozen(evidence.records), true);
        assert.equal(evidence.records.length, 3);
        assert.equal(
          evidence.records[0].path,
          join(realpathSync(frontendRoot), '.framework-state/readers', lease.record.nonce),
        );
        assert.equal(
          evidence.records[1].path,
          join(realpathSync(appRoot), '.framework-state/writer.json'),
        );
        assert.equal(
          evidence.records[2].path,
          join(
            realpathSync(frontendRoot),
            'packages/sdk/.framework-state',
            mode === 'production' ? `readers/${lease.record.nonce}` : 'writer.json',
          ),
        );
        const before = metadataSnapshot();
        const baseline = await privateRuntime(lease.record, marker);
        assert.deepEqual(
          {
            code: baseline.code,
            signal: baseline.signal,
            handshake: baseline.handshake,
            ready: baseline.ready,
          },
          { code: 0, signal: null, handshake: true, ready: true },
          baseline.stderr,
        );
        assert.deepEqual(
          metadataSnapshot(),
          before,
          'Child validation/startup must not acquire a lease or mutate cache metadata',
        );
        unlinkSync(marker);
        const recordPath = evidence.records[1].path;
        const cachePath = join(appRoot, '.framework-state/native-cache.json');
        const statePath = join(appRoot, '.framework-state');
        const changes = [
          { name: 'non-detached child', detached: false },
          {
            name: 'parent PID',
            environment: { LEARNSTACK_RUNTIME_PARENT: String(process.pid + 1) },
          },
          { name: 'lease nonce', environment: { LEARNSTACK_RUNTIME_LEASE_NONCE: '0'.repeat(32) } },
          {
            name: 'mode',
            environment: {
              LEARNSTACK_RUNTIME_MODE: mode === 'production' ? 'development' : 'production',
            },
          },
          {
            name: 'cache metadata',
            path: cachePath,
            change: () =>
              put(cachePath, { ...JSON.parse(readFileSync(cachePath)), root: root + '/wrong-app' }),
          },
          {
            name: 'parent record',
            path: recordPath,
            change: () => put(recordPath, { ...lease.record, pid: lease.record.pid + 1 }),
          },
          {
            name: 'record hard link',
            change: () => linkSync(recordPath, join(root, 'private-record-alias')),
            restore: () => unlinkSync(join(root, 'private-record-alias')),
          },
          {
            name: 'record permission',
            change: () => chmodSync(recordPath, 0o644),
            restore: () => chmodSync(recordPath, 0o600),
          },
          {
            name: 'owner directory permission',
            change: () => chmodSync(statePath, 0o755),
            restore: () => chmodSync(statePath, 0o700),
          },
        ];
        for (const change of changes.filter(
          (change) => mode === 'production' || ['lease nonce', 'mode'].includes(change.name),
        )) {
          // Requalify exact parent records immediately before this one mutation.
          assert.deepEqual(frameworkLeaseSnapshot(controlIdentity, lease.record), evidence);
          const original = change.path ? readFileSync(change.path) : undefined;
          try {
            change.change?.();
            const changed = metadataSnapshot();
            const result = await privateRuntime(
              lease.record,
              marker,
              change.environment,
              true,
              change.detached ?? true,
            );
            assert.equal(result.code, 1, change.name);
            assert.equal(result.signal, null, change.name);
            assert.equal(result.handshake, false, change.name);
            assert.equal(result.ready, false, change.name);
            assert.equal(existsSync(marker), false, change.name);
            assert.match(result.stderr, /Framework private runtime admission failed/);
            assert.deepEqual(
              metadataSnapshot(),
              changed,
              `${change.name}: refusal remains read-only`,
            );
          } finally {
            runtimeCleanup(() => {
              if (change.path) writeFileSync(change.path, original);
              change.restore?.();
              if (existsSync(marker)) unlinkSync(marker);
            });
          }
        }
        assert.deepEqual(frameworkLeaseSnapshot(controlIdentity, lease.record), evidence);
        assert.deepEqual(metadataSnapshot(), before);
      } finally {
        runtimeCleanup(() => {
          lease.release();
          removeRuntimeEntries();
          if (existsSync(marker)) unlinkSync(marker);
          writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
          if (previousDevelopment) writeFileSync(developmentPath, previousDevelopment);
          else unlinkSync(developmentPath);
        });
      }
    });
  }
  await test('direct private runtime entry and owner Node flags refuse before inert listener allocation', async () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const marker = join(root, 'entry-flags-runtime');
    copyRuntimeEntries();
    put(join(appRoot, 'scripts/public-server-runtime.mjs'), inertRuntimeSource(marker));
    buildControlManifest();
    try {
      verifyFrameworkBuild({ appRoot, frontendRoot });
      const before = metadataSnapshot();
      const direct = spawnSync(process.execPath, ['scripts/public-runtime-child.mjs'], {
        cwd: appRoot,
        env: ownerEnvironment(),
        encoding: 'utf8',
        timeout: 10_000,
      });
      if (direct.error || direct.signal || direct.status === null) retainRoot = true;
      assert.equal(direct.status, 1);
      assert.match(direct.stderr, /Framework private runtime admission failed/);
      assert.equal(existsSync(marker), false);
      assert.deepEqual(metadataSnapshot(), before);
      for (const [args, environment] of [
        [['--no-warnings'], {}],
        [[], { NODE_OPTIONS: '--no-warnings' }],
        [[], { NODE_OPTIONS: '' }],
      ]) {
        const refused = refusedOwner(args, environment);
        assert.equal(refused.status, 1);
        assert.match(refused.stderr, /Framework admission refused: runtime_arguments/);
        assert.equal(existsSync(marker), false);
        assert.deepEqual(metadataSnapshot(), before);
      }
    } finally {
      runtimeCleanup(() => {
        removeRuntimeEntries();
        if (existsSync(marker)) unlinkSync(marker);
        writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
      });
    }
  });
  await test('runtime boundary inputs are restart-critical while ordinary route source remains HMR eligible', () => {
    const previousManifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    copyRuntimeEntries();
    put(
      join(appRoot, 'scripts/public-server-runtime.mjs'),
      inertRuntimeSource(join(root, 'unused-restart-marker')),
    );
    buildControlManifest();
    try {
      verifyFrameworkBuild({ appRoot, frontendRoot });
      const before = frameworkDevelopmentRestartGraph(controlIdentity);
      for (const [path, input] of [
        [
          join(appRoot, 'scripts/public-server-runtime.mjs'),
          'app/scripts/public-server-runtime.mjs',
        ],
        [join(appRoot, 'scripts/public-runtime-child.mjs'), 'app/scripts/public-runtime-child.mjs'],
        [
          join(frontendRoot, 'scripts/framework-runtime-child-control.mjs'),
          'workspace/scripts/framework-runtime-child-control.mjs',
        ],
      ]) {
        const original = readFileSync(path);
        try {
          writeFileSync(path, Buffer.concat([original, Buffer.from('\n')]));
          onlyInputChanged(before, frameworkDevelopmentRestartGraph(controlIdentity), input);
          unlinkSync(path);
          onlyInputChanged(before, frameworkDevelopmentRestartGraph(controlIdentity), input);
        } finally {
          writeFileSync(path, original);
        }
      }
      const page = join(appRoot, 'src/app/page.tsx');
      altered(
        page,
        (bytes) => Buffer.concat([bytes, Buffer.from('\n')]),
        () => {
          assert.deepEqual(frameworkDevelopmentRestartGraph(controlIdentity), before);
        },
      );
      verifyFrameworkBuild({ appRoot, frontendRoot });
    } finally {
      removeRuntimeEntries();
      writeFileSync(join(appRoot, '.next/framework-build.json'), previousManifest);
    }
  });
  await test('an already-cancelled producer cannot claim ownership or mutate existing generation', async () => {
    const manifest = readFileSync(join(appRoot, '.next/framework-build.json'));
    const outputs = frameworkOutputGraph(controlIdentity);
    const metadata = metadataSnapshot();
    const cancellation = new AbortController();
    cancellation.abort('SIGTERM');
    await assert.rejects(
      produceFrameworkBuild({ appRoot, frontendRoot, signal: cancellation.signal }),
      (error) => error instanceof FrameworkAdmissionError && error.reason === 'producer_cancelled',
    );
    assert.deepEqual(readFileSync(join(appRoot, '.next/framework-build.json')), manifest);
    assert.deepEqual(frameworkOutputGraph(controlIdentity), outputs);
    assert.deepEqual(metadataSnapshot(), metadata);
  });
} finally {
  if (!retainRoot && activeRuntimeLaunches.size === 0)
    rmSync(root, { recursive: true, force: true });
  else process.stderr.write(`Framework provenance fixture retained: ${root}\n`);
}
