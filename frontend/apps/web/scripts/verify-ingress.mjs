// Production-build companion: a disposable configuration, real TLS and stock bypass.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  copyFileSync,
  cpSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect } from 'node:tls';
import { fileURLToPath } from 'node:url';

import { stopTestChild } from './stop-test-child.mjs';

const appRoot = fileURLToPath(new URL('../', import.meta.url));
const fixtureRoot = mkdtempSync(join(tmpdir(), 'learnstack-production-ingress-'));
const app = join(fixtureRoot, 'frontend/apps/web');
const secret = randomBytes(32).toString('base64url');
const children = [];
const upgrades = [];
const observations = new Map();
let observationId = 0;
const require = createRequire(join(appRoot, 'package.json'));
const WebSocket = require('next/dist/compiled/ws');
let bootstrapCalls = 0;
let listenerFailed = false;
const api = createServer((request, response) => {
  bootstrapCalls++;
  try {
    // Boolean checks never print a credential in a failed assertion.
    assert(request.url === '/api/v1/public/site', 'Unexpected bootstrap path');
    assert(request.headers['x-learnstack-hop-secret'] === secret, 'Invalid fixture hop');
    assert(request.headers['x-learnstack-visitor-address'] === '127.0.0.1', 'Invalid fixture peer');
    assert(request.headers.authorization === undefined, 'Unexpected authorization');
    assert(request.headers.cookie === undefined, 'Unexpected cookie');
    assert(request.headers['x-learnstack-ingress-provenance'] === undefined, 'Unexpected stamp');
  } catch {
    // A listener throw cannot reach the outer finally. Refuse and make the
    // awaited control below fail there, where cleanup owns every child.
    listenerFailed = true;
    response.writeHead(503);
    response.end();
    return;
  }
  response.setHeader('content-type', 'application/json');
  if (request.headers['x-learnstack-host'] !== 'tenant.example:3000') {
    response.writeHead(404);
    response.end(
      JSON.stringify({
        type: 'about:blank',
        title: 'Not found',
        status: 404,
        instance: '/api/v1/public/site',
        code: 'not_found',
        messageKey: 'lockey_not_found',
        correlationId: 'fixture',
      }),
    );
    return;
  }
  response.end(
    JSON.stringify({
      displayName: 'Institution',
      enabledLocales: ['tr', 'en'],
      defaultLocale: 'tr',
      theme: null,
      showPlatformAttribution: true,
    }),
  );
});
let logs = '';
let shutdownFailed = false;
mkdirSync(join(app, 'scripts'), { recursive: true });
writeFileSync(
  join(fixtureRoot, 'tls.cnf'),
  '[req]\ndistinguished_name=dn\n' +
    'x509_extensions=ext\nprompt=no\n[dn]\nCN=localhost\n[ext]\n' +
    'subjectAltName=DNS:localhost\nbasicConstraints=critical,CA:TRUE\n',
);
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
    '-config',
    join(fixtureRoot, 'tls.cnf'),
    '-keyout',
    join(fixtureRoot, 'key.pem'),
    '-out',
    join(fixtureRoot, 'cert.pem'),
  ],
  { stdio: 'ignore' },
);
const certificate = readFileSync(join(fixtureRoot, 'cert.pem'));
writeFileSync(
  join(fixtureRoot, '.env'),
  [
    'LEARNSTACK_PUBLIC_API_ORIGIN=http://127.0.0.1:5080',
    'LEARNSTACK_PUBLIC_HOP_SECRET=' + secret,
    'LEARNSTACK_PUBLIC_TLS_CERT=cert.pem',
    'LEARNSTACK_PUBLIC_TLS_KEY=key.pem',
    'DEBUG=next:*',
    '',
  ].join('\n'),
  { mode: 0o600 },
);
for (const name of ['.next', '.server', 'node_modules', 'src']) {
  symlinkSync(join(appRoot, name), join(app, name), 'dir');
}
for (const name of ['package.json', 'next.config.ts', 'tsconfig.json']) {
  copyFileSync(join(appRoot, name), join(app, name));
}
copyFileSync(
  join(appRoot, 'scripts/development-hmr.mjs'),
  join(app, 'scripts/development-hmr.mjs'),
);
writeFileSync(join(app, '.env.production.local'), 'DEBUG=next:*\n');
writeFileSync(join(app, '.env.development.local'), 'DEBUG=next:*\n');
// Observe automatic Next upgrade registrations in the disposable fixture only.
// No diagnostic route, request value or probe is added to the shipped launcher.
const launcher = readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8');
writeFileSync(
  join(app, 'scripts/public-server.mjs'),
  launcher
    .replace(
      '  const handle = app.getRequestHandler();',
      `  let nextCalls = 0;
  let sinkCalls = 0;
  const nextHandle = app.getRequestHandler();
  const handle = (...args) => { nextCalls++; return nextHandle(...args); };
  const emit = upgradeSink.emit.bind(upgradeSink);
  upgradeSink.emit = (event, ...args) => {
    if (event === 'upgrade') sinkCalls++;
    return emit(event, ...args);
  };
  process.on('message', (message) => {
    if (message?.kind === 'observe') process.send?.({ id: message.id, nextCalls, sinkCalls, debugDisabled: process.env.DEBUG === '-*' });
  });`,
    )
    .replace(
      "  server.listen(3000, '127.0.0.1',",
      `  for (const [surface, target] of [['native', server], ['sink', upgradeSink]]) {
    const register = target.on.bind(target);
    target.on = (event, listener) => register(event, event !== 'upgrade' ? listener :
      (request, socket, head) => {
        process.send?.({ surface, sanitized:
          !request.headers['x-matched-path'] &&
          !request.headers['x-middleware-subrequest'] &&
          request.rawHeaders.filter((value, index) => index % 2 === 0 &&
            value.toLowerCase() === 'host').length === 1 });
        return listener(request, socket, head);
      });
  }
  server.listen(3000, '127.0.0.1',`,
    ),
);
assert.notEqual(readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8'), launcher);

function start(argv, dev = false) {
  const env = {
    ...process.env,
    NODE_ENV: dev ? 'development' : 'production',
    DEBUG: argv.some((argument) => argument.endsWith('public-server.mjs')) ? 'next:*' : '',
  };
  for (const key of Object.keys(env)) {
    if (key.startsWith('LEARNSTACK_PUBLIC_')) delete env[key];
  }
  const child = spawn(process.execPath, argv, {
    cwd: app,
    env,
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
    detached: process.platform !== 'win32',
  });
  child.on('message', (message) => {
    if (message.id) observations.get(message.id)?.(message);
    else upgrades.push(message);
  });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) {
    stream.on('data', (chunk) => {
      logs = (logs + chunk.toString()).slice(-65536);
    });
  }
  return child;
}

