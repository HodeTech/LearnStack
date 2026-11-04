import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect } from 'node:tls';
import { pathToFileURL } from 'node:url';

// Inert dependency fixtures exercise the actual native startup/shutdown owner.
// They do not qualify initialized Next, workers, copied proof relay or family joins.
const runtimeUrl = new URL('../apps/web/scripts/public-server-runtime.mjs', import.meta.url);
const source = await readFile(runtimeUrl, 'utf8');
const root = await mkdtemp(join(tmpdir(), 'learnstack-native-control-'));
const credentials = join(root, 'credentials');
await mkdir(credentials);
execFileSync(
  'openssl',
  [
    'req',
    '-x509',
    '-newkey',
    'rsa:2048',
    '-nodes',
    '-days',
    '1',
    '-subj',
    '/CN=localhost',
    '-keyout',
    join(credentials, 'key.pem'),
    '-out',
    join(credentials, 'certificate.pem'),
  ],
  { stdio: 'ignore' },
);
const diagnostics = [];
const originalError = console.error;
const originalWarn = console.warn;
const environmentKeys = ['NODE_TLS_REJECT_UNAUTHORIZED', 'DEBUG', 'NEXT_TRACE_SPAN_THRESHOLD_MS'];
const originalEnvironment = Object.fromEntries(
  environmentKeys.map((key) => [key, process.env[key]]),
);
console.error = (...values) => diagnostics.push(values.join(' '));
console.warn = (...values) => diagnostics.push(values.join(' '));
let fixtureNumber = 0;
const fixtureStates = [];

async function pending(promise) {
  let settled = false;
  void promise.then(
    () => {
      settled = true;
    },
    () => {
      settled = true;
    },
  );
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(settled, false, 'Actual held work must keep the native join pending');
}

async function unusedPort() {
  const server = createServer();
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
  return port;
}

