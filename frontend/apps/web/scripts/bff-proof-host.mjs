// Shared test-owned native Next fixture. External mode retains real RP/API dependencies.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import {
  cpSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  bffListenerFailureDetail,
  createBffCookieScanner,
  rememberBffCookies,
  observeBffProofShutdown,
  waitForBffProofListener,
} from './bff-proof-support.mjs';
import { createPrivateScanner, fixtureEnvironment } from './fixture-support.mjs';
import { frameworkProofBuildArguments, prepareFrameworkProof } from './framework-proof-support.mjs';
const source = fileURLToPath(new URL('../', import.meta.url));
function file(path, value) {
  mkdirSync(join(path, '..'), { recursive: true });
  writeFileSync(path, value);
}
function replace(source, needle, value) {
  assert.equal(source.split(needle).length, 2, 'unique instrumentation anchor');
  return source.replace(needle, value);
}

/** Fixture-only copied source instrumentation; drift refuses before compilation. */
export function instrumentBffProofRequestRuntime(runtime) {
  runtime = replace(
    runtime,
    '  const active = new Set<Frame>();',
    `  const active = new Set<Frame>();
  const proofFrames = new Map<Frame, () => number[]>();`,
  );
  runtime = replace(
    runtime,
    '            active.delete(frame);',
    `            active.delete(frame);
            proofFrames.delete(frame);`,
  );
  runtime = replace(
    runtime,
    '      active.add(frame);',
    `      active.add(frame);
      proofFrames.set(frame, () => [Number(handlerPending), Number(!terminal), writes, waiting.size]);`,
  );
  return replace(
    runtime,
    '    snapshotCounts: pool.counts,',
    `    snapshotCounts: pool.counts,
    proofCounts() {
      const counts = [0, 0, 0, 0, Math.min(256, pool.counts().active)];
      for (const read of proofFrames.values()) {
        const values = read();
        for (let i = 0; i < 4; ++i) counts[i] = Math.min(256, counts[i]! + values[i]!);
      }
      return counts;
    },`,
  );
}

