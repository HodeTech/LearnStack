// Test-owned production Next copy. Fake grant/account capabilities here; real provider chain is separate.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const source = fileURLToPath(new URL('../', import.meta.url));
const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-protocol-')));
const app = join(root, 'frontend/apps/web');
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const callbackCode = randomBytes(32).toString('base64url');
const errorCanary = randomBytes(32).toString('base64url');
const privateValues = [secret, marker, callbackCode, errorCanary];
const scanners = [];
const deadline = setTimeout(() => {
  void owner.cancel();
}, 120_000);
deadline.unref();
const devMode = ['--dev', '--without-native-diagnostics'].includes(process.argv[2]);
const uiMode = devMode || process.argv[2] === '--ui';
let stage = 'setup';
function file(path, value) {
  mkdirSync(join(path, '..'), { recursive: true });
  writeFileSync(path, value);
}
function replace(source, needle, value) {
  assert.equal(source.split(needle).length, 2, 'unique instrumentation anchor');
  return source.replace(needle, value);
}
function child(args, env, ipc = false) {
  const process = owner.ownChild(
    spawn(globalThis.process.execPath, args, {
      cwd: app,
      env,
      detached: globalThis.process.platform !== 'win32',
      stdio: ['ignore', 'pipe', 'pipe', ...(ipc ? ['ipc'] : [])],
    }),
  );
  const scanner = createPrivateScanner(privateValues);
  scanners.push(scanner);
  for (const stream of [process.stdout, process.stderr]) {
    stream.on('data', (chunk) => scanner.push(chunk));
    stream.once('end', () => scanner.finish());
  }
  process.once('error', () => {});
  return {
    process,
    scanner,
    completed: new Promise((resolve) =>
      process.once('close', (code, signal) => resolve({ code, signal })),
    ),
  };
}
function get(path, cookie, extra = {}) {
  return new Promise((resolve, reject) => {
    const call = request(
      {
        hostname: '127.0.0.1',
        port: 3000,
        method: extra.method ?? 'GET',
        path,
        ca: readFileSync(join(root, 'cert.pem')),
        servername: 'localhost',
        agent: false,
        headers: {
          host: 'institution.example.test:3000',
          ...(cookie ? { cookie } : {}),
          ...extra.headers,
        },
      },
      (response) => {
        let body = '';
        response.setEncoding('utf8');
        response.on('data', (chunk) => {
          body += chunk;
          assert.ok(body.length <= 2 * 1024 * 1024);
        });
        response.once('end', () =>
          resolve({ status: response.statusCode, headers: response.headers, body }),
        );
      },
    );
    call.setTimeout(10_000, () => call.destroy(new Error('owned request deadline')));
    call.once('error', reject);
    call.end(extra.body);
  });
}
const jar = (response) =>
  (response.headers['set-cookie'] ?? [])
    .map((value) => value.split(';', 1)[0])
    .filter((value) => !value.endsWith('='))
    .join('; ');
