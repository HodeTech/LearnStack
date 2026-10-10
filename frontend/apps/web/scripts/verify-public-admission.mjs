// Test-only copied production app, driven by PublicAdmissionRenderingTests over private stdin.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { Agent, request as httpsRequest } from 'node:https';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

import { verifyBrowserFallback } from './admission-browser.mjs';
import { watchDevelopmentSource } from './admission-hmr-watch.mjs';
import { verifyHmrAdmission } from './admission-hmr.mjs';
import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';

const sourceApp = fileURLToPath(new URL('../', import.meta.url));
const owner = createFixtureOwner();
const scanners = [];
const pendingMessages = new Map();
let nextMessage = 0;
let root;
let app;
let configuration;
let certificate;
let input;
let lines;
let privateScanner;
let stage = 'configuration';

async function bounded(promise, milliseconds, label) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_resolve, reject) => {
        timer = setTimeout(() => reject(new Error(label)), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function checkpoint(name) {
  assert.match(name, /^[a-z0-9-]{1,63}$/);
  process.stdout.write(name + '\n');
  const answer = await bounded(lines.next(), 30_000, 'Parent control deadline');
  owner.assertActive();
  assert.equal(answer.done, false, 'Parent control closed');
  const value = JSON.parse(answer.value);
  assert.equal(value.kind, 'continue');
  if (value.apiOrigin !== null && value.apiOrigin !== undefined) {
    assert.match(value.apiOrigin, /^http:\/\/127\.0\.0\.1:\d+$/);
    configuration.apiOrigin = value.apiOrigin;
  }
  return value;
}

function environment(dev = false) {
  return fixtureEnvironment({
    root,
    nodeEnv: dev ? 'development' : 'production',
    nodePath: join(sourceApp, '../../node_modules/.pnpm/node_modules'),
    privateValues: {
      LEARNSTACK_PUBLIC_API_ORIGIN: configuration.apiOrigin,
      LEARNSTACK_PUBLIC_HOP_SECRET: configuration.secret,
      LEARNSTACK_PUBLIC_TLS_CERT: join(root, 'cert.pem'),
      LEARNSTACK_PUBLIC_TLS_KEY: join(root, 'key.pem'),
    },
  });
}

function start(command, args, dev = false, ipc = false) {
  owner.assertActive();
  const child = owner.ownChild(
    spawn(command, args, {
      cwd: app,
      env: environment(dev),
      detached: process.platform !== 'win32',
      stdio: ['ignore', 'pipe', 'pipe', ...(ipc ? ['ipc'] : [])],
    }),
  );
  child.failed = false;
  child.on('error', () => {
    child.failed = true;
  });
  const outputs = [];
  for (const stream of [child.stdout, child.stderr]) {
    const scanner = createPrivateScanner([configuration.secret, configuration.privateMarker]);
    outputs.push(scanner);
    scanners.push(scanner);
    stream.on('data', (chunk) => scanner.push(chunk));
    stream.once('end', () => scanner.finish());
    stream.once('close', () => scanner.finish());
  }
  Object.defineProperty(child, 'output', {
    get: () => outputs.map((scanner) => scanner.tail).join('\n'),
  });
  child.on('message', (message) => {
    if (message?.kind === 'shutdown') child.shutdownCounts = message.counts;
    else if (message?.id) pendingMessages.get(child)?.get(message.id)?.(message);
  });
  return child;
}

async function completion(child, milliseconds = 180_000) {
  if (child.exitCode !== null) return child.exitCode;
  return bounded(
    new Promise((resolve, reject) => {
      child.once('error', () => reject(new Error('Child startup')));
      child.once('close', (code) => resolve(code));
    }),
    milliseconds,
    'Child completion deadline',
  );
}

async function control(child, op, value) {
  assert.equal(child.connected, true, 'Owned native IPC remains connected');
  const id = ++nextMessage;
  let messages = pendingMessages.get(child);
  if (!messages) {
    messages = new Map();
    pendingMessages.set(child, messages);
  }
  try {
    const result = new Promise((resolve) => messages.set(id, resolve));
    child.send({ id, op, value });
    return await bounded(result, 10_000, 'Native observation deadline');
  } finally {
    messages.delete(id);
  }
}
const observe = (child) => control(child, 'observe');

function beginCall(
  path,
  { host = configuration.tenants[0].host, method = 'GET', headers = {}, agent } = {},
) {
  owner.assertActive();
  let request;
  let socket;
  const result = new Promise((resolve, reject) => {
    request = httpsRequest(
      {
        hostname: '127.0.0.1',
        port: 3000,
        path,
        method,
        agent,
        headers: { Host: host + ':3000', ...headers },
        ca: certificate,
        servername: host,
        rejectUnauthorized: true,
        timeout: 20_000,
      },
      (response) => {
        const chunks = [];
        let size = 0;
        response.on('data', (chunk) => {
          size += chunk.length;
          if (size > 4 * 1024 * 1024) request.destroy(new Error('Renderer response bound'));
          else chunks.push(chunk);
        });
        response.once('error', reject);
        response.once('end', () =>
          resolve({
            status: response.statusCode,
            headers: response.headers,
            body: Buffer.concat(chunks).toString('utf8'),
            socket,
          }),
        );
      },
    );
    request.on('socket', (value) => {
      socket = value;
    });
    request.once('timeout', () => request.destroy(new Error('Renderer request deadline')));
    request.once('error', reject);
    request.end();
  });
  // Attach immediately: deliberate aborts must never become unhandled rejections.
  const outcome = result.then(
    (response) => ({ response }),
    () => ({ aborted: true }),
  );
  return { result, outcome, abort: () => request.destroy(new Error('Owned client abort')) };
}
const call = (path, options) => beginCall(path, options).result;

async function vacant(port) {
  const probe = owner.ownServer(createServer());
  await new Promise((resolve, reject) => {
    probe.once('error', () => reject(new Error('Required fixture port occupied')));
    probe.listen(port, '127.0.0.1', resolve);
  });
  await new Promise((resolve, reject) =>
    probe.close((error) => (error ? reject(error) : resolve())),
  );
}

async function startNative(dev = false) {
  const child = start(
    process.execPath,
    [join(app, 'scripts/public-server.mjs'), ...(dev ? ['--dev'] : [])],
    dev,
    true,
  );
  for (let attempt = 0; attempt < 250; attempt++) {
    assert.equal(
      child.failed || child.exitCode !== null || child.signalCode !== null,
      false,
      'Native startup',
    );
    if (/Public HTTPS listener ready on 127\.0\.0\.1:3000/.test(child.output)) {
      try {
        const health = await call('/api/healthz');
        if (health.status === 200 && JSON.parse(health.body).status === 'healthy') return child;
      } catch {
        /* Only the exempt readiness request is retried. */
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Native readiness deadline');
}

function empty(counts) {
  assert.equal(counts.active, 0, 'No native request retained');
  assert.equal(counts.snapshots, 0, 'No admitted snapshot retained');
}

async function stopNative(child, signal = 'SIGTERM') {
  assert.equal(child.exitCode, null, 'Native server remains live before shutdown');
  child.kill(signal);
  assert.equal(await completion(child, 10_000), 0, 'Native shutdown without escalation');
  empty(child.shutdownCounts);
  pendingMessages.delete(child);
}

function safe(response) {
  assert.match(response.headers['cache-control'], /(?:^|,\s*)no-store(?:,|$)/);
  for (const header of ['set-cookie', 'x-powered-by', 'x-learnstack-ingress-provenance'])
    assert.equal(response.headers[header], undefined);
  assert.equal(
    privateScanner.contains(JSON.stringify({ headers: response.headers, body: response.body })),
    false,
  );
}

function refused(response, status, method = 'GET', retryAfter) {
  safe(response);
  assert.equal(response.status, status, 'Original wire refusal status');
  assert.match(response.headers['content-type'], /^text\/plain;\s*charset=utf-8$/i);
  assert.equal(response.headers['retry-after'], retryAfter);
  assert.equal(
    response.body,
    method === 'HEAD'
      ? ''
      : status === 404
        ? 'Not found'
        : status === 429
          ? 'Too many requests'
          : 'Service unavailable',
  );
  for (const tenant of configuration.tenants)
    assert.equal(response.body.includes(tenant.name), false);
  assert.equal(response.body.includes('data-public-theme'), false);
}

function scanAssets(directory) {
  let scripts = 0;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) scripts += scanAssets(path);
    else {
      if (entry.name.endsWith('.js')) scripts++;
      const asset = readFileSync(path, 'utf8');
      assert.equal(privateScanner.contains(asset), false, 'Client private containment');
      assert.equal(
        asset.includes('learnstack.public-admission.v1'),
        false,
        'Native holder stays server-only',
      );
      assert.equal(
        asset.includes('learnstack.public-ingress.v1'),
        false,
        'Provenance HMAC stays server-only',
      );
    }
  }
  return scripts;
}

function replaceOnce(path, needle, replacement) {
  const source = readFileSync(path, 'utf8');
  assert.equal(source.split(needle).length, 2, 'Disposable instrumentation anchor is unique');
  writeFileSync(path, source.replace(needle, replacement));
}

function instrumentCopy() {
  // IPC-only observations in this disposable app. No diagnostic route, DTO,
  // binding, envelope or secret is sent to the parent or added to production.
  const launcher = join(app, 'scripts/public-server.mjs');
  replaceOnce(
    launcher,
    'import { installPublicAdmissionRuntime }',
    'import { createPublicAdmissionRuntime, installPublicAdmissionRuntime }',
  );
  replaceOnce(
    launcher,
    '  const admission = installPublicAdmissionRuntime();',
    `  const admission = installPublicAdmissionRuntime();
  const snapshotIds = new WeakMap();
  let snapshotsSeen = 0;
  let reads = 0;
  let immutable = true;
  let lateRefused = 0;
  let lateAccepted = 0;
  let finishLate;
  const originalOrigin = process.env.LEARNSTACK_PUBLIC_API_ORIGIN;
  const fixture = {
    mode: 'normal', finished: Promise.resolve(),
    record(site) {
      reads++;
      if (!snapshotIds.has(site)) snapshotIds.set(site, ++snapshotsSeen);
      immutable &&= Object.isFrozen(site) && Object.isFrozen(site.enabledLocales)
        && (site.theme === null || Object.isFrozen(site.theme));
      const name = site.displayName;
      const locale = site.enabledLocales[0];
      try { site.displayName = 'fixture mutation'; } catch {}
      try { site.enabledLocales[0] = 'fixture mutation'; } catch {}
      immutable &&= site.displayName === name && site.enabledLocales[0] === locale;
      if (site.theme !== null) {
        const primary = site.theme.primary;
        try { site.theme.primary = 'fixture mutation'; } catch {}
        immutable &&= site.theme.primary === primary;
      }
    },
    late(refused) { if (refused) lateRefused++; else lateAccepted++; }
  };
  Object.defineProperty(globalThis, Symbol.for('learnstack.fixture.admission'), { value: fixture });
  Object.defineProperty(globalThis, Symbol.for('learnstack.fixture.duplicate'), {
    value: createPublicAdmissionRuntime().holder, writable: false, enumerable: false, configurable: false
  });
  process.on('message', (message) => {
    if (!message || !Number.isInteger(message.id)) return;
    if (message.op === 'mode') {
      if (!['normal','missing','mismatch','duplicate','late','configuration','wrong-status'].includes(message.value)) return;
      fixture.mode = message.value;
      process.env.LEARNSTACK_PUBLIC_API_ORIGIN = message.value === 'configuration' ? 'invalid' : originalOrigin;
      if (message.value === 'late') fixture.finished = new Promise((resolve) => { finishLate = resolve; });
    } else if (message.op !== 'observe') return;
    process.send?.({ id: message.id, ...admission.counts(), snapshotsSeen, reads, immutable, lateRefused, lateAccepted });
  });`,
  );
  replaceOnce(
    launcher,
    '      if (!binding) return refuseIngress(response);',
    `      if (!binding) return refuseIngress(response);
      if (fixture.mode === 'late') response.once('finish', () => finishLate?.());`,
  );
  replaceOnce(
    launcher,
    '.then(() => admission.run(binding, request, response, () => handle(request, response)))',
    `.then(() => fixture.mode === 'missing'
          ? handle(request, response)
          : admission.run(fixture.mode === 'mismatch' ? { ...binding, target: '/mismatch' } : binding,
              request, response, () => handle(request, response)))`,
  );
  replaceOnce(
    launcher,
    '    stopping = true;\n    admission.shutdown();',
    `    stopping = true;
    admission.shutdown();
    await new Promise((resolve) => process.send?.({ kind: 'shutdown', counts: admission.counts() }, resolve));`,
  );

  writeFileSync(
    join(app, 'src/server/admission-fixture.ts'),
    `import 'server-only';
type Fixture = { mode: string; finished: Promise<void>; record(site: unknown): void; late(refused: boolean): void };
export function admissionFixture() {
  return (globalThis as unknown as Record<symbol, Fixture | undefined>)[Symbol.for('learnstack.fixture.admission')];
}
`,
  );
  const facade = join(app, 'src/server/public-admission.ts');
  replaceOnce(
    facade,
    "import 'server-only';",
    "import 'server-only';\nimport { admissionFixture } from './admission-fixture';",
  );
  replaceOnce(
    facade,
    "Symbol.for('learnstack.public-admission.v1')",
    "Symbol.for(admissionFixture()?.mode === 'duplicate' ? 'learnstack.fixture.duplicate' : 'learnstack.public-admission.v1')",
  );
  replaceOnce(
    facade,
    "return holder().read(binding) as Omit<PublicAdmissionRead, 'site'> & {",
    "const admitted = holder().read(binding);\n  admissionFixture()?.record(admitted.site);\n  return admitted as Omit<PublicAdmissionRead, 'site'> & {",
  );
  const middleware = join(app, 'src/middleware.ts');
  replaceOnce(
    middleware,
    "import { NextResponse, type NextRequest } from 'next/server';",
    "import { NextResponse, type NextRequest } from 'next/server';\nimport { admissionFixture } from '@/server/admission-fixture';",
  );
  replaceOnce(
    middleware,
    '      status,\n      headers:',
    "      status: admissionFixture()?.mode === 'wrong-status' ? 200 : status,\n      headers:",
  );

  writeFileSync(
    join(app, 'src/server/late-admission-probe.tsx'),
    `import 'server-only';
import { admissionFixture } from './admission-fixture';
import { getPublicResource } from './public-resource';
export async function LateAdmissionProbe() {
  const fixture = admissionFixture();
  if (!fixture || fixture.mode !== 'late') return null;
  await fixture.finished;
  try { await getPublicResource(); fixture.late(false); }
  catch (error) { fixture.late(error instanceof Error && error.message === 'Public admission request completed'); }
  return null;
}
`,
  );
  const layout = join(app, 'src/app/layout.tsx');
  replaceOnce(
    layout,
    "import type { ReactNode } from 'react';",
    "import { Suspense, type ReactNode } from 'react';\nimport { LateAdmissionProbe } from '@/server/late-admission-probe';",
  );
  replaceOnce(
    layout,
    '{children}</body>',
    '{children}<Suspense fallback={null}><LateAdmissionProbe /></Suspense></body>',
  );
  const studio = join(app, 'src/app/(studio)/studio');
  writeFileSync(
    join(studio, 'admission-navigation.tsx'),
    `'use client';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
export default function AdmissionNavigation({ target }: { target: string }) {
  const [ready, setReady] = useState(false);
  const router = useRouter();
  useEffect(() => setReady(true), []);
  return <button data-admission-ready={String(ready)} data-admission-navigation onClick={() => router.push(target)}>Navigate</button>;
}
`,
  );
  writeFileSync(
    join(studio, 'page.tsx'),
    `import AdmissionNavigation from './admission-navigation';
export default function AdmissionPage() { return <main><h1>Admission fixture</h1><AdmissionNavigation target=${JSON.stringify('/' + configuration.locale + '/courses/' + configuration.courseSlug)} /></main>; }
`,
  );
}

async function wireProof(native) {
  const catalog = '/' + configuration.locale + '/courses';
  for (const fault of configuration.wireCases) {
    stage = 'wire ' + fault.name;
    await checkpoint('arm-' + fault.name);
    const response = await call(catalog, { method: fault.method });
    refused(response, fault.status, fault.method, fault.retryAfter ?? undefined);
    empty(await observe(native));
    await checkpoint('done-' + fault.name);
  }
  stage = 'wire mutant';
  await control(native, 'mode', 'wrong-status');
  await checkpoint('arm-rate-zero');
  const mutant = await call(catalog);
  assert.throws(() => refused(mutant, 429, 'GET', '0'), /Original wire refusal status/);
  await checkpoint('done-rate-zero');
  await control(native, 'mode', 'normal');
  await checkpoint('arm-rate-zero');
  refused(await call(catalog), 429, 'GET', '0');
  await checkpoint('done-rate-zero');
  stage = 'wire positive';
  const healthy = await call(catalog);
  safe(healthy);
  assert.equal(healthy.status, 200);
  assert.ok(healthy.body.includes(configuration.tenants[0].name));
  empty(await observe(native));
  await checkpoint('done-healthy-wire');
  refused(await call('/unadmitted-fixture-path'), 404);
  await checkpoint('done-entry-refusal');
}

async function lifetimes(native) {
  const catalog = '/' + configuration.locale + '/courses';
  for (const mode of ['missing', 'mismatch', 'duplicate', 'configuration']) {
    stage = 'context ' + mode;
    await control(native, 'mode', mode);
    refused(await call(catalog), 503);
    empty(await observe(native));
    await checkpoint(mode === 'configuration' ? 'done-configuration' : 'done-context-' + mode);
  }
  await control(native, 'mode', 'normal');
  const healthy = await call(catalog);
  assert.equal(healthy.status, 200);
  await checkpoint('done-healthy-lifetime');
  for (const protocol of ['html', 'rsc', 'prefetch']) {
    stage = protocol + ' overlap';
    const before = await observe(native);
    const headers =
      protocol === 'html'
        ? {}
        : { RSC: '1', ...(protocol === 'prefetch' ? { 'Next-Router-Prefetch': '1' } : {}) };
    await checkpoint('arm-overlap');
    const pending = Array.from({ length: 4 }, () => beginCall(catalog, { headers }));
    await checkpoint('held-overlap');
    const held = await observe(native);
    assert.equal(held.active, 4);
    assert.equal(held.snapshots, 0);
    await checkpoint('release-overlap');
    const responses = await Promise.all(pending.map((request) => request.result));
    for (const response of responses) {
      safe(response);
      assert.equal(response.status, 200);
    }
    const after = await observe(native);
    empty(after);
    assert.equal(after.immutable, true);
    assert.equal(
      after.snapshotsSeen - before.snapshotsSeen,
      4,
      'Identical incoming requests have separate snapshots',
    );
    assert.ok(after.reads - before.reads > 4, 'Multiple consumers share the four snapshots');
    await checkpoint('done-' + protocol + '-overlap');
  }
  stage = 'mixed overlap';
  const lanes = configuration.tenants.flatMap((tenant) =>
    [false, true].map((rsc) => ({ tenant, rsc })),
  );
  await checkpoint('arm-overlap');
  const pendingMixed = lanes.map(({ tenant, rsc }) =>
    beginCall('/' + tenant.locale + '/courses', {
      host: tenant.host,
      headers: rsc ? { RSC: '1' } : {},
    }),
  );
  await checkpoint('held-overlap');
  const mixedHeld = await observe(native);
  assert.equal(mixedHeld.active, 4);
  assert.equal(mixedHeld.snapshots, 0);
  await checkpoint('release-overlap');
  const mixed = await Promise.all(pendingMixed.map((request) => request.result));
  mixed.forEach((response, index) => {
    safe(response);
    assert.equal(response.status, 200);
    assert.ok(response.body.includes(lanes[index].tenant.name));
    const other = configuration.tenants.find((tenant) => tenant.host !== lanes[index].tenant.host);
    assert.equal(response.body.includes(other.name), false);
  });
  empty(await observe(native));
  await checkpoint('done-mixed-overlap');
  stage = 'keep-alive';
  const agent = new Agent({ keepAlive: true, maxSockets: 1 });
  try {
    const first = await call(catalog, { agent });
    const second = await call(catalog, { agent });
    assert.equal(first.status, 200);
    assert.equal(second.status, 200);
    assert.equal(
      first.socket,
      second.socket,
      'One physical keep-alive connection serves fresh native requests',
    );
    empty(await observe(native));
  } finally {
    agent.destroy();
  }
  await checkpoint('done-keep-alive');
  stage = 'valid HEAD';
  const head = await call(catalog, { method: 'HEAD' });
  safe(head);
  assert.equal(head.status, 200);
  assert.equal(head.body, '');
  empty(await observe(native));
  await checkpoint('done-head');
  stage = 'late HEAD';
  await control(native, 'mode', 'late');
  const late = await call('/studio', { method: 'HEAD' });
  assert.equal(late.status, 200);
  assert.equal(late.body, '');
  for (let attempt = 0; attempt < 100; attempt++) {
    const state = await observe(native);
    if (state.lateRefused === 1) break;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  const lateState = await observe(native);
  empty(lateState);
  assert.equal(lateState.lateRefused, 1);
  assert.equal(lateState.lateAccepted, 0);
  await checkpoint('done-late-head');
  await control(native, 'mode', 'normal');
  for (const kind of ['bootstrap', 'content']) {
    stage = kind + ' client abort';
    await checkpoint('arm-' + kind);
    const pending = beginCall(catalog);
    await checkpoint('held-' + kind);
    const active = await observe(native);
    assert.equal(active.active, 1);
    assert.equal(active.snapshots, kind === 'bootstrap' ? 0 : 1);
    pending.abort();
    assert.equal((await pending.outcome).aborted, true);
    await checkpoint('aborted-' + kind);
    empty(await observe(native));
  }
  stage = 'active shutdown';
  await checkpoint('arm-shutdown');
  const pending = Array.from({ length: 2 }, () => beginCall(catalog));
  await checkpoint('held-shutdown');
  const active = await observe(native);
  assert.equal(active.active, 2);
  assert.equal(active.snapshots, 2);
  await stopNative(native);
  await Promise.all(pending.map((request) => request.outcome));
  await checkpoint('aborted-shutdown');
}

try {
  input = createInterface({ input: process.stdin, terminal: false });
  owner.install({ control: input, event: 'close' });
  lines = input[Symbol.asyncIterator]();
  const first = await bounded(lines.next(), 30_000, 'Configuration deadline');
  assert.equal(first.done, false);
  configuration = JSON.parse(first.value);
  assert.match(configuration.apiOrigin, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.match(configuration.secret, /^[a-f0-9]{48}$/);
  assert.equal(configuration.tenants.length, 2);
  privateScanner = createPrivateScanner([configuration.secret, configuration.privateMarker]);
  root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-public-admission-')));
  app = join(root, 'frontend/apps/web');
  await vacant(3000);
  await vacant(3011);
  mkdirSync(app, { recursive: true });
  for (const name of [
    'src',
    'scripts',
    'package.json',
    'next.config.ts',
    'next-env.d.ts',
    'tsconfig.json',
    'tsconfig.server.json',
    'postcss.config.cjs',
    'tailwind.config.ts',
    '.eslintrc.cjs',
  ]) {
    cpSync(join(sourceApp, name), join(app, name), {
      recursive: true,
      filter: (path) =>
        !/\.test\.[jt]sx?$/.test(path) &&
        path !== join(sourceApp, 'src/test') &&
        !path.startsWith(join(sourceApp, 'src/test/')),
    });
  }
  symlinkSync(join(sourceApp, 'node_modules'), join(app, 'node_modules'), 'dir');
  instrumentCopy();
  stage = 'production build';
  const compiler = start(process.execPath, [
    join(sourceApp, 'node_modules/typescript/bin/tsc'),
    '--project',
    'tsconfig.server.json',
  ]);
  assert.equal(await completion(compiler), 0, 'Ingress build');
  const build = start(process.execPath, [
    join(sourceApp, 'node_modules/next/dist/bin/next'),
    'build',
  ]);
  assert.equal(await completion(build), 0, 'Copied product build');
  assert.ok(scanAssets(join(app, '.next/static')) > 0);
  await checkpoint('build-complete');
  stage = 'isolated TLS';
  writeFileSync(
    join(root, 'tls.cnf'),
    '[req]\ndistinguished_name=dn\nx509_extensions=ext\nprompt=no\n[dn]\nCN=localhost\n[ext]\nsubjectAltName=DNS:localhost,' +
      configuration.tenants.map((tenant) => 'DNS:' + tenant.host).join(',') +
      '\nbasicConstraints=critical,CA:TRUE\n',
  );
  const openssl = start('openssl', [
    'req',
    '-x509',
    '-newkey',
    'rsa:2048',
    '-nodes',
    '-days',
    '1',
    '-config',
    join(root, 'tls.cnf'),
    '-keyout',
    join(root, 'key.pem'),
    '-out',
    join(root, 'cert.pem'),
  ]);
  assert.equal(await completion(openssl, 20_000), 0);
  certificate = readFileSync(join(root, 'cert.pem'));
  await checkpoint('phase-wire');
  let native = await startNative();
  await wireProof(native);
  await stopNative(native);
  await checkpoint('phase-lifetime');
  native = await startNative();
  await lifetimes(native);
  await checkpoint('phase-browser');
  native = await startNative();
  for (const status of [429, 503]) {
    stage = 'browser ' + status;
    scanners.push(
      await verifyBrowserFallback({
        owner,
        app,
        root,
        certificate,
        origin: 'https://' + configuration.tenants[0].host + ':3000',
        initialPath: '/studio',
        target: '/' + configuration.locale + '/courses/' + configuration.courseSlug,
        status,
        secrets: [configuration.secret, configuration.privateMarker],
        beforeNavigation: async () => {
          await checkpoint('done-browser-initial');
          await checkpoint('arm-browser-' + status);
        },
        afterNavigation: async () => {
          empty(await observe(native));
          await checkpoint('done-browser-' + status);
        },
      }),
    );
  }
  await stopNative(native);
  stage = 'development HMR';
  await verifyHmrAdmission({
    app,
    configuration,
    checkpoint,
    startNative,
    stopNative,
    call,
    observe,
    watchSource: () => watchDevelopmentSource({ app, certificate }),
  });
  stage = 'private containment';
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
  );
  await checkpoint('verified');
} catch (error) {
  const hmrStages = new Set([
    'hmr-setup',
    'hmr-initial-request',
    'hmr-initial-status',
    'hmr-initial-source',
    'hmr-initial-site',
    'hmr-initial-release',
    'hmr-watch-connect',
    'hmr-change',
    'hmr-source-barrier',
    'hmr-refresh-request',
    'hmr-refresh-status',
    'hmr-refresh-type',
    'hmr-refresh-source',
    'hmr-refresh-site',
    'hmr-refresh-release',
    'hmr-after',
  ]);
  const detail = hmrStages.has(error?.admissionStage) ? ' ' + error.admissionStage : '';
  process.stderr.write('Public admission fixture failed during ' + stage + detail + '.\n');
  process.exitCode = 1;
} finally {
  try {
    await owner.dispose();
    assert.equal(
      scanners.some((scanner) => scanner.leaked),
      false,
    );
  } catch {
    process.stderr.write('Public admission fixture cleanup failed.\n');
    process.exitCode = 1;
  }
  input?.close();
}
