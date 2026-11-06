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
import { installCopiedExternalProviderCa } from './framework-external-provider-ca-source.mjs';
import { instrumentCopiedOverlap } from './framework-overlap-proof-source.mjs';
import { instrumentFrameworkOwnershipMatrix } from './framework-ownership-matrix-source.mjs';
import { instrumentFrameworkOwnershipProof } from './framework-ownership-proof-source.mjs';
import { createCopiedRuntimeHostBroker } from './framework-proof-host-broker.mjs';
import {
  frameworkProofBuildArguments,
  prepareFrameworkProof,
  reuseFrameworkProof,
} from './framework-proof-support.mjs';
import { instrumentCopiedRouterFaults } from './framework-router-fault-source.mjs';
import {
  copiedRouterMatrixRouteSource,
  instrumentCopiedRouterMatrix,
} from './framework-router-matrix-source.mjs';
import {
  instrumentCopiedTransport,
  instrumentCopiedTransportMiddleware,
} from './framework-transport-proof-source.mjs';
import { verifyFrameworkInstallation } from '../../../scripts/framework-installation.mjs';
const source = fileURLToPath(new URL('../', import.meta.url));
// Identity/lifetime metadata only. Every copy still authenticates both complete graphs.
const matrixBuilds = new WeakMap();
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
  profile = 'none',
}) {
  owner.assertActive();
  const runtime = args[0] === 'scripts/public-server.mjs';
  // This helper alone mints the parent identity for each detached POSIX group.
  // The watcher is confined to that live group leader, never an arbitrary PID.
  const useLifeline = globalThis.process.platform !== 'win32' && !runtime;
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
  const broker = runtime ? createCopiedRuntimeHostBroker(process, { app, profile }) : null;
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
    broker,
    proofChannel: broker?.proof ?? process,
    scanner,
    completed: new Promise((resolve) =>
      process.once('close', (code, signal) => resolve({ code, signal })),
    ),
  };
}
export function instrumentCopiedBffRuntime(
  sourceText,
  {
    mutation,
    external,
    uiMode,
    isolatedProtocol,
    randomListener,
    disabledOwnPreference,
    fakeIssuer,
    fakeLogout,
    sites,
    marker,
    clockBase,
    ownershipCase,
  },
) {
  let launcher = sourceText;
  const matrix = ownershipCase === 'matrix';
  if (matrix)
    assert.ok(
      isolatedProtocol && !uiMode && !external && !mutation,
      'Matrix credentials require the isolated ownership fixture',
    );
  // The existing private configuration retains each fixture's distinct marker.
  // Keep it out of copied matrix source so equivalent inputs can share a verified build.
  const token = matrix ? 'bff.clientSecret' : `'${marker}'`;
  const heldNativeStop = ['held-native-stop', 'watchdog-observation'].includes(mutation);
  assert.ok(!heldNativeStop || (uiMode && !external), 'Held native stop requires copied BFF UI');
  if (mutation === 'diagnostics')
    launcher = replace(
      launcher,
      "    process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';",
      '',
    );
  if (mutation === 'shutdown')
    launcher = replace(launcher, '  let failed = false;', '  let failed = true;');
  if (!external) {
    launcher = replace(
      launcher,
      "import { readFileSync } from 'node:fs';",
      "import { randomBytes } from 'node:crypto';\nimport { readFileSync } from 'node:fs';",
    );
    launcher = replace(
      launcher,
      '  let stopping = false;',
      '  let releaseProofRender = () => {};\n  let stopping = false;',
    );
    launcher = replace(
      launcher,
      `      const provider = createTenantProvider(bff, globalThis.fetch);`,
      `    let now = 0; let refreshes = 0; let accounts = 0; const administrations = {};
    let renderArmed = false; const renderWork = new Set();
    const releaseRender = () => { renderArmed = false; for (const release of renderWork) release(); };
    releaseProofRender = releaseRender;
    Object.defineProperty(globalThis, Symbol.for('learnstack.proof-render-gate'), {value: () => {
      if (!renderArmed) return;
      renderArmed = false;
      const framework = globalThis[Symbol.for('@next/request-context')]?.get();
      if (!framework || typeof framework.waitUntil !== 'function') throw new Error('Owned framework context absent');
      let release; const work = new Promise(resolve => { release = resolve; });
      renderWork.add(release); framework.waitUntil(work);
      return work.finally(() => renderWork.delete(release));
    }});
    ${heldNativeStop ? '' : "for (const event of ['disconnect', 'SIGINT', 'SIGTERM', 'exit']) process.once(event, releaseRender);"}
    control.proof.onCommand(async value => { if (value.kind === 'render-control') {
      if (value.action === 'arm') { if (renderArmed || renderWork.size) throw new Error('Owned render already held'); renderArmed = true; }
      if (value.action === 'release') releaseRender();
      control.proof.emit({kind: 'render-state', held: renderWork.size, frames: bffRequests?.counts() ?? 0, snapshots: bffRequests?.snapshotCounts() ?? null});
    } else if (value.kind === 'advance') {
      now += value.milliseconds;
      await (await import('next/dist/trace/index.js')).flushAllTraces();
      control.proof.emit({ kind: 'snapshot', now, refreshes, accounts, administrations: {...administrations} });
    } });
    const credentials = () => ({ accessToken: ${token}, refreshToken: ${token}, idToken: ${token}, identity: { issuer: ${JSON.stringify(fakeIssuer)}, subject: 'owned-subject', actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayLabel: 'Fixture account' }, accessExpiresAt: now + 900000, refreshExpiresAt: now + 3600000 });
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
    const fixtureDependencies = { provider: { begin: async callbackUri => ({ transaction: { callbackUri, state: randomBytes(32).toString('base64url'), nonce: randomBytes(32).toString('base64url'), verifier: randomBytes(32).toString('base64url') }, redirectUrl: ${JSON.stringify(fakeIssuer + '/protocol/openid-connect/auth')} }), redeem: async () => credentials(), refresh: async () => { ++refreshes; return credentials(); }, logoutUrl: () => ${JSON.stringify(fakeLogout)} }, account: async (_token, binding) => { ++accounts; return ({ actorId: '019a154b-0000-7000-8000-000000000011', tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, displayName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName }); }, native: { self: async () => ({state: 'active', association: {authorityId: '10000000-0000-4000-8000-000000000099', subjectKey: 'a'.repeat(64), actorId: '019a154b-0000-7000-8000-000000000011'}}), bootstrap: async () => {throw new Error('Unused fixture create');}, contexts: async (_token,binding) => ({items: [{tenantId: '10000000-0000-4000-8000-000000000001', organizationId: null, tenantName: (${JSON.stringify(sites)}).find(site => site.host === binding.host).displayName, organizationName: null, entryOrigin: 'https://' + binding.host, canUseCurrentHost: true}], endCursor: null, hasNextPage: false}), admit: async (_token,binding) => ({authorityId: '10000000-0000-4000-8000-000000000099', subjectKey: 'a'.repeat(64), actorId: '019a154b-0000-7000-8000-000000000011', tenantId: binding.tenantId, organizationId: binding.organizationId}), verifyAssociation: () => {} }, administration: fixtureAdministration, clock: { monotonic: () => now, utc: () => ${clockBase} + now } };
    const provider = fixtureDependencies.provider;`,
    );
    for (const [name, original] of [
      ['account', 'createAccountSiteCaller(nativeConfiguration, globalThis.fetch)'],
      ['native', 'createNativeSessionApi(nativeConfiguration, globalThis.fetch)'],
    ])
      launcher = replace(
        launcher,
        `      const ${name} = ${original};`,
        `      const ${name} = fixtureDependencies.${name};`,
      );
    launcher = replace(
      launcher,
      `      const administration = createNativeAdministrationCaller(
        nativeConfiguration,
        globalThis.fetch,
      );`,
      '      const administration = fixtureDependencies.administration;',
    );
    launcher = replace(
      launcher,
      "        diagnostic: (reason) => console.warn('BFF session lifecycle', { reason }),",
      "        diagnostic: (reason) => console.warn('BFF session lifecycle', { reason }),\n        clock: fixtureDependencies.clock,",
    );
    if (!heldNativeStop)
      launcher = replace(
        launcher,
        '      ownShutdown(() => admission.shutdown());',
        '      ownShutdown(() => admission.shutdown());\n      releaseProofRender();',
      );
  }
  if (uiMode && !external) {
    // Observe real native shutdown promises in this copied launcher only.
    launcher = replace(
      launcher,
      '  let stopping = false;',
      `  const proofShutdown = {kind: 'proof-shutdown', entered: false, watchdog: false,
      listener: 'pending', next: 'pending', sessions: 'pending', requests: 'pending'};
  const reportShutdown = (milestone) => {
    let sample = [0, 0, 0, 0, 0];
    try { sample = bffRequests?.proofCounts() ?? sample; } catch { control.requestStop('protocol-fault'); }
    try { control.proof.emit({...proofShutdown, milestone, sample}); }
    catch { control.requestStop('protocol-fault'); }
  };
  ${
    mutation === 'watchdog-observation'
      ? ''
      : `control.proof.onWatchdog(() => {
    proofShutdown.watchdog = true;
    reportShutdown('watchdog');
  });`
  }
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
      '    stopping = true;\n    shutdownPromise = Promise.resolve().then(async () => {',
      `    stopping = true;
    proofShutdown.entered = true; reportShutdown('entered');
    shutdownPromise = Promise.resolve().then(async () => {`,
    );
    launcher = replace(
      launcher,
      '      ownShutdown(() => bffRequests.stop());',
      "      ownShutdown(() => observeShutdownJoin('requests', bffRequests.stop()));",
    );
    launcher = replace(
      launcher,
      '      ownShutdown(() => bffSessions.stop());',
      "      ownShutdown(() => observeShutdownJoin('sessions', bffSessions.stop()));",
    );
    launcher = replace(
      launcher,
      'new Promise((resolve, reject) =>\n              server.close((error) => (error ? reject(error) : resolve())),\n            ),',
      "observeShutdownJoin('listener', new Promise((resolve, reject) =>\n              server.close((error) => (error ? reject(error) : resolve())),\n            )),",
    );
    launcher = replace(
      launcher,
      'if (app) ownShutdown(() => app.close());',
      "if (app) ownShutdown(() => observeShutdownJoin('next', app.close()));",
    );
  }
  if (isolatedProtocol || randomListener) {
    // Only the physical test listener changes. Logical Host/origin, ingress,
    // Next's configured port, TLS and all production admission remain intact.
    launcher = replace(
      launcher,
      "server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {",
      "server.listen(0, '127.0.0.1', () => {",
    );
    launcher = replace(
      launcher,
      'address.port !== PUBLIC_HTTPS_PORT',
      '!Number.isInteger(address.port) || address.port < 1 || address.port > 65535',
    );
    launcher = replace(
      launcher,
      "if (!ready) throw new Error('Public HTTPS listener failed');",
      "if (!ready) throw new Error('Public HTTPS listener failed');\n              control.proof.emit({ kind: 'proof-listener', port: address.port });",
    );
  }
  return launcher;
}

export async function prepareBffProof({
  owner,
  root,
  privateValues,
  secret,
  marker,
  devMode = false,
  uiMode = false,
  disabledOwnPreference = false,
  mutation,
  ownershipCase,
  transportProof = false,
  routerMatrix = false,
  routerFaults = false,
  overlapProof = false,
  clockBase = 1800000000000,
  external,
  isolatedProtocol = false,
  randomListener = false,
  frameworkTemplate,
  fakeIssuer = 'https://identity.example.test/realms/learnstack',
  fakeLogout = fakeIssuer + '/protocol/openid-connect/logout',
  mark = () => {},
  sites = [
    { host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' },
    { host: 'other.example.test:3000', displayName: 'Diğer kurum', locale: 'tr' },
  ],
}) {
  assert.ok(!isolatedProtocol || !external, 'isolated protocol excludes external capabilities');
  assert.equal(typeof randomListener, 'boolean');
  assert.ok(
    !randomListener || (external && uiMode),
    'random external listener requires real provider UI fixture',
  );
  assert.equal(typeof transportProof, 'boolean', 'closed transport fixture selection');
  assert.equal(typeof routerMatrix, 'boolean', 'closed router matrix fixture selection');
  assert.equal(typeof routerFaults, 'boolean', 'closed router fault fixture selection');
  if (routerFaults) assert.equal(transportProof, true, 'router faults require copied transport');
  assert.equal(typeof overlapProof, 'boolean', 'closed overlap fixture selection');
  if (routerMatrix) assert.equal(transportProof, true, 'router matrix requires copied transport');
  if (overlapProof) assert.equal(transportProof, true, 'overlap requires copied transport');
  assert.ok(
    Number(routerMatrix) + Number(routerFaults) + Number(overlapProof) <= 1,
    'copied proof variants are mutually exclusive',
  );
  if (transportProof)
    assert.ok(
      uiMode &&
        isolatedProtocol &&
        !devMode &&
        !external &&
        !mutation &&
        ownershipCase === undefined,
      'isolated production UI transport fixture',
    );
  if (ownershipCase !== undefined) {
    assert.ok(
      ['prepare-sigterm', 'worker-positive', 'matrix'].includes(ownershipCase),
      'closed ownership case',
    );
    assert.ok(isolatedProtocol && !uiMode && !external && !mutation, 'isolated ownership fixture');
    if (ownershipCase !== 'matrix')
      assert.equal(devMode, ownershipCase === 'worker-positive', 'ownership mode');
  }
  function templateApp() {
    if (frameworkTemplate === undefined) return undefined;
    assert.equal(ownershipCase, 'matrix', 'Only isolated matrix fixtures copy templates');
    const template = matrixBuilds.get(frameworkTemplate);
    assert.ok(template && template.owner === owner, 'Matrix template must share its owner');
    owner.assertActive();
    assert.equal(template.launched(), false, 'Matrix template must never have launched');
    return template.app;
  }
  templateApp(); // Refuse forged/cross-owner/started templates before creating any resource.
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
      profile: external && !randomListener ? 'none' : 'bff',
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
    'native-invitation-contract',
    'native-invitation-client',
    'native-invitation-http',
    'invitation-session',
    'invitation-copy',
    'invitation-html',
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
  // The native launcher imports these exact authored assets before Next starts.
  for (const locale of ['en', 'tr']) {
    const directory = join(app, 'src/i18n/messages', locale);
    mkdirSync(directory, { recursive: true });
    cpSync(
      join(source, 'src/i18n/messages', locale, 'invitation.json'),
      join(directory, 'invitation.json'),
    );
  }
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
  prepareFrameworkProof({
    sourceApp: source,
    app,
    profile: external && !randomListener ? 'none' : 'bff',
  });
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
  if ((uiMode || ownershipCase === 'matrix') && !external) {
    // The observation exists only in this copied fixture, after any UI source copy.
    const path = join(app, 'src/server/auth/request-runtime.ts');
    file(path, instrumentBffProofRequestRuntime(readFileSync(path, 'utf8')));
  }
  if (['held-native-stop', 'watchdog-observation'].includes(mutation)) {
    // Hold actual App Router work registered with the native request owner.
    // The ordinary copied UI keeps its existing shutdown release behavior.
    assert.ok(uiMode && !external, 'Held native stop requires copied BFF UI');
    const path = join(app, 'src/app/(studio)/studio/page.tsx');
    let page = readFileSync(path, 'utf8');
    page = "import { preparedPage } from '@/server/auth/page';\n" + page;
    page = replace(
      page,
      'export default function StudioHomePage() {',
      `export default async function StudioHomePage() {
  const page = await preparedPage();
  page.assertActive();
  const gate = Reflect.get(globalThis, Symbol.for('learnstack.proof-render-gate'));
  if (typeof gate === 'function') await gate();`,
    );
    file(path, page);
  }
  if (mutation === 'history') {
    const history = join(app, 'src/components/auth/history.ts');
    file(history, replace(readFileSync(history, 'utf8'), 'location.reload()', 'void 0'));
  }
  file(
    join(app, 'scripts/public-server-runtime.mjs'),
    instrumentCopiedBffRuntime(
      readFileSync(join(source, 'scripts/public-server-runtime.mjs'), 'utf8'),
      {
        mutation,
        external,
        uiMode,
        isolatedProtocol,
        randomListener,
        disabledOwnPreference,
        fakeIssuer,
        fakeLogout,
        sites,
        marker,
        clockBase,
        ownershipCase,
      },
    ),
  );
  if (transportProof) {
    const middleware = join(app, 'src/middleware.ts');
    file(middleware, instrumentCopiedTransportMiddleware(readFileSync(middleware, 'utf8')));
    const path = join(app, 'scripts/public-server-runtime.mjs');
    const copied = instrumentCopiedTransport({
      source: readFileSync(path, 'utf8'),
      identity: verifyFrameworkInstallation({ appRoot: app }),
    });
    file(path, copied.runtime);
    file(join(app, 'scripts/framework-proof-transport-observer.mjs'), copied.observer);
  }
  if (overlapProof) {
    const runtime = join(app, 'scripts/public-server-runtime.mjs');
    const requestRuntime = join(app, 'src/server/auth/request-runtime.ts');
    const snapshotPool = join(app, 'src/server/auth/page-snapshot-pool.ts');
    const sessionStore = join(app, 'src/server/auth/session-store.ts');
    const page = join(app, 'src/app/(studio)/studio/page.tsx');
    const observer = join(app, 'scripts/framework-proof-transport-observer.mjs');
    const copied = instrumentCopiedOverlap({
      runtime: readFileSync(runtime, 'utf8'),
      requestRuntime: readFileSync(requestRuntime, 'utf8'),
      snapshotPool: readFileSync(snapshotPool, 'utf8'),
      sessionStore: readFileSync(sessionStore, 'utf8'),
      page: readFileSync(page, 'utf8'),
      observer: readFileSync(observer, 'utf8'),
    });
    file(runtime, copied.runtime);
    file(requestRuntime, copied.requestRuntime);
    file(snapshotPool, copied.snapshotPool);
    file(sessionStore, copied.sessionStore);
    file(page, copied.page);
    file(observer, copied.observer);
    file(join(app, 'scripts/framework-proof-overlap.mjs'), copied.coordinator);
  }
  if (routerMatrix || routerFaults) {
    file(
      join(app, 'src/app/(public)/en/courses/transport/route.ts'),
      copiedRouterMatrixRouteSource,
    );
    const runtimePath = join(app, 'scripts/public-server-runtime.mjs');
    const observerPath = join(app, 'scripts/framework-proof-transport-observer.mjs');
    for (const name of ['framework-router-generated-pipes.mjs', 'framework-router-pipe-ledger.mjs'])
      file(join(app, 'scripts', name), readFileSync(join(source, 'scripts', name), 'utf8'));
    const copied = instrumentCopiedRouterMatrix({
      runtime: readFileSync(runtimePath, 'utf8'),
      observer: readFileSync(observerPath, 'utf8'),
      identity: verifyFrameworkInstallation({ appRoot: app }),
    });
    file(runtimePath, copied.runtime);
    file(observerPath, copied.observer);
    if (routerFaults) {
      const requestPath = join(app, 'src/server/auth/request-runtime.ts');
      const ledgerPath = join(app, 'scripts/framework-router-pipe-ledger.mjs');
      const faults = instrumentCopiedRouterFaults({
        runtime: copied.runtime,
        observer: copied.observer,
        owner: readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8'),
        ledger: readFileSync(ledgerPath, 'utf8'),
        requestRuntime: readFileSync(requestPath, 'utf8'),
      });
      file(runtimePath, faults.runtime);
      file(join(app, 'scripts/public-server.mjs'), faults.owner);
      file(observerPath, faults.observer);
      file(ledgerPath, faults.ledger);
      file(requestPath, faults.requestRuntime);
      file(join(app, 'src/app/(public)/en/courses/transport/route.ts'), faults.route);
      for (const name of ['gate', 'runtime', 'join-points']) {
        const moduleName = 'framework-router-fault-' + name + '.mjs';
        file(
          join(app, 'scripts', moduleName),
          readFileSync(join(source, 'scripts', moduleName), 'utf8'),
        );
      }
    }
  }
  if (ownershipCase !== undefined) {
    const inputs = {
      ownershipCase,
      configuration: readFileSync(join(app, 'next.config.ts'), 'utf8'),
      runtime: readFileSync(join(app, 'scripts/public-server-runtime.mjs'), 'utf8'),
    };
    const copied =
      ownershipCase === 'matrix'
        ? instrumentFrameworkOwnershipMatrix({
            ...inputs,
            child: readFileSync(join(app, 'scripts/public-runtime-child.mjs'), 'utf8'),
            owner: readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8'),
            rootPage: readFileSync(join(app, 'src/app/page.tsx'), 'utf8'),
          })
        : instrumentFrameworkOwnershipProof(inputs);
    file(join(app, 'next.config.ts'), copied.configuration);
    file(join(app, 'scripts/public-server-runtime.mjs'), copied.runtime);
    if (ownershipCase === 'matrix') {
      file(join(app, 'scripts/public-runtime-child.mjs'), copied.child);
      file(join(app, 'scripts/public-server.mjs'), copied.owner);
      file(join(app, 'src/app/page.tsx'), copied.rootPage);
    }
    if (copied.page) file(join(app, 'src/app/en/courses/[slug]/page.tsx'), copied.page);
  }
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
    const trust = installCopiedExternalProviderCa({
      root,
      app,
      authority: external.authority,
      randomListener,
    });
    env.NODE_EXTRA_CA_CERTS = trust.caPath;
  }
  // Compile the copied current sources: never let stale workstation output
  // qualify the native side while Next compiles different bundled sources.
  cpSync(join(source, 'tsconfig.server.json'), join(app, 'tsconfig.server.json'));
  async function buildFixture() {
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
  }
  const template = templateApp();
  // An invalid source throws; only a valid source with different target inputs falls back.
  const reused = template !== undefined && reuseFrameworkProof({ app, templateApp: template });
  if (!reused) await buildFixture();
  // Fresh production and verified copies bind the actual isolated SDK/native graph.
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
  let launched = false;
  const prepared = Object.freeze({
    preparation: reused ? 'verified-copy' : 'compiled',
    ...(ownershipCase === 'matrix' ? { rebuild: buildFixture } : {}),
    launch(mode) {
      owner.assertActive();
      if (ownershipCase !== 'matrix') {
        assert.equal(mode, undefined, 'Ordinary fixture mode is fixed');
        assert.equal(launched, false, 'Prepared fixture launches once');
      } else assert.ok(['production', 'development'].includes(mode), 'Closed matrix launch mode');
      const development = ownershipCase === 'matrix' ? mode === 'development' : devMode;
      launched = true;
      mark('start');
      const server = child(
        ['scripts/public-server.mjs', ...(development ? ['--dev'] : [])],
        development ? { ...env, NODE_ENV: 'development' } : env,
        true,
      );
      if (uiMode && !external) server.shutdown = observeBffProofShutdown(server.proofChannel);
      const advance = (milliseconds) =>
        new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error('owned IPC deadline')), 5000);
          server.proofChannel.once('message', (snapshot) => {
            clearTimeout(timer);
            if (snapshot?.kind !== 'snapshot') reject(new Error('owned IPC shape'));
            else resolve(snapshot);
          });
          server.broker.send({ kind: 'advance', milliseconds });
        });
      const renderControl = (action) =>
        new Promise((resolve, reject) => {
          assert.ok(['arm', 'release', 'status'].includes(action));
          const timer = setTimeout(() => reject(new Error('owned render IPC deadline')), 5000);
          server.proofChannel.once('message', (state) => {
            clearTimeout(timer);
            if (state?.kind !== 'render-state') reject(new Error('owned render IPC shape'));
            else resolve(state);
          });
          server.broker.send({ kind: 'render-control', action });
        });
      const view = {
        app,
        server,
        advance,
        renderControl,
        get,
        scanners,
        cookieCanaries,
        listenerPort: () => listenerPort,
      };
      const ready = (async () => {
        if (isolatedProtocol || randomListener) {
          try {
            listenerPort = await waitForBffProofListener(server.proofChannel);
          } catch (error) {
            const detail = bffListenerFailureDetail(error);
            if (detail && !['prepare-sigterm', 'matrix'].includes(ownershipCase))
              process.stderr.write(
                'BFF listener startup observation: ' + JSON.stringify(detail) + '\n',
              );
            throw error;
          }
        } else {
          const until = Date.now() + 20_000;
          while (!server.scanner.tail.includes('Public HTTPS listener ready on 127.0.0.1:3000')) {
            assert.equal(server.process.exitCode, null, 'native listener alive');
            assert.ok(Date.now() < until, 'listener startup deadline');
            await new Promise((resolve) => setTimeout(resolve, 25));
          }
          assert.ok(
            server.broker.capture.family(),
            'Native runtime family captured before readiness',
          );
        }
        return view;
      })();
      // The caller owns readiness even when stop deliberately precedes readiness.
      void ready.catch(() => {});
      return { ...view, ready };
    },
  });
  if (ownershipCase === 'matrix')
    matrixBuilds.set(prepared, { owner, app, launched: () => launched });
  return prepared;
}

export async function startBffProof(options) {
  const prepared = await prepareBffProof(options);
  return prepared.launch().ready;
}

export const jar = (response) =>
  (response.headers['set-cookie'] ?? [])
    .map((value) => value.split(';', 1)[0])
    .filter((value) => !value.endsWith('='))
    .join('; ');
