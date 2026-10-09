// Test-only production build, driven by PublicServerRenderingTests over private stdin.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

import { stopTestChild } from './stop-test-child.mjs';

const sourceApp = fileURLToPath(new URL('../', import.meta.url));
const fixtureRoot = mkdtempSync(join(tmpdir(), 'learnstack-public-rendering-'));
const app = join(fixtureRoot, 'frontend/apps/web');
const input = createInterface({ input: process.stdin, terminal: false });
const lines = input[Symbol.asyncIterator]();
const children = [];
let stage = 'configuration';
let configuration;
let certificate;
let privateOutput = false;
let cleanup;
let finished = false;

function checkPrivate(value) {
  return (
    /x-learnstack-|\bv1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}/i.test(value) ||
    (configuration !== undefined && value.includes(configuration.secret))
  );
}

async function line() {
  let timer;
  try {
    const result = await Promise.race([
      lines.next(),
      new Promise((_resolve, reject) => {
        timer = setTimeout(() => reject(new Error('Control deadline')), 30_000);
      }),
    ]);
    assert.equal(result.done, false, 'Parent control closed');
    return result.value;
  } finally {
    clearTimeout(timer);
  }
}

async function checkpoint(name) {
  process.stdout.write(name + '\n');
  assert.equal(await line(), 'continue', 'Invalid parent acknowledgement');
}

// Do not inherit private/public Next environment, loader hooks, trust overrides,
// proxy settings or a developer's certificate configuration into the fixture.
function environment(runtime = false) {
  const env = {
    PATH: process.env.PATH,
    TMPDIR: fixtureRoot,
    NODE_ENV: 'production',
    NODE_PATH: join(sourceApp, '../../node_modules/.pnpm/node_modules'),
    NEXT_TELEMETRY_DISABLED: '1',
    CI: 'true',
  };
  if (runtime)
    Object.assign(env, {
      LEARNSTACK_PUBLIC_API_ORIGIN: configuration.apiOrigin,
      LEARNSTACK_PUBLIC_HOP_SECRET: configuration.secret,
      LEARNSTACK_PUBLIC_TLS_CERT: join(fixtureRoot, 'cert.pem'),
      LEARNSTACK_PUBLIC_TLS_KEY: join(fixtureRoot, 'key.pem'),
    });
  return env;
}

function start(command, args, runtime = false) {
  const child = spawn(command, args, {
    cwd: app,
    env: environment(runtime),
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: process.platform !== 'win32',
  });
  child.output = '';
  child.failed = false;
  child.on('error', () => {
    child.failed = true;
  });
  for (const stream of [child.stdout, child.stderr])
    stream.on('data', (chunk) => {
      // Scan before truncation, including a split secret/header across chunks.
      const combined = child.output.slice(-16384) + chunk.toString();
      privateOutput ||= checkPrivate(combined);
      child.output = (child.output + chunk.toString()).slice(-65536);
    });
  children.push(child);
  return child;
}

