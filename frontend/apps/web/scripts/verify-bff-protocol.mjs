// Test-owned production Next copy. Fake grant/account capabilities here; real provider chain is separate.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, symlinkSync, writeFileSync } from 'node:fs';
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

const source = fileURLToPath(new URL('../', import.meta.url));
const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-protocol-')));
const app = join(root, 'frontend/apps/web');
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const scanners = [];
const deadline = setTimeout(() => {
  void owner.cancel();
}, 120_000);
deadline.unref();
let stage = 'setup';
function file(path, value) {
  mkdirSync(join(path, '..'), { recursive: true });
  writeFileSync(path, value);
}
function replace(source, needle, value) {
  assert.equal(source.split(needle).length, 2, 'unique instrumentation anchor');
  return source.replace(needle, value);
}
function child(args, env) {
  const process = owner.ownChild(
    spawn(globalThis.process.execPath, args, {
      cwd: app,
      env,
      detached: globalThis.process.platform !== 'win32',
      stdio: ['ignore', 'pipe', 'pipe'],
    }),
  );
  const scanner = createPrivateScanner([secret, marker]);
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
  cpSync(join(source, 'src/server/auth'), join(app, 'src/server/auth'), {
    recursive: true,
    filter: (path) => !path.endsWith('.test.ts'),
  });
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
  nextConfiguration = replace(
    nextConfiguration,
    "import createNextIntlPlugin from 'next-intl/plugin';",
    '',
  );
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
  let launcher = readFileSync(join(source, 'scripts/public-server.mjs'), 'utf8');
  launcher = replace(
    launcher,
    "import { installPublicAdmissionRuntime } from '../.server/public-admission-runtime.js';",
    `import { installPublicAdmissionRuntime } from '../.server/public-admission-runtime.js';
import { installBffSessionRuntime } from '../.server/auth/session-runtime.js';
import { installBffRequestRuntime } from '../.server/auth/request-runtime.js';
import { randomBytes } from 'node:crypto';`,
  );
  launcher = replace(
    launcher,
    '  const admission = installPublicAdmissionRuntime();',
    `  const admission = installPublicAdmissionRuntime(true);
  const bffRequests = installBffRequestRuntime();
  const credentials = () => ({ accessToken: '${marker}', refreshToken: '${marker}', idToken: '${marker}', identity: { issuer: 'https://identity.example.test/realms/learnstack', subject: 'owned-subject', actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null }, accessExpiresAt: performance.now() + 900000, refreshExpiresAt: performance.now() + 3600000 });
  const bffSessions = installBffSessionRuntime({ provider: { begin: async callbackUri => ({ transaction: { callbackUri, state: randomBytes(32).toString('base64url'), nonce: randomBytes(32).toString('base64url'), verifier: randomBytes(32).toString('base64url') }, redirectUrl: 'https://identity.example.test/authorize' }), redeem: async () => credentials(), refresh: async () => credentials(), logoutUrl: () => 'https://identity.example.test/logout' }, account: async () => ({ actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayName: 'Institution' }) });`,
  );
  launcher = replace(
    launcher,
    'if (!admitIncomingRequest(request, configuration.secret)) return refuseIngress(response);',
    'if (!admitIncomingRequest(request, configuration.secret, true)) return refuseIngress(response);',
  );
  launcher = replace(
    launcher,
    '.then(() => admission.run(binding, request, response, () => handle(request, response)))',
    '.then(() => admission.run(binding, request, response, () => bffRequests.run(binding, request, response, () => handle(request, response))))',
  );
  launcher = replace(
    launcher,
    '    admission.shutdown();\n    // Node',
    '    admission.shutdown();\n    bffRequests.stop(); await bffSessions.stop();\n    // Node',
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
      response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      response.end(
        JSON.stringify({
          displayName: 'Institution',
          enabledLocales: ['en'],
          defaultLocale: 'en',
          theme: null,
          showPlatformAttribution: true,
        }),
      );
    }),
  );
  await new Promise((resolve) => backend.listen(0, '127.0.0.1', resolve));
  const env = fixtureEnvironment({
    root,
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
      .filter((line) => /Type error:|Module not found:|Cannot find module|Error:/.test(line))
      .join('\n')
      .replaceAll(secret, '[private]')
      .replaceAll(marker, '[private]');
    process.stderr.write(diagnostics.slice(0, 2000) + '\n');
  }
  assert.equal(buildResult.code, 0, 'owned Next build');
  stage = 'start';
  const server = child(['scripts/public-server.mjs'], env);
  const until = Date.now() + 20_000;
  while (!server.scanner.tail.includes('Public HTTPS listener ready on 127.0.0.1:3000')) {
    assert.equal(server.process.exitCode, null, 'native listener alive');
    assert.ok(Date.now() < until, 'listener startup deadline');
    await new Promise((resolve) => setTimeout(resolve, 25));
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
  stage = 'callback';
  const callback = await get(
    '/api/auth/callback/keycloak?state=' + state + '&code=owned',
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
  assert.match(document.body, /<h1>authenticated<\/h1>/);
  assert.equal(document.body.includes(marker), false);
  const flight = await get('/studio?_rsc=probe', active, { headers: { RSC: '1' } });
  assert.equal(flight.status, 200);
  assert.match(flight.headers['content-type'], /text\/x-component/);
  assert.equal(flight.body.includes(marker), false);
  stage = 'session';
  const session = await get('/api/auth/session', active);
  const projection = JSON.parse(session.body);
  assert.equal(projection.state, 'authenticated');
  assert.equal(session.body.includes(marker), false);
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
  assert.match(refused.body, /<h1>anonymous<\/h1>/);
  stage = 'cleanup';
  await owner.dispose();
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
    'private output scanner',
  );
  clearTimeout(deadline);
  process.stdout.write('BFF native Next protocol: passed\n');
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