async function fixture(options = {}) {
  const directory = join(root, `fixture-${++fixtureNumber}`);
  await mkdir(directory);
  const port = options.port ?? (await unusedPort());
  const files = {
    'state.mjs': `
      export const state = { events: [], options: ${JSON.stringify({ ...options, port })} };
      const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return {promise, resolve, reject}; };
      for (const name of ['import', 'prepare', 'requests', 'sessions', 'close', 'listen', 'upgrade']) state[name + 'Gate'] = deferred();
      for (const name of ['import', 'prepare', 'close', 'listen', 'upgrade']) state[name + 'Started'] = deferred();
      state.event = (name) => { state.events.push(name); state.onEvent?.(name); };
    `,
    'development.mjs': `
      import { state } from './state.mjs';
      state.event('development-import');
      if (state.options.holdImport) { state.importStarted.resolve(); await state.importGate.promise; }
      export const createFrameworkDevelopmentAdmission = () => ({assert() {}});
      export const protectFrameworkDevelopmentResponse = () => {};
    `,
    'hmr.mjs': `
      import { state } from './state.mjs';
      export function delegateDevelopmentHmr(_request, socket) {
        state.upgradeSocket = socket;
        socket.once('close', () => state.event('upgrade-closed'));
        if (state.options.holdUpgrade) {
          const destroy = socket.destroy;
          socket.destroy = function () {
            state.event('upgrade-destroy');
            void state.upgradeGate.promise.then(() => destroy.call(this));
            return this;
          };
        }
        socket.write('HTTP/1.1 101 Switching Protocols\\r\\nConnection: Upgrade\\r\\nUpgrade: websocket\\r\\n\\r\\n');
        state.upgradeStarted.resolve();
      }
    `,
    'configuration.mjs': `
      import { state } from './state.mjs';
      if (state.options.rejectImport) throw new Error('credential-redaction-marker');
      export class BffConfigurationError extends Error {}
      export function bffConfiguration() {
        state.event('configuration');
        if (state.options.rejectConfiguration) throw new Error('credential-redaction-marker');
        return {enabled: state.options.bff === true, issuer: 'https://fixture.invalid'};
      }
    `,
    'logout.mjs': 'export const protectBffLogoutResponse = () => {};',
    'ingress.mjs': `
      import { state } from './state.mjs';
      export const PUBLIC_ENV_KEYS = [];
      export const PUBLIC_HTTPS_PORT = ${port};
      export const INGRESS_HEADER = 'fixture';
      export const loadLocalEnvironment = () => ({});
      export const publicServerConfiguration = () => ({certificate: ${JSON.stringify(join(credentials, 'certificate.pem'))}, privateKey: ${JSON.stringify(join(credentials, 'key.pem'))}});
      export const admitIncomingRequest = () => true;
      export const verifyProvenance = () => ({method: 'GET', target: '/', host: 'fixture.invalid'});
      export const refuseIngress = (response) => { response.writeHead(503); response.end(); };
    `,
    'admission.mjs': `
      import { state } from './state.mjs';
      export function installPublicAdmissionRuntime() {
        state.event('admission-created');
        return {holder: {}, shutdown() { state.event('admission-stop'); }, run(_binding, _request, _response, work) { return work(); }};
      }
    `,
    'dependencies.mjs': `
      export const createTenantProvider = () => ({});
      export const createAccountSiteCaller = () => ({});
      export const createNativeSessionApi = () => ({});
      export const createNativeAdministrationCaller = () => ({});
      export const createNativeInvitationApi = () => ({});
      export const nativeInvitationTarget = () => false;
      export function createInvitationRenderer({en, tr}) {
        if (en.fixture !== 'en' || tr.fixture !== 'tr') throw new Error('Fixture catalogue wiring failed');
        return () => '';
      }
      export function createNativeInvitationHandler({store, responseLease, render}) {
        if (!store || typeof responseLease !== 'function' || typeof render !== 'function') throw new Error('Fixture invitation wiring failed');
        return () => {};
      }
    `,
    'invitation-en.json': JSON.stringify({ fixture: 'en' }),
    'invitation-tr.json': JSON.stringify({ fixture: 'tr' }),
    'requests.mjs': `
      import { state } from './state.mjs';
      export function installBffRequestRuntime() {
        state.event('requests-created');
        return {preparation: {}, nativeResponse() { throw new Error('Unused fixture response lease'); }, run(_binding, _request, _response, work) { return work(); }, async stop() { state.event('requests-stop'); if (state.options.holdRequests) await state.requestsGate.promise; state.event('requests-joined'); }};
      }
    `,
    'sessions.mjs': `
      import { state } from './state.mjs';
      export function installBffSessionRuntime() {
        state.event('sessions-created');
        if (state.options.rejectSessions) throw new Error('credential-redaction-marker');
        return {store: {}, async stop() { state.event('sessions-stop'); if (state.options.holdSessions) await state.sessionsGate.promise; state.event('sessions-joined'); }};
      }
    `,
    'next.mjs': `
      import { state } from './state.mjs';
      state.privacy = ['NODE_TLS_REJECT_UNAUTHORIZED', 'DEBUG', 'NEXT_TRACE_SPAN_THRESHOLD_MS'].map(key => process.env[key]);
      export default function next() {
        state.event('app-created');
        return {
          async prepare() { state.event('prepare-start'); state.prepareStarted.resolve(); if (state.options.holdPrepare) await state.prepareGate.promise; state.event('prepare-settled'); },
          getRequestHandler() { state.event('handler-created'); return (_request, response) => { response.end('native-control'); }; },
          async close() { state.event('app-close'); state.closeStarted.resolve(); if (state.options.holdClose) await state.closeGate.promise; if (state.options.rejectClose) throw new Error('credential-redaction-marker'); state.event('app-joined'); },
        };
      }
    `,
    'http.mjs': `
      import { createServer as actualCreateServer } from 'node:http';
      import { state } from './state.mjs';
      export function createServer(...args) {
        state.event('sink-created');
        const sink = actualCreateServer(...args);
        const close = sink.close;
        sink.close = function (...values) { state.event('sink-close'); return close.apply(this, values); };
        return sink;
      }
    `,
    'https.mjs': `
      import { createServer as actualCreateServer } from 'node:https';
      import { state } from './state.mjs';
      export function createServer(...args) {
        state.event('server-created');
        const server = actualCreateServer(...args);
        state.server = server;
        const listen = server.listen;
        const close = server.close;
        server.listen = function (port, host, callback) {
          state.event('listen-start');
          if (state.options.rejectListen) throw new Error('credential-redaction-marker');
          return listen.call(this, port, host, async () => {
            state.event('listen-bound'); state.listenStarted.resolve();
            if (state.options.holdListen) await state.listenGate.promise;
            callback();
          });
        };
        server.close = function (callback) {
          state.event('server-close');
          return close.call(this, error => { state.event('server-closed'); callback(error); });
        };
        return server;
      }
    `,
  };
  let runtime = source;
  const replacements = new Map([
    ['node:http', './http.mjs'],
    ['node:https', './https.mjs'],
    ['./development-hmr.mjs', './hmr.mjs'],
    ['../../../scripts/framework-development.mjs', './development.mjs'],
    ['../.server/auth/configuration.js', './configuration.mjs'],
    ['../.server/auth/logout-response.js', './logout.mjs'],
    ['../.server/ingress.js', './ingress.mjs'],
    ['../.server/public-admission-runtime.js', './admission.mjs'],
    ['../.server/auth/provider-core.js', './dependencies.mjs'],
    ['../.server/auth/account-client.js', './dependencies.mjs'],
    ['../.server/auth/native-session-client.js', './dependencies.mjs'],
    ['../.server/auth/native-administration-client.js', './dependencies.mjs'],
    ['../.server/auth/native-invitation-client.js', './dependencies.mjs'],
    ['../.server/auth/native-invitation-http.js', './dependencies.mjs'],
    ['../.server/auth/invitation-html.js', './dependencies.mjs'],
    ['../src/i18n/messages/en/invitation.json', './invitation-en.json'],
    ['../src/i18n/messages/tr/invitation.json', './invitation-tr.json'],
    ['../.server/auth/session-runtime.js', './sessions.mjs'],
    ['../.server/auth/request-runtime.js', './requests.mjs'],
    ['next', './next.mjs'],
  ]);
  for (const [before, after] of replacements) {
    assert.equal(runtime.split(`'${before}'`).length, 2, `Exact inert fixture import: ${before}`);
    runtime = runtime.replace(`'${before}'`, `'${after}'`);
  }
  files['runtime.mjs'] = runtime;
  await Promise.all(
    Object.entries(files).map(([name, body]) => writeFile(join(directory, name), body)),
  );
  const { state } = await import(pathToFileURL(join(directory, 'state.mjs')).href);
  fixtureStates.push(state);
  const { startPublicRuntime } = await import(pathToFileURL(join(directory, 'runtime.mjs')).href);
  let stopped = options.alreadyStopped === true;
  let registered;
  let shutdown;
  const reasons = [];
  const ready = [];
  const control = Object.freeze({
    framework: Object.freeze({
      mode: options.development ? 'development' : 'production',
      identity: Object.freeze({}),
    }),
    registerShutdown(handler) {
      assert.equal(registered, undefined);
      registered = handler;
      state.event('shutdown-registered');
      if (stopped) stop('already-stopped');
    },
    requestStop: (reason) => stop(reason),
    stopRequested: () => stopped,
    listenerReady(server) {
      assert.equal(stopped, false);
      assert.equal(server.listening, true);
      assert.equal(server.address().port, port);
      ready.push(server);
      state.event('ready');
      return true;
    },
  });
  function stop(reason = 'test-stop') {
    stopped = true;
    reasons.push(reason);
    if (registered) {
      const actual = registered();
      if (shutdown) assert.equal(actual, shutdown, 'Repeated stops share one native join');
      shutdown = actual;
      void shutdown.catch(() => {});
    }
    return shutdown;
  }
  return {
    state,
    control,
    start: () => startPublicRuntime(control),
    stop,
    ready,
    reasons,
    joined: () => shutdown,
    port,
  };
}