function observe(child) {
  return new Promise((resolve, reject) => {
    const id = ++observationId;
    const timer = setTimeout(() => {
      observations.delete(id);
      reject(new Error('IPC observation deadline'));
    }, 2000);
    observations.set(id, (message) => {
      clearTimeout(timer);
      observations.delete(id);
      resolve(message);
    });
    child.send({ kind: 'observe', id });
  });
}

async function callUpgrade(hostLines, path = '/_next/webpack-hmr', method = 'GET') {
  await new Promise((resolve, reject) => {
    const socket = connect(
      { host: '127.0.0.1', port: 3000, servername: 'localhost', ca: certificate },
      () =>
        socket.write(
          method +
            ' ' +
            path +
            ' HTTP/1.1\r\n' +
            hostLines +
            '\r\n' +
            'Connection: Upgrade\r\nUpgrade: websocket\r\n' +
            'Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: ' +
            randomBytes(16).toString('base64') +
            '\r\n' +
            'X-Matched-Path: attacker\r\nX-Middleware-Subrequest: attacker\r\n' +
            'X-LearnStack-Ingress-Provenance: forged\r\n\r\n',
        ),
    );
    const timer = setTimeout(() => {
      socket.destroy();
      reject(new Error('Server did not close unsupported upgrade'));
    }, 2000);
    socket.on('data', () => {
      reject(new Error('Unsupported upgrade produced a response'));
      socket.destroy();
    });
    socket.once('error', reject);
    socket.once('close', () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

function call(tls, port, path, headers = {}, method = 'GET') {
  return new Promise((resolve, reject) => {
    const send = tls ? httpsRequest : httpRequest;
    const outgoing = send(
      {
        hostname: '127.0.0.1',
        port,
        path,
        method,
        headers,
        ...(tls ? { ca: certificate, servername: 'localhost' } : {}),
        timeout: 2000,
      },
      (response) => {
        let body = '';
        response.on('data', (chunk) => {
          body += chunk.toString();
        });
        response.on('end', () =>
          resolve({ status: response.statusCode, headers: response.headers, body }),
        );
      },
    );
    outgoing.on('timeout', () => outgoing.destroy(new Error('Readiness timeout')));
    outgoing.on('error', reject);
    outgoing.end();
  });
}

async function ready(child, tls, port) {
  let lastStatus;
  for (let i = 0; i < 200; i++) {
    if (child.exitCode !== null || child.signalCode !== null)
      throw new Error('Fixture startup failed');
    try {
      const response = await call(tls, port, '/api/healthz');
      lastStatus = response.status;
      if (response.status === 200 && JSON.parse(response.body).status === 'healthy') return;
    } catch {
      /* Readiness may race preparation; no application retry policy here. */
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(
    'Fixture did not become ready: ' +
      JSON.stringify({
        lastStatus,
        compilation: logs.includes('Failed to compile'),
        missingModule: logs.includes('Module not found') || logs.includes('Cannot find module'),
        typescript: logs.includes('TypeScript'),
        postcss: logs.includes('postcss'),
        symlink: logs.includes('symlink'),
        middleware: logs.includes('middleware'),
      }),
  );
}

try {
  await new Promise((resolve, reject) => {
    api.once('error', reject);
    api.listen(0, '127.0.0.1', resolve);
  });
  const address = api.address();
  assert(address && typeof address === 'object');
  const privateEnvironment = readFileSync(join(fixtureRoot, '.env'), 'utf8');
  writeFileSync(
    join(fixtureRoot, '.env'),
    privateEnvironment.replace(
      'LEARNSTACK_PUBLIC_API_ORIGIN=http://127.0.0.1:5080',
      'LEARNSTACK_PUBLIC_API_ORIGIN=http://127.0.0.1:' + address.port,
    ),
    { mode: 0o600 },
  );
  const native = start([join(app, 'scripts/public-server.mjs')]);
  await ready(native, true, 3000);
  assert.equal((await observe(native)).debugDisabled, true, 'Framework dotenv re-enabled DEBUG');
  await callUpgrade('Host: localhost:3000\r\nHost: attacker.example');
  for (const path of ['/_next/webpack-hmr', '/unmatched', '/studio', '/api/healthz'])
    await callUpgrade('Host: localhost:3000', path);
  await callUpgrade('Host: localhost:3000', '/_next/webpack-hmr', 'POST');
  assert.equal((await observe(native)).sinkCalls, 0, 'Production upgrades reached Next');
  assert.deepEqual(upgrades, [], 'Production upgrades reached an automatic sink listener');
  assert.equal(bootstrapCalls, 0, 'Production upgrades reached bootstrap');

  const buildManifest = JSON.parse(
    readFileSync(join(appRoot, '.next/build-manifest.json'), 'utf8'),
  );
  const asset = buildManifest.rootMainFiles.find((path) => path.endsWith('.js'));
  assert(asset, 'Built asset control must be nonempty');
  const assetPath = '/_next/' + asset;
  const methods = ['POST', 'OPTIONS', 'TRACE', 'PUT', 'DELETE'];
  const paths = ['/en/courses', '/studio', '/portal', '/api/healthz', '/favicon.ico', assetPath];
  for (const path of paths) {
    const before = await observe(native);
    const beforeBootstrap = bootstrapCalls;
    for (const method of methods) {
      const response = await call(true, 3000, path, { Host: 'tenant.example:3000' }, method);
      assert.equal(listenerFailed, false, 'Bootstrap fixture refused the hop');
      assert.equal(
        response.status,
        404,
        'Unsupported method reached framework: ' + method + ' ' + path,
      );
      assert.equal(response.body, 'Not found');
      assert.equal(response.headers['cache-control'], 'no-store');
      assert.equal(response.headers['x-powered-by'], undefined);
      assert.equal(response.headers.location, undefined);
    }
    assert.equal(
      (await observe(native)).nextCalls,
      before.nextCalls,
      'Unsupported method reached Next',
    );
    assert.equal(bootstrapCalls, beforeBootstrap, 'Unsupported method reached bootstrap');
  }
  for (const [path, status, calls] of [
    ['/api/healthz', 200, 0],
    [assetPath, 200, 0],
    ['/favicon.ico', 404, 0],
    ['/faviconXico', 404, 1],
    ['/favicon.ico/extra', 404, 1],
    ['/nested/favicon.ico', 404, 1],
    ['/en/courses', 404, 1],
    ['/studio', 200, 1],
    ['/portal', 200, 1],
  ]) {
    for (const method of ['GET', 'HEAD']) {
      const before = bootstrapCalls;
      const response = await call(true, 3000, path, { Host: 'tenant.example:3000' }, method);
      assert.equal(listenerFailed, false, 'Bootstrap fixture refused the hop');
      assert.equal(
        response.status,
        status,
        'Supported method/path control failed: ' + method + ' ' + path,
      );
      assert.equal(
        bootstrapCalls - before,
        calls,
        'Exact matcher/bootstrap control failed: ' + path,
      );
      assert.equal(response.headers['x-powered-by'], undefined, 'Framework identified itself');
      if (method === 'HEAD') assert.equal(response.body, '', 'HEAD emitted a body: ' + path);
      else if (status === 200)
        assert(response.body.length > 0, 'GET success control must contain a body');
    }
  }
  const beforeHead = await observe(native);
  const refusedHead = await call(true, 3000, '/en/courses', { Host: 'bad@host' }, 'HEAD');
  assert.equal(refusedHead.status, 404);
  assert.equal(refusedHead.body, '');
  assert.equal(refusedHead.headers['cache-control'], 'no-store');
  assert.equal((await observe(native)).nextCalls, beforeHead.nextCalls);
  const beforeTrack = bootstrapCalls;
  const parsedRefusal = await call(
    true,
    3000,
    '/api/healthz',
    { Host: 'tenant.example:3000' },
    'TRACK',
  );
  assert.equal(parsedRefusal.status, 400, 'Node parser must refuse unsupported TRACK');
  assert.equal(parsedRefusal.body, '');
  assert.equal(bootstrapCalls, beforeTrack);
  assert.equal((await observe(native)).nextCalls, beforeHead.nextCalls);

  const beforePositive = bootstrapCalls;
  const positive = await call(true, 3000, '/en/courses?next=https%3A%2F%2Fevil.example', {
    Host: 'tenant.example:3000',
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-Forwarded-For': 'attacker',
    'X-Middleware-Subrequest': 'middleware:middleware:middleware',
  });
  assert.equal(listenerFailed, false, 'Bootstrap fixture refused the hop');
  assert.equal(positive.status, 404); // P6 pages are absent; verified bootstrap still ran.
  assert.equal(bootstrapCalls, beforePositive + 1);
  assert.match(positive.headers['cache-control'], /(?:^|,\s*)no-store(?:,|$)/);
  for (const query of [
    '?',
    '?x=%20',
    '?x=~',
    '?x=%2f',
    '?x',
    '?x=%41',
    '?_rsc=abc',
    '?x=1&x=2',
    '?x=%20&_rsc=abc',
    '?next=https://evil.example',
    '?x=tail.rsc',
    '?x=tail.rsc?extra=1',
    '?x=tail.rsc&y=1',
    '?x=tail%2Ersc',
    '?x=tail.rsc&_rsc=abc',
    "?x=o'neil&x=%27&blank=&x=last&next=https://evil.example",
  ]) {
    const response = await call(true, 3000, '/courses' + query, {
      Host: 'tenant.example:3000',
    });
    assert.equal(response.status, 307, 'Valid raw query must survive Next processing: ' + query);
    const location = new URL(response.headers.location);
    assert.equal(location.origin, 'https://tenant.example:3000');
    assert.equal(location.pathname, '/tr/courses');
    assert.deepEqual(
      [...location.searchParams],
      [...new URL('https://inert.invalid/' + query).searchParams],
    );
    if (query.includes("o'neil")) assert.match(response.headers.location, /x=o%27neil/);
    assert.equal(response.headers['cache-control'], 'no-store');
  }
  for (const path of [
    '/courses.rsc',
    '/en/courses.rsc',
    '/en/courses/a/lessons/intro.rsc',
    '/courses.prefetch.rsc',
    '/en/courses.segments/_tree.segment.rsc',
  ]) {
    const response = await call(true, 3000, path, { Host: 'tenant.example:3000' });
    assert.equal(response.status, 404, 'Framework suffix must not become a public route alias');
    assert.equal(response.headers.location, undefined);
  }
  const ambiguous = await call(true, 3000, '/en//courses?next=https://evil.example', {
    Host: 'unknown.example:3000',
  });
  assert.equal(ambiguous.status, 404);
  assert.equal(ambiguous.headers['cache-control'], 'no-store');
  assert.equal(ambiguous.headers.location, undefined);
  const stock = start([
    join(appRoot, 'node_modules/next/dist/bin/next'),
    'start',
    '--hostname',
    '127.0.0.1',
    '--port',
    '3011',
  ]);
  await ready(stock, false, 3011);
  const beforeBypass = bootstrapCalls;
  const bypass = await call(false, 3011, '/en/courses', {
    Host: 'tenant.example:3000',
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-LearnStack-Host': 'tenant.example',
    'X-LearnStack-Visitor-Address': '1.2.3.4',
    'X-Middleware-Subrequest': 'middleware:middleware:middleware:middleware:middleware',
    'X-Middleware-Subrequest-Id': 'attacker',
  });
  assert.equal(bypass.status, 404);
  assert.equal(bootstrapCalls, beforeBypass, 'Stock bypass must not reach bootstrap');
  for (const response of [positive, bypass]) {
    assert.equal(JSON.stringify(response).includes(secret), false, 'Secret containment failed');
    assert.equal(
      JSON.stringify(response).includes('x-learnstack-'),
      false,
      'Carrier containment failed',
    );
  }
  assert.equal(logs.includes(secret), false, 'Secret entered diagnostics');
  assert.equal(logs.includes('v1.'), false, 'Provenance entered diagnostics');

  // Development gets its own build directory, never the shared production output.
  await stopTestChild(stock);
  await stopTestChild(native);
  unlinkSync(join(app, '.next'));
  // Next's development route discovery does not traverse a symlinked src tree.
  unlinkSync(join(app, 'src'));
  cpSync(join(appRoot, 'src'), join(app, 'src'), { recursive: true });
  const development = start([join(app, 'scripts/public-server.mjs'), '--dev'], true);
  await ready(development, true, 3000);
  assert.equal(
    (await observe(development)).debugDisabled,
    true,
    'Development dotenv re-enabled DEBUG',
  );
  for (const [path, method] of [
    ['/_next/webpack-hmr-extra', 'GET'],
    ['/_next/webpack-hmr/child', 'GET'],
    ['/unmatched', 'GET'],
    ['/_next/webpack-hmr', 'POST'],
  ])
    await callUpgrade('Host: localhost:3000', path, method);
  assert.equal(
    (await observe(development)).sinkCalls,
    0,
    'Unsupported development upgrade reached Next',
  );
  await new Promise((resolve, reject) => {
    const client = new WebSocket('wss://127.0.0.1:3000/_next/webpack-hmr', {
      ca: certificate,
      servername: 'localhost',
      headers: { 'X-Matched-Path': 'attacker', 'X-Middleware-Subrequest': 'attacker' },
    });
    let accepted = false;
    let observedFrame = false;
    let retained = false;
    let retention;
    const deadline = setTimeout(() => {
      client.terminate();
      reject(new Error('Development HMR handshake/frame deadline'));
    }, 12000);
    client.on('upgrade', (response) => {
      accepted = response.statusCode === 101;
    });
    client.once('message', (bytes) => {
      try {
        const frame = JSON.parse(bytes.toString());
        assert(typeof frame.action === 'string', 'HMR must send a real protocol frame');
        observedFrame = true;
      } catch {
        client.terminate();
        clearTimeout(deadline);
        reject(new Error('Invalid development HMR frame'));
        return;
      }
      // The accepted connection must outlive the launcher's handshake deadline.
      retention = setTimeout(() => {
        clearTimeout(deadline);
        if (!accepted || client.readyState !== WebSocket.OPEN) {
          client.terminate();
          reject(new Error('Development HMR did not retain its accepted connection'));
        } else {
          retained = true;
          client.close();
        }
      }, 5100);
    });
    client.once('error', (error) => {
      clearTimeout(deadline);
      clearTimeout(retention);
      reject(error);
    });
    client.once('close', () => {
      clearTimeout(deadline);
      clearTimeout(retention);
      if (accepted && observedFrame && retained) resolve();
      else reject(new Error('Development HMR closed before retained acceptance'));
    });
  });
  assert.equal(
    (await observe(development)).sinkCalls,
    1,
    'Exactly the accepted HMR upgrade must reach Next',
  );
  assert.deepEqual(upgrades, [{ surface: 'sink', sanitized: true }]);
  assert.equal(logs.includes(secret), false, 'Secret entered development diagnostics');
  assert.equal(logs.includes('v1.'), false, 'Provenance entered development diagnostics');
  console.warn(
    'Native ingress: TLS, GET/HEAD admission, query redirects, production closure, DEBUG containment and development HMR passed.',
  );
} finally {
  api.closeAllConnections();
  await new Promise((resolve) => api.close(resolve));
  const stopped = await Promise.allSettled(children.map(stopTestChild));
  shutdownFailed = stopped.some((result) => result.status === 'rejected');
  rmSync(fixtureRoot, { recursive: true, force: true });
}
if (shutdownFailed) throw new Error('Fixture shutdown failed');