try {
  assert.ok(
    process.argv.length === 2 ||
      (process.argv.length === 3 &&
        ['--without-native-activity', '--ui', '--dev', '--without-native-diagnostics'].includes(
          process.argv[2],
        )),
    'closed proof mode',
  );
  mkdirSync(join(app, 'src/server/auth'), { recursive: true });
  mkdirSync(join(app, 'scripts'), { recursive: true });
  for (const name of [
    'ingress.ts',
    'public-api-limits.ts',
    'public-entry.ts',
    'configured-public-client.ts',
    'public-admission.ts',
    'public-admission-runtime.ts',
  ])
    cpSync(join(source, 'src/server', name), join(app, 'src/server', name));
  // This minimal app qualifies the protocol, not the separate product UI.
  for (const name of [
    'account-client',
    'auth',
    'configuration',
    'cookies',
    'http-admission',
    'http-input',
    'http-paths',
    'http-protocol',
    'provider',
    'provider-core',
    'provider-error',
    'provider-transport',
    'request-owner',
    'request-runtime',
    'session-contract',
    'session-projection',
    'session-owner',
    'session-runtime',
    'session-store',
  ])
    cpSync(
      join(source, 'src/server/auth', name + '.ts'),
      join(app, 'src/server/auth', name + '.ts'),
    );
  if (process.argv[2] === '--without-native-activity') {
    const path = join(app, 'src/server/auth/http-admission.ts');
    file(
      path,
      replace(
        readFileSync(path, 'utf8'),
        'protocol.project(input, bffRequestOwner().activity(context))',
        'protocol.project(input)',
      ),
    );
  }
  symlinkSync(join(source, 'node_modules'), join(app, 'node_modules'), 'dir');
  symlinkSync(join(source, '.server'), join(app, '.server'), 'dir');
  cpSync(join(source, 'scripts/development-hmr.mjs'), join(app, 'scripts/development-hmr.mjs'));
  file(
    join(app, 'package.json'),
    JSON.stringify({ name: 'learnstack-owned-bff-proof', private: true, type: 'module' }),
  );
  file(
    join(app, 'tsconfig.json'),
    JSON.stringify({
      compilerOptions: {
        target: 'ES2022',
        lib: ['dom', 'dom.iterable', 'esnext'],
        strict: true,
        noEmit: true,
        module: 'esnext',
        moduleResolution: 'bundler',
        jsx: 'preserve',
        esModuleInterop: true,
        skipLibCheck: true,
        resolveJsonModule: true,
        isolatedModules: true,
        plugins: [{ name: 'next' }],
        paths: { '@/*': ['./src/*'] },
      },
      include: ['next-env.d.ts', 'src/**/*.ts', 'src/**/*.tsx', '.next/types/**/*.ts'],
      exclude: ['node_modules'],
    }),
  );
  file(
    join(app, 'next-env.d.ts'),
    '/// <reference types="next" />\n/// <reference types="next/image-types/global" />\n',
  );
  let nextConfiguration = readFileSync(join(source, 'next.config.ts'), 'utf8');
  if (!uiMode)
    nextConfiguration = replace(
      nextConfiguration,
      "import createNextIntlPlugin from 'next-intl/plugin';",
      '',
    );
  if (!uiMode)
    nextConfiguration = replace(
      nextConfiguration,
      "export default createNextIntlPlugin('./src/i18n/request.ts')(nextConfig);",
      'export default nextConfig;',
    );
  nextConfiguration = replace(
    nextConfiguration,
    'serverComponentsHmrCache: false',
    'serverComponentsHmrCache: false, cpus: 2',
  );
  file(join(app, 'next.config.ts'), nextConfiguration);
  file(
    join(app, 'src/app/layout.tsx'),
    'export default function Layout({children}: {children: React.ReactNode}) { return <html lang="en"><body>{children}</body></html>; }',
  );
  file(
    join(app, 'src/app/studio/page.tsx'),
    `import { auth } from '@/server/auth/auth';
export const dynamic = 'force-dynamic'; export const revalidate = 0;
export default async function Page() { const a = await auth(); const b = await auth(); if (a !== b) throw new Error('request snapshot identity'); return <main><h1>{a?.state ?? 'anonymous'}</h1><p>{a?.displayName}</p></main>; }`,
  );
  file(
    join(app, 'src/app/page.tsx'),
    'export default function Page() { return <main>Public</main>; }',
  );
  file(
    join(app, 'src/middleware.ts'),
    `import { NextResponse, type NextRequest } from 'next/server';
import { createConfiguredPublicClient } from '@/server/configured-public-client';
import { INGRESS_HEADER, verifyNextProvenance } from '@/server/ingress';
import { beginPublicAdmission } from '@/server/public-admission';
import { admitBffHttp } from '@/server/auth/http-admission';
export async function middleware(request: NextRequest) {
  const url = new URL(request.url); const envelope = request.headers.get(INGRESS_HEADER);
  const context = verifyNextProvenance(envelope, process.env.LEARNSTACK_PUBLIC_HOP_SECRET, { method: request.method, target: url.pathname + url.search });
  if (!context) return new NextResponse('Not found', { status: 404 });
  const admission = beginPublicAdmission(context); const client = createConfiguredPublicClient(envelope, { signal: admission.signal });
  const site = await client!.getSite(); if (site.kind !== 'success') { admission.refuse(); return new NextResponse('Not found', { status: 404 }); }
  admission.publish(site.data);
  const result = await admitBffHttp(request, context, admission.signal);
  if (result.response) return result.response;
  const headers = new Headers({ [INGRESS_HEADER]: envelope! });
  const response = NextResponse.next({ request: { headers } }); response.headers.set('cache-control', 'no-store'); return response;
}
export const config = { runtime: 'nodejs', matcher: ['/((?!_next/|favicon\\.ico$).*)'] };`,
  );
  if (uiMode) {
    rmSync(join(app, 'src/app/studio'), { recursive: true });
    rmSync(join(app, 'src/app/page.tsx'));
    cpSync(join(source, 'src'), join(app, 'src'), {
      recursive: true,
      filter: (path) => !/(?:\.(?:test|spec)\.tsx?$|[/]test(?:[/]|$))/.test(path),
    });
    for (const name of ['tailwind.config.ts', 'postcss.config.cjs'])
      cpSync(join(source, name), join(app, name));
  }
  let launcher = readFileSync(join(source, 'scripts/public-server.mjs'), 'utf8');
  if (process.argv[2] === '--without-native-diagnostics')
    launcher = replace(
      launcher,
      "  process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';",
      '',
    );
  launcher = replace(
    launcher,
    "import { bffConfiguration } from '../.server/auth/configuration.js';",
    "import { bffConfiguration } from '../.server/auth/configuration.js';\nimport { randomBytes } from 'node:crypto';",
  );
  launcher = replace(
    launcher,
    `    const dependencies = {
      provider: createTenantProvider(bff, globalThis.fetch),
      account: createAccountSiteCaller(configuration, globalThis.fetch),
      diagnostic: (reason) => console.warn('BFF session refused', { reason }),
    };`,
    `    let now = 0; let refreshes = 0; let accounts = 0;
    process.on('message', async value => { if (value && value.kind === 'advance' && [0, 60000, 901000].includes(value.milliseconds)) { now += value.milliseconds; await (await import('next/dist/trace/index.js')).flushAllTraces(); process.send?.({ kind: 'snapshot', now, refreshes, accounts }); } });
    const credentials = () => ({ accessToken: '${marker}', refreshToken: '${marker}', idToken: '${marker}', identity: { issuer: 'https://identity.example.test/realms/learnstack', subject: 'owned-subject', actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null }, accessExpiresAt: now + 900000, refreshExpiresAt: now + 3600000 });
    const dependencies = { provider: { begin: async callbackUri => ({ transaction: { callbackUri, state: randomBytes(32).toString('base64url'), nonce: randomBytes(32).toString('base64url'), verifier: randomBytes(32).toString('base64url') }, redirectUrl: 'https://identity.example.test/authorize' }), redeem: async () => credentials(), refresh: async () => { ++refreshes; return credentials(); }, logoutUrl: () => 'https://identity.example.test/logout' }, account: async (_token, binding) => { ++accounts; return ({ actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayName: binding.host === 'other.example.test:3000' ? 'Diğer kurum' : 'Institution' }); }, clock: { monotonic: () => now, utc: () => 1800000000000 + now } };`,
  );
  file(join(app, 'scripts/public-server.mjs'), launcher);
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      join(root, 'key.pem'),
      '-out',
      join(root, 'cert.pem'),
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-addext',
      'subjectAltName=DNS:localhost',
    ],
    { timeout: 10_000, stdio: 'ignore' },
  );
  const backend = owner.ownServer(
    createServer((request, response) => {
      assert.equal(request.url, '/api/v1/public/site');
      assert.equal(request.headers.authorization, undefined);
      if (
        !['institution.example.test:3000', 'other.example.test:3000'].includes(
          request.headers['x-learnstack-host'],
        )
      ) {
        response.writeHead(404, { 'content-type': 'application/problem+json' });
        response.end(
          JSON.stringify({
            status: 404,
            code: 'not_found',
            messageKey: 'lockey_not_found',
            correlationId: '00000000-0000-4000-8000-000000000001',
            type: 'https://fixture.invalid/not-found',
            title: 'Not found',
            instance: '/api/v1/public/site',
          }),
        );
        return;
      }
      const second = request.headers['x-learnstack-host'] === 'other.example.test:3000';
      response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      response.end(
        JSON.stringify({
          displayName: second ? 'Diğer kurum' : 'Institution',
          enabledLocales: second ? ['tr'] : ['en'],
          defaultLocale: second ? 'tr' : 'en',
          theme: null,
          showPlatformAttribution: true,
        }),
      );
    }),
  );
  await new Promise((resolve) => backend.listen(0, '127.0.0.1', resolve));
  const env = fixtureEnvironment({
    root,
    bffValues: {
      LEARNSTACK_BFF_ENABLED: 'true',
      LEARNSTACK_BFF_ISSUER: 'https://identity.example.test/realms/learnstack',
      KEYCLOAK_WEB_CLIENT_SECRET: marker,
    },
    privateValues: {
      LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:' + backend.address().port,
      LEARNSTACK_PUBLIC_HOP_SECRET: secret,
      LEARNSTACK_PUBLIC_TLS_CERT: join(root, 'cert.pem'),
      LEARNSTACK_PUBLIC_TLS_KEY: join(root, 'key.pem'),
    },
  });
  stage = 'build';
  const build = child([join(source, 'node_modules/next/dist/bin/next'), 'build'], env);
  const buildResult = await build.completed;
  if (buildResult.code !== 0) {
    const diagnostics = build.scanner.tail
      .split('\n')
      .filter((line) => /error|warning|cannot|failed/i.test(line))
      .join('\n')
      .replaceAll(secret, '[private]')
      .replaceAll(marker, '[private]');
    process.stderr.write(diagnostics.slice(0, 2000) + '\n');
  }
  assert.equal(buildResult.code, 0, 'owned Next build');
  const buildTrace = devMode ? readFileSync(join(app, '.next/trace'), 'utf8') : '';
  if (devMode)
    assert.ok(buildTrace.length > 0, 'separate build leaves a nonempty real collector control');
  stage = 'start';
  const server = child(
    ['scripts/public-server.mjs', ...(devMode ? ['--dev'] : [])],
    devMode ? { ...env, NODE_ENV: 'development' } : env,
    true,
  );
  const advance = (milliseconds) =>
    new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('owned IPC deadline')), 5000);
      server.process.once('message', (snapshot) => {
        clearTimeout(timer);
        if (snapshot?.kind !== 'snapshot') reject(new Error('owned IPC shape'));
        else resolve(snapshot);
      });
      server.process.send({ kind: 'advance', milliseconds });
    });
  const until = Date.now() + 20_000;
  while (!server.scanner.tail.includes('Public HTTPS listener ready on 127.0.0.1:3000')) {
    assert.equal(server.process.exitCode, null, 'native listener alive');
    assert.ok(Date.now() < until, 'listener startup deadline');
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  if (uiMode) {
    stage = 'ui-signin';
    for (const [host, label] of [
      ['institution.example.test:3000', 'Continue to sign in'],
      ['other.example.test:3000', 'Giriş yapmaya devam et'],
    ]) {
      const page = await get('/api/auth/signin?returnTo=%2Fstudio', undefined, {
        headers: { host, Accept: 'text/html' },
      });
      stage = host.startsWith('other.') ? 'ui-signin-tr-status' : 'ui-signin-en-status';
      if (page.status !== 200) process.stderr.write(JSON.stringify({ status: page.status }) + '\n');
      assert.equal(page.status, 200);
      stage = host.startsWith('other.') ? 'ui-signin-tr-render' : 'ui-signin-en-render';
      assert.ok(page.body.includes(label));
      assert.ok(page.body.includes('name="csrfToken"'));
      assert.equal(page.body.includes(marker), false);
      assert.equal(page.headers['cache-control'], 'no-store');
      const head = await get('/api/auth/signin', undefined, { method: 'HEAD', headers: { host } });
      stage = 'ui-signin-head';
      assert.equal(head.status, 200);
      assert.equal(head.body, '');
    }
    stage = 'ui-unknown-host';
    const unknown = await get('/api/auth/signin', undefined, {
      headers: { host: 'unknown.example.test:3000' },
    });
    assert.equal(unknown.status, 404);
    assert.equal(unknown.headers['set-cookie'], undefined);
  }
  stage = 'csrf';
  const csrf = await get('/api/auth/csrf');
  assert.equal(csrf.status, 200);
  const token = JSON.parse(csrf.body).csrfToken;
  stage = 'signin';
  const signIn = await get('/api/auth/signin/keycloak', jar(csrf), {
    method: 'POST',
    headers: {
      origin: 'https://institution.example.test:3000',
      'content-type': 'application/x-www-form-urlencoded',
    },
    body: 'csrfToken=' + token + '&returnTo=%2Fstudio',
  });
  assert.equal(signIn.status, 303);
  assert.equal(signIn.headers['set-cookie'].length, 2);
  const transaction = signIn.headers['set-cookie'].find((value) =>
    value.startsWith('__Host-ls-login-'),
  );
  const state = transaction.slice('__Host-ls-login-'.length).split('=')[0];
  privateValues.push(state);
  stage = 'callback';
  const callback = await get(
    '/api/auth/callback/keycloak?state=' + state + '&code=' + callbackCode,
    jar(signIn),
  );
  if (callback.status !== 303) {
    let reason = 'unknown';
    try {
      const code = JSON.parse(callback.body).code;
      if (
        [
          'configuration',
          'cancelled',
          'revoked',
          'response',
          'identity',
          'transaction',
          'expired',
          'invalid_request',
        ].includes(code)
      )
        reason = code;
    } catch {
      /* A framework error is not a closed BFF response. */
    }
    process.stderr.write(JSON.stringify({ status: callback.status, reason }) + '\n');
  }
  assert.equal(callback.status, 303);
  const active = jar(callback);
  stage = 'rsc';
  const document = await get('/studio', active);
  assert.equal(document.status, 200);
  if (uiMode) {
    assert.match(document.body, /<h1>Studio<\/h1>/);
    assert.ok(document.body.includes('Account access'));
    assert.ok(document.body.includes('bff-history'));
  } else assert.match(document.body, /<h1>authenticated<\/h1>/);
  assert.equal(document.body.includes(marker), false);
  const before = await advance(901000);
  const prior = JSON.parse((await get('/api/auth/session', active)).body);
  const flight = await get('/studio?_rsc=probe', active, {
    headers: { RSC: '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
  });
  assert.equal(flight.status, 200);
  assert.match(flight.headers['content-type'], /text\/x-component/);
  assert.equal(flight.body.includes(marker), false);
  const after = await advance(0);
  assert.equal(after.refreshes, before.refreshes + 1, 'actual RSC refreshes expired access');
  assert.equal(after.accounts, before.accounts + 1, 'actual RSC validates AccountSite');
  const session = await get('/api/auth/session', active);
  const projection = JSON.parse(session.body);
  assert.equal(projection.expiresAt, prior.expiresAt + 901000, 'RSC advances effective idle');
  assert.equal(projection.state, 'authenticated');
  assert.equal(session.body.includes(marker), false);
  stage = 'passive';
  await advance(60000);
  const prefetched = await get('/studio?_rsc=prefetch', active, {
    headers: { RSC: '1', 'Next-Router-Prefetch': '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
  });
  assert.equal(prefetched.status, 200);
  const head = await get('/studio', active, { method: 'HEAD', headers: { Accept: 'text/html' } });
  assert.equal(head.status, 200);
  assert.equal(head.body, '');
  assert.equal(
    JSON.parse((await get('/api/auth/session', active)).body).expiresAt,
    projection.expiresAt,
    'prefetch/HEAD/poll never touch idle',
  );
  const passive = await advance(0);
  assert.equal(passive.accounts, after.accounts);
  assert.equal(passive.refreshes, after.refreshes);
  if (uiMode) {
    stage = 'ui-host-fence';
    const crossHost = await get('/portal', active, {
      headers: { host: 'other.example.test:3000' },
    });
    assert.equal(crossHost.status, 303);
    assert.ok(
      crossHost.headers.location.startsWith('https://other.example.test:3000/api/auth/signin'),
    );
    const csrfB = await get('/api/auth/csrf', undefined, {
      headers: { host: 'other.example.test:3000' },
    });
    const startB = await get('/api/auth/signin/keycloak', jar(csrfB), {
      method: 'POST',
      headers: {
        host: 'other.example.test:3000',
        origin: 'https://other.example.test:3000',
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: 'csrfToken=' + JSON.parse(csrfB.body).csrfToken + '&returnTo=%2Fportal',
    });
    assert.equal(startB.status, 303);
    const stateB = startB.headers['set-cookie']
      .find((value) => value.startsWith('__Host-ls-login-'))
      .slice('__Host-ls-login-'.length)
      .split('=')[0];
    const callbackB = await get(
      '/api/auth/callback/keycloak?state=' + stateB + '&code=owned',
      jar(startB),
      { headers: { host: 'other.example.test:3000' } },
    );
    assert.equal(callbackB.status, 303);
    const portal = await get('/portal', jar(callbackB), {
      headers: { host: 'other.example.test:3000' },
    });
    assert.equal(portal.status, 200);
    assert.match(portal.body, /<html lang="tr"/);
    assert.ok(portal.body.includes('Portalınız'));
    assert.ok(portal.body.includes('Diğer kurum'));
    assert.equal(portal.body.includes(marker), false);
    stage = 'ui-extension';
    const extended = await get('/api/auth/extend', active, {
      method: 'POST',
      headers: {
        origin: 'https://institution.example.test:3000',
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: 'csrfToken=' + projection.csrfToken,
    });
    assert.equal(extended.status, 200);
    assert.ok(JSON.parse(extended.body).expiresAt > projection.expiresAt);
  }
  stage = 'logout';
  const logout = await get('/api/auth/signout', active, {
    method: 'POST',
    headers: {
      origin: 'https://institution.example.test:3000',
      'content-type': 'application/x-www-form-urlencoded',
    },
    body: 'csrfToken=' + projection.csrfToken,
  });
  assert.equal(logout.status, 303);
  stage = 'revocation';
  const refused = await get('/studio', active);
  if (uiMode) {
    assert.equal(refused.status, 303);
    assert.ok(refused.headers.location.includes('/api/auth/signin'));
  } else assert.match(refused.body, /<h1>anonymous<\/h1>/);
  if (devMode) {
    stage = 'dev-logout-return';
    const logoutState = logout.headers['set-cookie']
      .find((value) => value.startsWith('__Host-ls-logout-'))
      .slice('__Host-ls-logout-'.length)
      .split('=')[0];
    privateValues.push(logoutState);
    const returned = await get('/?state=' + logoutState, active + '; ' + jar(logout));
    assert.equal(returned.status, 303);
    const failedCallback = await get(
      '/api/auth/callback/keycloak?state=' +
        randomBytes(32).toString('base64url') +
        '&error=access_denied&error_description=' +
        errorCanary,
      active,
    );
    assert.equal(failedCallback.status, 400);
    assert.equal(failedCallback.body.includes(errorCanary), false);
    await advance(0); // Flush the pinned real reporter; no raw artifact leaves this owned root.
    await stopTestChild(server.process);
    const stopped = await server.completed;
    assert.equal(stopped.code, 0, 'development owner exits naturally before artifact inspection');
    assert.equal(stopped.signal, null);
    stage = 'dev-diagnostics';
    let trace = '';
    try {
      trace = readFileSync(join(app, '.next/trace'), 'utf8');
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
    for (const canary of [callbackCode, errorCanary, state, logoutState]) {
      stage = 'dev-trace';
      assert.equal(trace.includes(canary), false, 'no auth URL value in persisted trace');
      stage = 'dev-console';
      assert.equal(
        scanners.some((scanner) => scanner.tail.includes(canary)),
        false,
        'no auth URL value in console output',
      );
    }
  }
  stage = 'cleanup';
  await owner.dispose();
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
    'private output scanner',
  );
  clearTimeout(deadline);
  process.stdout.write(
    devMode
      ? 'BFF development diagnostics: passed\n'
      : uiMode
        ? 'BFF production UI: passed\n'
        : 'BFF native Next protocol: passed\n',
  );
} catch {
  try {
    await owner.dispose();
  } catch {
    stage = 'cleanup';
  }
  clearTimeout(deadline);
  process.stderr.write('BFF native Next protocol failed: ' + stage + '\n');
  process.exitCode = 1;
}