try {
  const before = Object.fromEntries(environmentKeys.map((key) => [key, process.env[key]]));
  const handlers = ['SIGINT', 'SIGTERM'].map((name) => process.listenerCount(name));
  const exports = await import(runtimeUrl);
  assert.equal(typeof exports.startPublicRuntime, 'function');
  assert.deepEqual(
    Object.fromEntries(environmentKeys.map((key) => [key, process.env[key]])),
    before,
  );
  assert.deepEqual(
    ['SIGINT', 'SIGTERM'].map((name) => process.listenerCount(name)),
    handlers,
  );
  assert.doesNotMatch(source, /\bsetTimeout\s*\(|process\.exit\s*\(|process\.(?:once|on)\s*\(/);
  assert.doesNotMatch(source, /learnstack\.framework\.runtime/);

  const stopped = await fixture({ alreadyStopped: true });
  await stopped.start();
  await stopped.joined();
  assert.deepEqual(stopped.state.events, ['shutdown-registered']);

  const importing = await fixture({ holdImport: true });
  const importStart = importing.start();
  await importing.state.importStarted.promise;
  const importStop = importing.stop();
  await pending(importStop);
  importing.state.importGate.resolve();
  await Promise.all([importStart, importStop]);
  assert.equal(importing.state.events.includes('admission-created'), false);
  assert.equal(importing.state.events.includes('app-created'), false);

  const importFault = await fixture({ holdImport: true, rejectImport: true });
  const faultStart = importFault.start();
  await importFault.state.importStarted.promise;
  await faultStart;
  assert.equal(importFault.reasons.includes('startup-fault'), true);
  await pending(importFault.joined());
  importFault.state.importGate.resolve();
  await assert.rejects(importFault.joined(), /^Error: Public server shutdown failed$/);

  const configurationFault = await fixture({ rejectConfiguration: true });
  await configurationFault.start();
  await assert.rejects(configurationFault.joined(), /^Error: Public server shutdown failed$/);
  assert.equal(configurationFault.state.events.includes('admission-created'), false);

  for (const boundary of ['admission-created', 'app-created']) {
    const partial = await fixture();
    partial.state.onEvent = (event) => {
      if (event === boundary) partial.stop();
    };
    await partial.start();
    await partial.joined();
    assert.equal(partial.state.events.filter((event) => event === 'admission-stop').length, 1);
    assert.equal(partial.state.events.includes('prepare-start'), false);
    assert.equal(partial.state.events.includes('server-created'), false);
    assert.equal(
      partial.state.events.filter((event) => event === 'app-close').length,
      boundary === 'app-created' ? 1 : 0,
    );
  }

  const sessionsFault = await fixture({ bff: true, rejectSessions: true });
  await sessionsFault.start();
  await assert.rejects(sessionsFault.joined(), /^Error: Public server shutdown failed$/);
  assert.equal(
    sessionsFault.state.events.indexOf('admission-stop') <
      sessionsFault.state.events.indexOf('requests-stop'),
    true,
  );
  assert.equal(sessionsFault.state.events.includes('sessions-stop'), false);

  const preparing = await fixture({
    bff: true,
    holdPrepare: true,
    holdRequests: true,
    holdSessions: true,
    holdClose: true,
  });
  const prepareStart = preparing.start();
  await preparing.state.prepareStarted.promise;
  const prepareStop = preparing.stop();
  assert.equal(preparing.stop(), prepareStop);
  await pending(prepareStop);
  assert.deepEqual(preparing.state.events.slice(-3), [
    'admission-stop',
    'requests-stop',
    'sessions-stop',
  ]);
  assert.equal(preparing.state.events.includes('app-close'), false);
  preparing.state.prepareGate.resolve();
  await prepareStart;
  await preparing.state.closeStarted.promise;
  assert.equal(preparing.state.events.includes('server-created'), false);
  assert.equal(preparing.state.events.includes('sink-close'), false);
  assert.equal(
    preparing.state.events.indexOf('prepare-settled') < preparing.state.events.indexOf('app-close'),
    true,
  );
  preparing.state.closeGate.resolve();
  preparing.state.sessionsGate.resolve();
  await pending(prepareStop);
  preparing.state.requestsGate.resolve();
  await prepareStop;

  const prepareFault = await fixture({ holdPrepare: true });
  const rejectedStart = prepareFault.start();
  await prepareFault.state.prepareStarted.promise;
  const rejectedStop = prepareFault.stop();
  prepareFault.state.prepareGate.reject(new Error('credential-redaction-marker'));
  await rejectedStart;
  await assert.rejects(rejectedStop, /^Error: Public server shutdown failed$/);
  assert.equal(prepareFault.state.events.includes('app-close'), true);
  assert.equal(prepareFault.state.events.includes('server-created'), false);

  const closeFault = await fixture({ rejectClose: true });
  await closeFault.start();
  await assert.rejects(closeFault.stop(), /^Error: Public server shutdown failed$/);
  assert.equal(closeFault.state.events.includes('server-closed'), true);

  const listening = await fixture({ holdListen: true });
  const listenStart = listening.start();
  await listening.state.listenStarted.promise;
  const listenStop = listening.stop();
  await pending(listenStop);
  assert.equal(listening.ready.length, 0);
  listening.state.listenGate.resolve();
  await Promise.all([listenStart, listenStop]);
  assert.equal(listening.ready.length, 0);
  assert.equal(listening.state.server.listening, false);
  assert.equal(listening.state.events.includes('server-closed'), true);

  const healthy = await fixture();
  await healthy.start();
  assert.equal(healthy.ready.length, 1);
  assert.deepEqual(healthy.state.privacy, ['1', '-*', '9007199254740991']);
  const body = await new Promise((resolve, reject) => {
    const outgoing = request(
      { host: '127.0.0.1', port: healthy.port, rejectUnauthorized: false, agent: false },
      (response) => {
        let body = '';
        response.setEncoding('utf8');
        response.on('data', (chunk) => {
          body += chunk;
        });
        response.on('end', () => resolve(body));
      },
    );
    outgoing.on('error', reject);
    outgoing.end();
  });
  assert.equal(body, 'native-control');
  await healthy.stop();
  assert.equal(healthy.state.server.listening, false);
  assert.equal(healthy.state.events.filter((event) => event === 'server-close').length, 1);
  assert.equal(healthy.state.events.includes('sink-close'), false);

  const upgraded = await fixture({ development: true, holdUpgrade: true });
  await upgraded.start();
  const peer = connect({ host: '127.0.0.1', port: upgraded.port, rejectUnauthorized: false });
  await new Promise((resolve, reject) => {
    peer.once('error', reject);
    peer.once('secureConnect', () => {
      peer.write(
        'GET /_next/webpack-hmr HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n',
      );
    });
    peer.once('data', (data) => {
      assert.match(data.toString(), /^HTTP\/1.1 101 /);
      resolve();
    });
  });
  const peerClosed = new Promise((resolve) => peer.once('close', resolve));
  const upgradeStop = upgraded.stop();
  await pending(upgradeStop);
  assert.equal(upgraded.state.upgradeSocket.closed, false);
  assert.equal(upgraded.state.events.includes('upgrade-destroy'), true);
  upgraded.state.upgradeGate.resolve();
  await Promise.all([upgradeStop, peerClosed]);
  assert.equal(upgraded.state.upgradeSocket.closed, true);
  assert.equal(upgraded.state.events.includes('upgrade-closed'), true);

  const listenerFault = await fixture();
  await listenerFault.start();
  listenerFault.state.server.emit('error', new Error('credential-redaction-marker'));
  await assert.rejects(listenerFault.joined(), /^Error: Public server shutdown failed$/);
  assert.equal(listenerFault.reasons.includes('listener-fault'), true);
  assert.equal(listenerFault.state.events.includes('server-closed'), true);

  const synchronousListenFault = await fixture({ rejectListen: true });
  await synchronousListenFault.start();
  await assert.rejects(synchronousListenFault.joined(), /^Error: Public server shutdown failed$/);
  assert.equal(synchronousListenFault.reasons.includes('listener-fault'), true);
  assert.equal(synchronousListenFault.state.events.includes('server-close'), false);
  assert.equal(synchronousListenFault.state.events.includes('app-close'), true);

  const blocker = createServer();
  await new Promise((resolve) => blocker.listen(0, '127.0.0.1', resolve));
  try {
    const failedBind = await fixture({ port: blocker.address().port });
    await failedBind.start();
    await assert.rejects(failedBind.joined(), /^Error: Public server shutdown failed$/);
    assert.equal(failedBind.reasons.includes('listener-fault'), true);
    assert.equal(failedBind.ready.length, 0);
    assert.equal(failedBind.state.events.includes('server-close'), false);
    assert.equal(failedBind.state.events.includes('app-close'), true);
  } finally {
    await new Promise((resolve) => blocker.close(resolve));
  }
  assert.equal(
    diagnostics.some((line) => line.includes('credential-redaction-marker')),
    false,
  );
} finally {
  for (const state of fixtureStates) {
    for (const name of ['import', 'prepare', 'requests', 'sessions', 'close', 'listen', 'upgrade'])
      state[name + 'Gate'].resolve();
    state.upgradeSocket?.destroy();
    if (state.server?.listening) {
      const closed = new Promise((resolve) => state.server.close(resolve));
      state.server.closeAllConnections();
      await closed;
    }
  }
  console.error = originalError;
  console.warn = originalWarn;
  for (const [key, value] of Object.entries(originalEnvironment)) {
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  await rm(root, { recursive: true, force: true });
}
process.stdout.write(
  'Native runtime control checks passed (inert Next/dependency fixtures; actual TLS listeners).\n',
);