async function completion(child, milliseconds = 180_000) {
  if (child.exitCode !== null) return child.exitCode;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Child deadline')), milliseconds);
    child.once('error', () => {
      clearTimeout(timer);
      reject(new Error('Child startup'));
    });
    child.once('exit', (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

function dispose() {
  cleanup ??= (async () => {
    const results = await Promise.allSettled(children.map(stopTestChild));
    rmSync(fixtureRoot, { recursive: true, force: true });
    assert.equal(
      results.some((result) => result.status === 'rejected'),
      false,
      'Child cleanup',
    );
    assert.equal(privateOutput, false, 'Private data entered shutdown logs');
  })();
  return cleanup;
}

// Parent failure/termination must not leave the listener or compiler workers alive.
function cancelled() {
  if (!finished) void dispose().finally(() => process.exit(1));
}
input.once('close', cancelled);
process.once('SIGTERM', cancelled);
process.once('SIGINT', cancelled);

async function vacant(port) {
  const probe = createServer();
  await new Promise((resolve, reject) => {
    probe.once('error', () => reject(new Error('Required test port is occupied')));
    probe.listen(port, '127.0.0.1', resolve);
  });
  await new Promise((resolve, reject) =>
    probe.close((error) => (error ? reject(error) : resolve())),
  );
}

function call(tls, port, path, headers = {}, method = 'GET') {
  return new Promise((resolve, reject) => {
    const send = tls ? httpsRequest : httpRequest;
    const request = send(
      {
        hostname: '127.0.0.1',
        port,
        path,
        headers,
        method,
        ...(tls ? { ca: certificate, servername: headers.Host?.split(':')[0] ?? 'localhost' } : {}),
        timeout: 15_000,
      },
      (response) => {
        let body = '';
        response.on('data', (chunk) => {
          body += chunk.toString();
          if (body.length > 2 * 1024 * 1024) request.destroy(new Error('Response bound'));
        });
        response.on('error', reject);
        response.on('end', () =>
          resolve({ status: response.statusCode, headers: response.headers, body }),
        );
      },
    );
    request.on('timeout', () => request.destroy(new Error('Request deadline')));
    request.on('error', reject);
    request.end();
  });
}

async function ready(child, tls, port) {
  for (let attempt = 0; attempt < 200; attempt++) {
    assert.equal(
      child.failed || child.exitCode !== null || child.signalCode !== null,
      false,
      'Listener startup',
    );
    try {
      const response = await call(tls, port, '/api/healthz');
      if (response.status === 200 && JSON.parse(response.body).status === 'healthy') return;
    } catch {
      /* Readiness only; public API calls are never retried. */
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Readiness deadline');
}

function safeResponse(
  response,
  status,
  rsc = false,
  contentType = rsc ? /^text\/x-component/ : /^text\/html/,
) {
  if (response.status !== status) stage += ` (status ${response.status}, expected ${status})`;
  assert.equal(response.status, status, 'Public response status');
  assert.match(response.headers['cache-control'], /(?:^|,\s*)no-store(?:,|$)/);
  assert.equal(response.headers.etag, undefined);
  assert.equal(response.headers['set-cookie'], undefined);
  assert.equal(response.headers['x-powered-by'], undefined);
  assert.equal(checkPrivate(JSON.stringify(response)), false, 'Response private-data containment');
  if (status === 200) assert.match(response.headers['content-type'], contentType);
}

async function representation(tenant, path, rsc = false, extra = {}, status = 200) {
  const response = await call(true, 3000, path, {
    Host: tenant.host + ':3000',
    ...(rsc ? { RSC: '1' } : {}),
    ...extra,
  });
  safeResponse(response, status, rsc);
  const other = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  assert.equal(response.body.includes(other.name), false, 'Opposite tenant name');
  assert.equal(response.body.includes(other.courseTitle), false, 'Opposite tenant course');
  if (status === 200)
    assert.equal(response.body.includes(tenant.name), true, 'Live bootstrap name');
  return response;
}

function scanClientAssets(directory) {
  let files = 0;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) files += scanClientAssets(path);
    else {
      files++;
      assert.equal(
        checkPrivate(readFileSync(path, 'utf8')),
        false,
        'Static client private-data containment',
      );
    }
  }
  return files;
}

// This source exists only inside the disposable app. Every public value comes
// from the real configured SDK; headers, envelopes and errors are never serialized.
const page = `import { headers } from 'next/headers';
import { notFound } from 'next/navigation';
import { createConfiguredPublicClient } from '@/server/configured-public-client';
import { INGRESS_HEADER } from '@/server/ingress';

export default async function FixturePage({ params }: {
  params: Promise<{ locale: string; segments?: string[] }>;
}) {
  const incoming = await headers();
  const client = createConfiguredPublicClient(incoming.get(INGRESS_HEADER), {
    traceparent: incoming.get('traceparent'),
  });
  if (!client) notFound();
  const site = await client.getSite();
  if (site.kind !== 'success') throw new Error('Fixture bootstrap failed');
  const { segments = [] } = await params;
  const result = segments.length === 0 ? await client.getCourses() :
    segments.length === 1 ? await client.getCourse({ slug: segments[0]! }) :
    await client.getLesson({ slug: segments[0]!, lessonSlug: segments[2]! });
  if (result.kind === 'api-error' && result.status === 404) notFound();
  if (result.kind !== 'success') throw new Error('Fixture public read failed');
  return <section><h1>{site.data.displayName}</h1><pre>{JSON.stringify(result.data)}</pre></section>;
}
`;

// The real Next adapter removes Flight inputs before user middleware, then
// restores them after its request-header override. Observe that final request
// through a disposable admitted route, without exposing any header values.
const protocolProbe = `export const dynamic = 'force-dynamic';

export function GET(request: Request) {
  return Response.json({
    rsc: request.headers.get('rsc') === '1',
    stateTree: request.headers.get('next-router-state-tree') === '%5B%22%22%2C%7B%7D%5D',
    routerPrefetch: request.headers.get('next-router-prefetch') === '1',
    segmentPrefetch: request.headers.get('next-router-segment-prefetch') === '/_tree',
    hmrRefresh: request.headers.get('next-hmr-refresh') === '1',
  }, { headers: { 'cache-control': 'no-store' } });
}
`;

try {
  configuration = JSON.parse(await line());
  assert.match(configuration.apiOrigin, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.match(configuration.secret, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(configuration.tenants.length, 2);
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
  const fixturePage = join(app, 'src/app/(public)/[locale]/courses/[[...segments]]/page.tsx');
  mkdirSync(dirname(fixturePage), { recursive: true });
  writeFileSync(fixturePage, page);
  const fixtureProbe = join(app, 'src/app/(public)/[locale]/courses/protocol-probe/route.ts');
  mkdirSync(dirname(fixtureProbe), { recursive: true });
  writeFileSync(fixtureProbe, protocolProbe);

  stage = 'client import rejection';
  const mutant = join(app, 'src/app/client-import-probe/page.tsx');
  mkdirSync(dirname(mutant), { recursive: true });
  writeFileSync(
    mutant,
    "'use client';\n" +
      "import { createConfiguredPublicClient } from '@/server/configured-public-client';\n" +
      'export default function Probe() { return <p>{String(createConfiguredPublicClient(null))}</p>; }\n',
  );
  const nextBin = join(sourceApp, 'node_modules/next/dist/bin/next');
  const rejected = start(process.execPath, [nextBin, 'build']);
  assert.notEqual(
    await completion(rejected),
    0,
    'Client import mutant must fail a real production build',
  );
  assert.match(rejected.output, /server-only/);
  assert.match(rejected.output, /client-import-probe\/page\.tsx/);
  rmSync(dirname(mutant), { recursive: true });
  rmSync(join(app, '.next'), { recursive: true, force: true });

  stage = 'healthy production build';
  const ingress = start(process.execPath, [
    join(sourceApp, 'node_modules/typescript/bin/tsc'),
    '--project',
    'tsconfig.server.json',
  ]);
  assert.equal(await completion(ingress), 0, 'Production ingress compilation');
  const build = start(process.execPath, [nextBin, 'build']);
  assert.equal(await completion(build), 0, 'Healthy production build');
  assert.ok(scanClientAssets(join(app, '.next/static')) > 0, 'Nonempty production client assets');
  await checkpoint('build-complete');

  stage = 'isolated TLS';
  writeFileSync(
    join(fixtureRoot, 'tls.cnf'),
    '[req]\ndistinguished_name=dn\nx509_extensions=ext\nprompt=no\n' +
      '[dn]\nCN=localhost\n[ext]\nsubjectAltName=DNS:localhost,' +
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
    join(fixtureRoot, 'tls.cnf'),
    '-keyout',
    join(fixtureRoot, 'key.pem'),
    '-out',
    join(fixtureRoot, 'cert.pem'),
  ]);
  assert.equal(await completion(openssl, 20_000), 0, 'Isolated TLS generation');
  certificate = readFileSync(join(fixtureRoot, 'cert.pem'));
  const native = start(process.execPath, [join(app, 'scripts/public-server.mjs')], true);
  await ready(native, true, 3000);
  const [first, second] = configuration.tenants;
  const catalog = '/' + configuration.locale + '/courses';
  const course = catalog + '/' + configuration.courseSlug;

  stage = 'cold and interleaved host representations';
  const firstCatalog = await representation(first, catalog, false, {
    traceparent: configuration.traceparent,
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-LearnStack-Host': second.host,
    'X-Forwarded-For': '203.0.113.99',
    Cookie: 'locale=invalid',
  });
  assert.ok(firstCatalog.body.includes(first.courseTitle));
  await checkpoint('trace-supplied');
  assert.ok((await representation(second, catalog)).body.includes(second.courseTitle));
  await checkpoint('trace-missing');
  assert.ok(
    (await representation(first, course, false, { traceparent: 'malformed' })).body.includes(
      first.courseTitle,
    ),
  );
  await checkpoint('trace-malformed');
  assert.ok((await representation(second, course)).body.includes(second.courseTitle));
  for (const path of [catalog, course])
    for (const tenant of configuration.tenants) {
      assert.ok((await representation(tenant, path, true)).body.includes(tenant.courseTitle));
    }
  for (const tenant of configuration.tenants) {
    const lesson = await representation(tenant, course + '/lessons/' + tenant.lessonSlug);
    assert.ok(lesson.body.includes(tenant.lessonTitle), 'Real SDK lesson body');
    assert.ok(lesson.body.includes(tenant.lessonText), 'Public projected field');
  }
  const redirect = await call(true, 3000, '/', { Host: second.host + ':3000' });
  safeResponse(redirect, 307);
  assert.equal(
    redirect.headers.location,
    'https://' + second.host + ':3000/' + second.defaultLocale + '/courses',
  );

  stage = 'real adapter Flight header preservation';
  await checkpoint('protocol-before');
  const absentProtocol = {
    rsc: false,
    stateTree: false,
    routerPrefetch: false,
    segmentPrefetch: false,
    hmrRefresh: false,
  };
  const navigationHeaders = {
    RSC: '1',
    'Next-Router-State-Tree': '%5B%22%22%2C%7B%7D%5D',
  };
  for (const [headers, expected] of [
    [{}, absentProtocol],
    [navigationHeaders, { ...absentProtocol, rsc: true, stateTree: true }],
    [
      { ...navigationHeaders, 'Next-Router-Prefetch': '1' },
      { ...absentProtocol, rsc: true, stateTree: true, routerPrefetch: true },
    ],
    [
      { RSC: '1', 'Next-Router-Segment-Prefetch': '/_tree' },
      { ...absentProtocol, rsc: true, segmentPrefetch: true },
    ],
    [
      { RSC: '1', 'Next-Hmr-Refresh': '1' },
      { ...absentProtocol, rsc: true, hmrRefresh: true },
    ],
  ]) {
    const response = await call(true, 3000, catalog + '/protocol-probe', {
      Host: first.host + ':3000',
      ...headers,
    });
    safeResponse(response, 200, false, /^application\/json/);
    assert.deepEqual(JSON.parse(response.body), expected, 'Final route protocol inputs');
  }
  stage = 'real adapter bodyless HEAD';
  const head = await call(
    true,
    3000,
    catalog + '/protocol-probe',
    { Host: first.host + ':3000', ...navigationHeaders },
    'HEAD',
  );
  safeResponse(head, 200, false, /^application\/json/);
  assert.equal(head.body, '', 'Supported HEAD has no response body');
  await checkpoint('protocol-after');

  stage = 'stock launcher forgery before bootstrap';
  const stock = start(
    process.execPath,
    [nextBin, 'start', '--hostname', '127.0.0.1', '--port', '3011'],
    true,
  );
  await ready(stock, false, 3011);
  await checkpoint('stock-before');
  for (const rsc of [false, true]) {
    const bypass = await call(false, 3011, course, {
      Host: first.host + ':3000',
      ...(rsc ? { RSC: '1' } : {}),
      'X-LearnStack-Ingress-Provenance': 'forged',
      'X-LearnStack-Host': first.host,
      'X-LearnStack-Visitor-Address': '203.0.113.99',
      'X-Middleware-Subrequest': 'middleware:middleware:middleware:middleware:middleware',
      'X-Middleware-Subrequest-Id': 'attacker',
    });
    safeResponse(bypass, 404);
    assert.equal(bypass.body.includes(first.name), false);
  }
  await checkpoint('stock-after');

  stage = 'same process publication freshness';
  const nativePid = native.pid;
  await checkpoint('make-draft');
  for (const rsc of [false, true]) {
    stage = rsc ? 'draft RSC catalog' : 'draft HTML catalog';
    const fresh = await representation(first, catalog, rsc);
    assert.equal(fresh.body.includes(first.courseTitle), false, 'Draft vanished from catalog');
    stage = rsc ? 'draft RSC detail refusal' : 'draft HTML detail refusal';
    // Next's streamed RSC refusal carries its not-found digest after HTTP 200;
    // the document is 404. Both must discard the previously rendered body.
    const hidden = rsc
      ? await call(true, 3000, course, { Host: first.host + ':3000', RSC: '1' })
      : await representation(first, course, false, {}, 404);
    if (rsc) {
      safeResponse(hidden, 200, true);
      assert.ok(hidden.body.includes('NEXT_HTTP_ERROR_FALLBACK;404'), 'RSC not-found digest');
      assert.equal(
        hidden.body.includes(first.name),
        false,
        'Refused RSC has no tenant representation',
      );
      assert.equal(hidden.body.includes(second.name), false, 'Refused RSC has no opposite tenant');
    }
    assert.equal(hidden.body.includes(first.courseTitle), false, 'Draft course is hidden');
  }
  stage = 'unaffected tenant after draft';
  assert.ok((await representation(second, course)).body.includes(second.courseTitle));
  assert.equal(native.pid, nativePid);
  assert.equal(native.exitCode, null, 'Freshness used the same live native process');
  await checkpoint('restore-published');
  stage = 'restored publication';
  assert.ok(
    (await representation(first, course)).body.includes(first.courseTitle),
    'Restored row is fresh too',
  );

  stage = 'private output containment';
  assert.equal(privateOutput, false, 'Private data entered child logs');
  assert.ok(scanClientAssets(join(app, '.next/static')) > 0);
  await checkpoint('verified');
} catch {
  // Assertions and compiler/provider errors may contain private values. Report
  // only this finite test-owned stage; raw stdout/stderr never leave this process.
  process.stderr.write('Production rendering fixture failed during ' + stage + '.\n');
  if (stage === 'client import rejection' || stage === 'healthy production build') {
    const diagnostic = (children.at(-1)?.output ?? '')
      .replaceAll(configuration.secret, '[private]')
      .replaceAll(configuration.apiOrigin, '[private origin]')
      .replaceAll(fixtureRoot, '[fixture]')
      .replace(/x-learnstack-[a-z-]+/gi, '[private header]')
      .replace(/\bv1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}/g, '[private provenance]')
      .replace(/\s+/g, ' ')
      .slice(-2000);
    process.stderr.write('Production rendering fixture compiler diagnostic: ' + diagnostic + '\n');
  }
  process.exitCode = 1;
} finally {
  finished = true;
  try {
    await dispose();
  } catch {
    process.stderr.write('Production rendering fixture cleanup failed.\n');
    process.exitCode = 1;
  }
  input.close();
}