/** One real child/output path shared by the native proof and its observer control. */
export function ownBffProofChild({
  owner,
  app,
  args,
  env,
  ipc = false,
  privateValues,
  cookieCanaries,
  scanners,
}) {
  owner.assertActive();
  // This helper alone mints the parent identity for each detached POSIX group.
  // The watcher is confined to that live group leader, never an arbitrary PID.
  const useLifeline = globalThis.process.platform !== 'win32';
  const process = owner.ownChild(
    spawn(
      globalThis.process.execPath,
      useLifeline
        ? ['--import', new URL('./bff-proof-lifeline.mjs', import.meta.url).href, ...args]
        : args,
      {
        cwd: app,
        env: useLifeline
          ? { ...env, LEARNSTACK_TEST_LIFELINE_PARENT: String(globalThis.process.pid) }
          : env,
        detached: globalThis.process.platform !== 'win32',
        stdio: ['ignore', 'pipe', 'pipe', ...(ipc ? ['ipc'] : [])],
      },
    ),
  );
  const scanner = createPrivateScanner(privateValues);
  const cookieScanner = createBffCookieScanner(cookieCanaries);
  scanners.push(scanner, cookieScanner);
  for (const stream of [process.stdout, process.stderr]) {
    stream.on('data', (chunk) => {
      scanner.push(chunk);
      cookieScanner.push(chunk);
    });
    stream.once('end', () => {
      scanner.finish();
      cookieScanner.finish();
    });
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
export async function startBffProof({
  owner,
  root,
  privateValues,
  secret,
  marker,
  devMode = false,
  uiMode = false,
  disabledOwnPreference = false,
  mutation,
  clockBase = 1800000000000,
  external,
  isolatedProtocol = false,
  fakeIssuer = 'https://identity.example.test/realms/learnstack',
  fakeLogout = fakeIssuer + '/protocol/openid-connect/logout',
  mark = () => {},
  sites = [
    { host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' },
    { host: 'other.example.test:3000', displayName: 'Diğer kurum', locale: 'tr' },
  ],
}) {
  assert.ok(!isolatedProtocol || !external, 'isolated protocol excludes external capabilities');
  const app = join(root, 'frontend/apps/web');
  let listenerPort = 3000;
  const scanners = [];
  // CSRF cookies may appear in their intended HTML/JSON response. They remain
  // forbidden in child/browser diagnostics, independently of body projections.
  const cookieCanaries = [];
  function child(args, env, ipc = false) {
    return ownBffProofChild({
      owner,
      app,
      args,
      env,
      ipc,
      privateValues,
      cookieCanaries,
      scanners,
    });
  }
  function get(path, cookie, extra = {}) {
    return new Promise((resolve, reject) => {
      let observed = { status: null, headers: {}, body: '' };
      const call = request(
        {
          hostname: '127.0.0.1',
          port: listenerPort,
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
          observed = { status: response.statusCode, headers: response.headers, body: '' };
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
            observed.body = body;
          });
          const interrupted = () =>
            extra.allowAborted
              ? resolve({
                  status: response.statusCode,
                  headers: response.headers,
                  body,
                  aborted: true,
                })
              : reject(new Error('owned response aborted'));
          response.once('error', (error) => {
            if (extra.allowAborted && error.code === 'ECONNRESET') interrupted();
            else reject(error);
          });
          response.once('aborted', interrupted);
          response.once('end', () => {
            try {
              rememberBffCookies(response.headers['set-cookie'], cookieCanaries);
              resolve({ status: response.statusCode, headers: response.headers, body });
            } catch (error) {
              reject(error);
            }
          });
        },
      );
      // A real callback can traverse three separate ten-second owners: bootstrap,
      // provider and AccountSite. This test-client ceiling changes none of them.
      call.setTimeout(external ? 35_000 : 10_000, () =>
        call.destroy(new Error('owned request deadline')),
      );
      call.once('error', (error) => {
        if (extra.allowAborted && error.code === 'ECONNRESET')
          resolve({ ...observed, aborted: true });
        else reject(error);
      });
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
    'logout-response',
    'provider',
    'provider-core',
    'provider-error',
    'provider-transport',
    'request-owner',
    'request-runtime',
    'page',
    'page-contract',
    'page-snapshot-contract',
    'page-snapshot-pool',
    'page-response',
    'page-preload',
    'session-contract',
    'session-path',
    'native-contract',
    'native-operation-contract',
    'native-api-transport',
    'native-session-client',
    'entry-cursor',
    'native-administration-client',
    'http-forms',
    'input-error',
    'page-query',
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
    const path = join(app, 'src/server/auth/request-runtime.ts');
    file(
      path,
      replace(readFileSync(path, 'utf8'), 'activity: frame.activity,', "activity: 'passive',"),
    );
  }
  // Every proof owns its SDK output, including non-protocol browser/API copies.
  symlinkSync(join(source, 'node_modules'), join(app, 'node_modules'), 'dir');
  for (const name of ['tailwind.config.ts', 'postcss.config.cjs'])
    cpSync(join(source, name), join(app, name));
  cpSync(join(source, 'scripts/development-hmr.mjs'), join(app, 'scripts/development-hmr.mjs'));
  prepareFrameworkProof({ sourceApp: source, app });
  file(
    join(app, 'package.json'),
    JSON.stringify({
      name: 'learnstack-owned-bff-proof',
      private: true,
      type: 'module',
      dependencies: JSON.parse(readFileSync(join(source, 'package.json'), 'utf8')).dependencies,
      devDependencies: JSON.parse(readFileSync(join(source, 'package.json'), 'utf8'))
        .devDependencies,
    }),
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
        allowJs: true,
        incremental: true,
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
    // Match the qualified Next declaration writer before immutable build input
    // capture. This scaffold must work in a fresh checkout with no prior build.
    '/// <reference types="next" />\n' +
      '/// <reference types="next/image-types/global" />\n' +
      '/// <reference path="./.next/types/routes.d.ts" />\n\n' +
      '// NOTE: This file should not be edited\n' +
      '// see https://nextjs.org/docs/app/api-reference/config/typescript for more information.\n',
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
    `import { optionalPreparedPage } from '@/server/auth/page';
export const dynamic = 'force-dynamic'; export const revalidate = 0;
export default async function Page() { const a = await optionalPreparedPage(); const b = await optionalPreparedPage(); a?.assertActive(); b?.assertActive(); if (a?.snapshot !== b?.snapshot) throw new Error('request snapshot identity'); const gate = Reflect.get(globalThis, Symbol.for('learnstack.proof-render-gate')); if (a && typeof gate === 'function') await gate(); return <main><h1>{a?.snapshot.session.state ?? 'anonymous'}</h1><p>{a?.snapshot.session.displayName}</p></main>; }`,
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
  if (uiMode && !external) {
    // The observation exists only in this copied fixture, after the UI source copy.
    const path = join(app, 'src/server/auth/request-runtime.ts');
    file(path, instrumentBffProofRequestRuntime(readFileSync(path, 'utf8')));
  }
  if (mutation === 'history') {
    const history = join(app, 'src/components/auth/history.ts');
    file(history, replace(readFileSync(history, 'utf8'), 'location.reload()', 'void 0'));
  }
  let launcher = readFileSync(join(source, 'scripts/public-server-runtime.mjs'), 'utf8');
  if (mutation === 'diagnostics')
    launcher = replace(
      launcher,
      "  process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';",
      '',
    );
  if (mutation === 'shutdown')
    launcher = replace(launcher, 'process.exit(failed ? 1 : 0)', 'process.exit(1)');
  if (!external) {
    launcher = replace(
      launcher,
      "import { BffConfigurationError, bffConfiguration } from '../.server/auth/configuration.js';",
      "import { BffConfigurationError, bffConfiguration } from '../.server/auth/configuration.js';\nimport { randomBytes } from 'node:crypto';",
    );
    launcher = replace(
      launcher,
      `    const dependencies = {
      provider: createTenantProvider(bff, globalThis.fetch),
      account: createAccountSiteCaller(nativeConfiguration, globalThis.fetch),
      native: createNativeSessionApi(nativeConfiguration, globalThis.fetch),
      administration: createNativeAdministrationCaller(nativeConfiguration, globalThis.fetch),
      diagnostic: (reason) => console.warn('BFF session lifecycle', { reason }),
    };`,
      `    let now = 0; let refreshes = 0; let accounts = 0; const administrations = {};
    let renderArmed = false; const renderWork = new Set();
    const releaseRender = () => { renderArmed = false; for (const release of renderWork) release(); };
    Object.defineProperty(globalThis, Symbol.for('learnstack.proof-render-gate'), {value: () => {
      if (!renderArmed) return;
      renderArmed = false;
      const framework = globalThis[Symbol.for('@next/request-context')]?.get();
      if (!framework || typeof framework.waitUntil !== 'function') throw new Error('Owned framework context absent');
      let release; const work = new Promise(resolve => { release = resolve; });
      renderWork.add(release); framework.waitUntil(work);
      return work.finally(() => renderWork.delete(release));
    }});
    for (const event of ['disconnect', 'SIGINT', 'SIGTERM', 'exit']) process.once(event, releaseRender);
    process.on('message', value => { if (value?.kind === 'render-control' && ['arm', 'release', 'status'].includes(value.action)) {
      if (value.action === 'arm') { if (renderArmed || renderWork.size) throw new Error('Owned render already held'); renderArmed = true; }
      if (value.action === 'release') releaseRender();
      process.send?.({kind: 'render-state', held: renderWork.size, frames: bffRequests?.counts() ?? 0, snapshots: bffRequests?.snapshotCounts() ?? null});
    }});
    process.on('message', async value => { if (value && value.kind === 'advance' && [0, 60000, 901000].includes(value.milliseconds)) { now += value.milliseconds; await (await import('next/dist/trace/index.js')).flushAllTraces(); process.send?.({ kind: 'snapshot', now, refreshes, accounts, administrations: {...administrations} }); } });
    const credentials = () => ({ accessToken: '${marker}', refreshToken: '${marker}', idToken: '${marker}', identity: { issuer: ${JSON.stringify(fakeIssuer)}, subject: 'owned-subject', actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayLabel: 'Fixture account' }, accessExpiresAt: now + 900000, refreshExpiresAt: now + 3600000 });
    const member = {membershipId: '019a154b-0000-7000-8000-000000000012', userId: '019a154b-0000-7000-8000-000000000011', organizationId: null, status: 'Active', displayName: 'Fixture member', preferredLocale: null, roleIds: ['019a154b-0000-7000-8000-000000000013'], version: '0'};
    const role = {roleId: '019a154b-0000-7000-8000-000000000013', builtInKey: 'TenantAdmin', capabilityScope: 'Tenant', version: '0', grants: [{scope: 'Tenant', key: 'identity.membership.admin'}], adoptedReleaseVersion: 1, adoptedReleaseFingerprint: 'a'.repeat(64)};
    const ownProfile = {displayName: 'Fixture member', preferredLocale: ${disabledOwnPreference ? "'fr'" : 'null'}, version: '0'};
    const pageInfo = {nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false};
    const success = (data, version = false) => ({kind: 'success', status: 200, data: structuredClone(data), etag: version ? '"' + data.version + '"' : null, location: null, replayed: false});
    const fixtureAdministration = async (_token, binding, operation) => {
      administrations[operation.kind] = (administrations[operation.kind] ?? 0) + 1;
      switch (operation.kind) {
        case 'getOwnCapabilities': return success({canReadTenantMembers: true, canReadOrganizationMembers: true, canWriteMemberProfiles: true, canManageMemberships: true, canReadRoles: true, canChangeRoleGrants: true, canAdoptRoleRelease: true, canWriteOwnProfile: true, canManageOrganizations: true});
        case 'getOwnProfile': return success(ownProfile, true);
        case 'listMemberships':
        case 'listOrganizationMemberships': { if (${mutation === 'unavailable-pages'}) return {kind: 'transport-error'}; const {roleIds, version, ...item} = member; return success({items: [item], pageInfo}); }
        case 'getMembership': return operation.id === member.membershipId ? success(member, true) : {kind: 'invalid-request'};
        case 'listRoles': { const {grants, adoptedReleaseVersion, adoptedReleaseFingerprint, ...item} = role; return success({items: [item]}); }
        case 'getRole': return operation.id === role.roleId ? success(role, true) : {kind: 'invalid-request'};
        case 'getPermissions': return success({items: [{scope: 'Tenant', key: 'identity.membership.admin', labelKey: 'lockey_permission_identity_membership_admin', canGrant: true}], release: {version: 1, fingerprint: 'a'.repeat(64), tenantAdminDelta: []}});
        case 'getOwnContexts': return success({items: [{tenantId: binding.tenantId, organizationId: binding.organizationId, tenantName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName, organizationName: null, entryOrigin: 'https://' + binding.host, canUseCurrentHost: true}], pageInfo});
        case 'setOwnProfile': Object.assign(ownProfile, operation.profile); ownProfile.version = String(BigInt(ownProfile.version) + 1n); return success({version: ownProfile.version}, true);
        case 'setMembershipProfile': if (operation.id !== member.membershipId) break; Object.assign(member, operation.profile); member.version = String(BigInt(member.version) + 1n); return success({version: member.version}, true);
        case 'replaceMembershipRoles': if (operation.id !== member.membershipId) break; member.roleIds = [...operation.roleIds]; member.version = String(BigInt(member.version) + 1n); return success({version: member.version}, true);
        case 'changeMembershipStatus': if (operation.id !== member.membershipId) break; member.status = operation.status; member.version = String(BigInt(member.version) + 1n); return success({version: member.version}, true);
        case 'changeRoleGrants': if (operation.id !== role.roleId) break; role.grants = [...operation.grants]; role.version = String(BigInt(role.version) + 1n); return success({version: role.version}, true);
        case 'adoptRoleManifest': if (operation.id !== role.roleId) break; role.adoptedReleaseVersion = operation.manifestVersion; role.adoptedReleaseFingerprint = operation.manifestFingerprint; role.version = String(BigInt(role.version) + 1n); return success({version: role.version}, true);
      }
      return {kind: 'invalid-request'};
    };
    const dependencies = { provider: { begin: async callbackUri => ({ transaction: { callbackUri, state: randomBytes(32).toString('base64url'), nonce: randomBytes(32).toString('base64url'), verifier: randomBytes(32).toString('base64url') }, redirectUrl: ${JSON.stringify(fakeIssuer + '/protocol/openid-connect/auth')} }), redeem: async () => credentials(), refresh: async () => { ++refreshes; return credentials(); }, logoutUrl: () => ${JSON.stringify(fakeLogout)} }, account: async (_token, binding) => { ++accounts; return ({ actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName }); }, native: { self: async () => ({state: 'active', association: {authorityId: '10000000-0000-4000-8000-000000000099', subjectKey: 'a'.repeat(64), actorId: '019a154b-0000-7000-8000-000000000011'}}), bootstrap: async () => {throw new Error('Unused fixture create');}, contexts: async (_token,binding) => ({items: [{tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, tenantName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName, organizationName: null, entryOrigin: 'https://' + binding.host, canUseCurrentHost: true}], endCursor: null, hasNextPage: false}), admit: async (_token,binding) => ({authorityId: '10000000-0000-4000-8000-000000000099', subjectKey: 'a'.repeat(64), actorId: '019a154b-0000-7000-8000-000000000011', tenantId: binding.tenantId, organizationId: binding.organizationId}), verifyAssociation: () => {} }, administration: fixtureAdministration, clock: { monotonic: () => now, utc: () => ${clockBase} + now } };`,
    );
  }
  if (uiMode && !external) {
    // Diagnostic-only instrumentation in the private copied launcher. Observe the
    // original promises, bounds and exits without acquiring shutdown authority.
    launcher = replace(
      launcher,
      '  let stopping = false;',
      `  const proofShutdown = {kind: 'proof-shutdown', entered: false, watchdog: false,
      listener: 'pending', next: 'pending', sessions: 'pending', requests: 'pending'};
  const reportShutdown = (milestone) => {
    try {
      let sample = null;
      try { sample = bffRequests?.proofCounts() ?? null; } catch {}
      if (process.connected) process.send({...proofShutdown, milestone, sample}, () => {});
    } catch {}
  };
  const observeShutdownJoin = (key, promise) => {
    void Promise.resolve(promise).then(
      () => { proofShutdown[key] = 'fulfilled'; reportShutdown(key); },
      () => { proofShutdown[key] = 'rejected'; reportShutdown(key); },
    );
    return promise;
  };
  let stopping = false;`,
    );
    launcher = replace(
      launcher,
      '    stopping = true;',
      "    stopping = true; proofShutdown.entered = true; reportShutdown('entered');",
    );
    launcher = replace(
      launcher,
      '    const requestsStopped = bffRequests?.stop();',
      "    const requestsStopped = observeShutdownJoin('requests', bffRequests?.stop());",
    );
    launcher = replace(
      launcher,
      '    const sessionsStopped = bffSessions?.stop();',
      "    const sessionsStopped = observeShutdownJoin('sessions', bffSessions?.stop());",
    );
    launcher = replace(
      launcher,
      "    const deadline = setTimeout(() => {\n      console.error('Public server shutdown failed');",
      "    const deadline = setTimeout(() => {\n      proofShutdown.watchdog = true; reportShutdown('watchdog');\n      console.error('Public server shutdown failed');",
    );
    launcher = replace(
      launcher,
      '    server.closeAllConnections();',
      "    observeShutdownJoin('listener', closed);\n    server.closeAllConnections();",
    );
    launcher = replace(
      launcher,
      '      app.close(),',
      "      observeShutdownJoin('next', app.close()),",
    );
  }
  if (isolatedProtocol) {
    // Only the physical test listener changes. Logical Host/origin, ingress,
    // Next's configured port, TLS and all production admission remain intact.
    launcher = replace(
      launcher,
      "server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {",
      "server.listen(0, '127.0.0.1', () => { const address = server.address(); if (!address || typeof address === 'string') throw new Error('Owned listener address absent'); process.send({kind: 'proof-listener', port: address.port});",
    );
  }
  file(join(app, 'scripts/public-server-runtime.mjs'), launcher);
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
      LEARNSTACK_BFF_ISSUER: external?.issuer ?? fakeIssuer,
      KEYCLOAK_WEB_CLIENT_SECRET: external?.clientSecret ?? marker,
      Identity__AuthorityId: external?.authorityId ?? '10000000-0000-4000-8000-000000000099',
      Identity__NativeSession__CurrentSecret: external?.nativeSourceSecret ?? marker,
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
  mark('build');
  const build = child(
    mutation === 'held-build'
      ? ['-e', 'setInterval(() => {}, 1000)']
      : frameworkProofBuildArguments(app),
    env,
  );
  if (mutation === 'held-build') mark('held-build'); // The owned child exists before the parent may close input.
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
  // The guarded producer compiled the actual resolved copied SDK/native graph.
  if (isolatedProtocol) {
    const require = createRequire(join(app, 'package.json'));
    const sdkRoot = join(root, 'frontend/packages/sdk');
    for (const [resolved, target] of [
      [require.resolve('@learnstack/sdk/server'), 'src/server.ts'],
      [require.resolve('@learnstack/sdk/authenticated-server'), 'dist/authenticated-server.js'],
      [require.resolve('@learnstack/sdk/administration-server'), 'dist/administration-server.js'],
    ])
      assert.equal(
        realpathSync(resolved),
        realpathSync(join(sdkRoot, target)),
        'current owned SDK resolution',
      );
  }
  const buildTrace = devMode ? readFileSync(join(app, '.next/trace'), 'utf8') : '';
  if (devMode)
    assert.ok(buildTrace.length > 0, 'separate build leaves a nonempty real collector control');
  mark('start');
  const server = child(
    ['scripts/public-server.mjs', ...(devMode ? ['--dev'] : [])],
    devMode ? { ...env, NODE_ENV: 'development' } : env,
    true,
  );
  if (uiMode && !external) server.shutdown = observeBffProofShutdown(server.process);
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
  const renderControl = (action) =>
    new Promise((resolve, reject) => {
      assert.ok(['arm', 'release', 'status'].includes(action));
      const timer = setTimeout(() => reject(new Error('owned render IPC deadline')), 5000);
      server.process.once('message', (state) => {
        clearTimeout(timer);
        if (state?.kind !== 'render-state') reject(new Error('owned render IPC shape'));
        else resolve(state);
      });
      server.process.send({ kind: 'render-control', action });
    });
  if (isolatedProtocol) {
    try {
      listenerPort = await waitForBffProofListener(server.process);
    } catch (error) {
      const detail = bffListenerFailureDetail(error);
      if (detail)
        process.stderr.write('BFF listener startup observation: ' + JSON.stringify(detail) + '\n');
      throw error;
    }
  } else {
    const until = Date.now() + 20_000;
    while (!server.scanner.tail.includes('Public HTTPS listener ready on 127.0.0.1:3000')) {
      assert.equal(server.process.exitCode, null, 'native listener alive');
      assert.ok(Date.now() < until, 'listener startup deadline');
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
  }
  return { app, server, advance, renderControl, get, scanners, cookieCanaries };
}
export const jar = (response) =>
  (response.headers['set-cookie'] ?? [])
    .map((value) => value.split(';', 1)[0])
    .filter((value) => !value.endsWith('='))
    .join('; ');
