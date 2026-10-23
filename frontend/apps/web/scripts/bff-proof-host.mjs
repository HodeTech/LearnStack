// Shared test-owned native Next fixture. External mode retains real RP/API dependencies.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { createPrivateScanner, fixtureEnvironment } from './fixture-support.mjs';
const source = fileURLToPath(new URL('../', import.meta.url));
function file(path, value) {
  mkdirSync(join(path, '..'), { recursive: true });
  writeFileSync(path, value);
}
function replace(source, needle, value) {
  assert.equal(source.split(needle).length, 2, 'unique instrumentation anchor');
  return source.replace(needle, value);
}
export async function startBffProof({
  owner,
  root,
  privateValues,
  secret,
  marker,
  devMode = false,
  uiMode = false,
  mutation,
  clockBase = 1800000000000,
  external,
  fakeLogout = 'https://identity.example.test/logout',
  mark = () => {},
  sites = [
    { host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' },
    { host: 'other.example.test:3000', displayName: 'Diğer kurum', locale: 'tr' },
  ],
}) {
  const app = join(root, 'frontend/apps/web');
  const scanners = [];
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
            host: sites[0].host,
            ...(cookie ? { cookie } : {}),
            ...extra.headers,
          },
        },
        (response) => {
          let body = '';
          let bytes = 0;
          response.setEncoding('utf8');
          response.on('data', (chunk) => {
            bytes += Buffer.byteLength(chunk);
            if (bytes > 2 * 1024 * 1024) {
              call.destroy(new Error('owned response body limit'));
              response.destroy();
              return;
            }
            body += chunk;
          });
          response.once('error', reject);
          response.once('aborted', () => reject(new Error('owned response aborted')));
          response.once('end', () =>
            resolve({ status: response.statusCode, headers: response.headers, body }),
          );
        },
      );
      // A real callback can traverse three separate ten-second owners: bootstrap,
      // provider and AccountSite. This test-client ceiling changes none of them.
      call.setTimeout(external ? 35_000 : 10_000, () =>
        call.destroy(new Error('owned request deadline')),
      );
      call.once('error', reject);
      call.end(extra.body);
    });
  }
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
  if (mutation === 'activity') {
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
  if (mutation === 'history') {
    const history = join(app, 'src/components/auth/history.ts');
    file(history, replace(readFileSync(history, 'utf8'), 'location.reload()', 'void 0'));
  }
  let launcher = readFileSync(join(source, 'scripts/public-server.mjs'), 'utf8');
  if (mutation === 'diagnostics')
    launcher = replace(
      launcher,
      "  process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';",
      '',
    );
  if (!external) {
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
    const dependencies = { provider: { begin: async callbackUri => ({ transaction: { callbackUri, state: randomBytes(32).toString('base64url'), nonce: randomBytes(32).toString('base64url'), verifier: randomBytes(32).toString('base64url') }, redirectUrl: 'https://identity.example.test/authorize' }), redeem: async () => credentials(), refresh: async () => { ++refreshes; return credentials(); }, logoutUrl: () => ${JSON.stringify(fakeLogout)} }, account: async (_token, binding) => { ++accounts; return ({ actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName }); }, clock: { monotonic: () => now, utc: () => ${clockBase} + now } };`,
    );
  }
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
      'subjectAltName=DNS:localhost,' +
        sites.map((site) => 'DNS:' + site.host.split(':')[0]).join(','),
    ],
    { timeout: 10_000, stdio: 'ignore' },
  );
  let backend;
  if (!external) {
    backend = owner.ownServer(
      createServer((request, response) => {
        assert.equal(request.url, '/api/v1/public/site');
        assert.equal(request.headers.authorization, undefined);
        if (!sites.some((site) => site.host === request.headers['x-learnstack-host'])) {
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
        const site = sites.find((site) => site.host === request.headers['x-learnstack-host']);
        response.writeHead(200, {
          'content-type': 'application/json',
          'cache-control': 'no-store',
        });
        response.end(
          JSON.stringify({
            displayName: site.displayName,
            enabledLocales: [site.locale],
            defaultLocale: site.locale,
            theme: null,
            showPlatformAttribution: true,
          }),
        );
      }),
    );
    await new Promise((resolve) => backend.listen(0, '127.0.0.1', resolve));
  }
  const env = fixtureEnvironment({
    root,
    bffValues: {
      LEARNSTACK_BFF_ENABLED: 'true',
      LEARNSTACK_BFF_ISSUER: external?.issuer ?? 'https://identity.example.test/realms/learnstack',
      KEYCLOAK_WEB_CLIENT_SECRET: external?.clientSecret ?? marker,
    },
    privateValues: {
      LEARNSTACK_PUBLIC_API_ORIGIN:
        external?.apiOrigin ?? 'http://127.0.0.1:' + backend.address().port,
      LEARNSTACK_PUBLIC_HOP_SECRET: secret,
      LEARNSTACK_PUBLIC_TLS_CERT: join(root, 'cert.pem'),
      LEARNSTACK_PUBLIC_TLS_KEY: join(root, 'key.pem'),
    },
  });
  if (external) {
    file(join(root, 'provider-ca.pem'), external.authority);
    env.NODE_EXTRA_CA_CERTS = join(root, 'provider-ca.pem');
  }
  // Compile the copied current sources: never let stale workstation output
  // qualify the native side while Next compiles different bundled sources.
  cpSync(join(source, 'tsconfig.server.json'), join(app, 'tsconfig.server.json'));
  mark('compile');
  const native = child(
    [join(source, 'node_modules/typescript/bin/tsc'), '--project', 'tsconfig.server.json'],
    env,
  );
  assert.equal((await native.completed).code, 0, 'owned current-source native compilation');
  mark('build');
  const build = child([join(source, 'node_modules/next/dist/bin/next'), 'build'], env);
  const buildResult = await build.completed;
  if (buildResult.code !== 0 && !external) {
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
  mark('start');
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
  return { app, server, advance, get, scanners };
}
export const jar = (response) =>
  (response.headers['set-cookie'] ?? [])
    .map((value) => value.split(';', 1)[0])
    .filter((value) => !value.endsWith('='))
    .join('; ');
