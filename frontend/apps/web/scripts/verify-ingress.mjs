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
import { createServer as createPortProbe } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect } from 'node:tls';
import { fileURLToPath } from 'node:url';

import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const appRoot = fileURLToPath(new URL('../', import.meta.url));
const owner = createFixtureOwner();
owner.install({ control: process.connected ? process : undefined, event: 'disconnect' });
let fixtureRoot;
let app;
let certificate;
let wrongCertificate;
let api;
let WebSocket;
let sharp;
let optimizerPixels;
const optimizerImagePath = '/_next/static/media/ingress-optimizer-proof.png';
const optimizerPath = '/_next/image?url=' + encodeURIComponent(optimizerImagePath) + '&w=64&q=75';
const secret = randomBytes(32).toString('base64url');
const scanners = [];
let verificationFailed = false;
const responseScanner = createPrivateScanner([secret]);
const diagnosticsLeaked = () => scanners.some((scanner) => scanner.leaked);
const diagnosticIncludes = (value) => scanners.some((scanner) => scanner.tail.includes(value));
const fence = randomBytes(16).toString('hex');
const children = [];
const bindings = new WeakMap();
const startupFailures = new WeakSet();
const upgrades = [];
const observations = new Map();
let observationId = 0;
let bootstrapCalls = 0;
let listenerFailed = false;

try {
  fixtureRoot = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-production-ingress-')));
  app = join(fixtureRoot, 'frontend/apps/web');
  for (const port of [3000, 3011]) await preflight(port);
  await checkpoint('root');
  const require = createRequire(join(appRoot, 'package.json'));
  WebSocket = require('next/dist/compiled/ws');
  sharp = createRequire(require.resolve('next/package.json'))('sharp');
  api = owner.ownServer(
    createServer((request, response) => {
      bootstrapCalls++;
      try {
        // Boolean checks never print a credential in a failed assertion.
        assert(request.url === '/api/v1/public/site', 'Unexpected bootstrap path');
        assert(request.headers['x-learnstack-hop-secret'] === secret, 'Invalid fixture hop');
        assert(
          request.headers['x-learnstack-visitor-address'] === '127.0.0.1',
          'Invalid fixture peer',
        );
        assert(request.headers.authorization === undefined, 'Unexpected authorization');
        assert(request.headers.cookie === undefined, 'Unexpected cookie');
        assert(
          request.headers['x-learnstack-ingress-provenance'] === undefined,
          'Unexpected stamp',
        );
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
    }),
  );
  mkdirSync(join(app, 'scripts'), { recursive: true });
  writeFileSync(
    join(fixtureRoot, 'tls.cnf'),
    '[req]\ndistinguished_name=dn\n' +
      'x509_extensions=ext\nprompt=no\n[dn]\nCN=localhost\n[ext]\n' +
      'subjectAltName=DNS:localhost\nbasicConstraints=critical,CA:TRUE\n',
  );
  try {
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
      { stdio: 'ignore', env: fixtureEnvironment({ root: fixtureRoot }) },
    );
  } catch {
    throw new Error('Fixture TLS certificate setup failed');
  }
  certificate = readFileSync(join(fixtureRoot, 'cert.pem'));
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
      '/CN=untrusted.invalid',
      '-keyout',
      join(fixtureRoot, 'wrong-key.pem'),
      '-out',
      join(fixtureRoot, 'wrong-cert.pem'),
    ],
    { stdio: 'ignore', env: fixtureEnvironment({ root: fixtureRoot }) },
  );
  wrongCertificate = readFileSync(join(fixtureRoot, 'wrong-cert.pem'));
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
  // Both the image fixture/cache and the later mutant belong to this copy.
  // Never write into the caller's production build through a symlink.
  cpSync(join(appRoot, '.next'), join(app, '.next'), {
    recursive: true,
    filter: (path) => path !== join(appRoot, '.next/cache'),
  });
  const optimizerImage = await sharp({
    create: { width: 2, height: 2, channels: 4, background: '#216ba5' },
  })
    .png()
    .toBuffer();
  optimizerPixels = await sharp(optimizerImage).ensureAlpha().raw().toBuffer();
  mkdirSync(join(app, '.next/static/media'), { recursive: true });
  writeFileSync(join(app, '.next/static/media/ingress-optimizer-proof.png'), optimizerImage);
  for (const name of ['.server', 'node_modules', 'src']) {
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
  writeFileSync(
    join(app, 'scripts/bind-proof.mjs'),
    `
  import { Server } from 'node:net';
  const listen = Server.prototype.listen;
  Server.prototype.listen = function (...args) {
    this.once('listening', () => {
      const address = this.address();
      if (address && typeof address === 'object') process.send?.({ kind: 'bound', pid: process.pid, port: address.port, address: address.address });
    });
    return listen.apply(this, args);
  };
  `,
  );
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
    process.on('message', async (message) => {
      if (message?.kind !== 'observe') return;
      const marker = message.fence + ':' + message.id + ':end\\n';
      await Promise.all([process.stdout, process.stderr].map((stream) =>
        new Promise((resolve) => stream.write(marker, resolve))));
      process.send?.({ id: message.id, nextCalls, sinkCalls, debugDisabled: process.env.DEBUG === '-*', environmentSafe: process.env.NODE_TLS_REJECT_UNAUTHORIZED === '1' && ['NODE_OPTIONS', 'NODE_EXTRA_CA_CERTS', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'INGRESS_ENV_CANARY'].every((key) => process.env[key] === undefined) });
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

  await checkpoint('setup');
  await run();
} catch (error) {
  verificationFailed = true;
  throw error;
} finally {
  await owner.dispose();
  if (!verificationFailed)
    assert.equal(diagnosticsLeaked(), false, 'Private material entered shutdown diagnostics');
}

function start(argv, dev = false) {
  owner.assertActive();
  const env = fixtureEnvironment({
    root: fixtureRoot,
    nodeEnv: dev ? 'development' : 'production',
    debug: argv.some((argument) => argument.endsWith('public-server.mjs')) ? 'next:*' : '',
  });
  const child = owner.ownChild(
    spawn(process.execPath, ['--import', join(app, 'scripts/bind-proof.mjs'), ...argv], {
      cwd: app,
      env,
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
      detached: process.platform !== 'win32',
    }),
  );
  bindings.set(child, new Map());
  child.once('error', () => startupFailures.add(child));
  child.on('message', (message) => {
    if (message.kind === 'bound') bindings.get(child).set(message.port, message);
    else if (message.id) observations.get(message.id)?.receive(message);
    else upgrades.push(message);
  });
  children.push(child);
  for (const [index, stream] of [child.stdout, child.stderr].entries()) {
    const scanner = createPrivateScanner([secret]);
    scanners.push(scanner);
    stream.once('end', () => scanner.finish());
    stream.once('close', () => scanner.finish());
    let overlap = '';
    stream.on('data', (chunk) => {
      scanner.push(chunk);
      const text = overlap + chunk.toString();
      for (const [id, observation] of observations) {
        if (observation.child === child && text.includes(fence + ':' + id + ':end\n'))
          observation.receive(index);
      }
      overlap = text.slice(-128);
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
    const received = new Set();
    let result;
    observations.set(id, {
      child,
      receive(value) {
        if (typeof value === 'number') received.add(value);
        else result = value;
        if (!result || received.size !== 2) return;
        clearTimeout(timer);
        observations.delete(id);
        resolve(result);
      },
    });
    child.send({ kind: 'observe', id, fence }, (error) => {
      if (!error) return;
      clearTimeout(timer);
      observations.delete(id);
      reject(new Error('Owned IPC observation failed'));
    });
  });
}

async function callUpgrade(hostLines, path = '/_next/webpack-hmr', method = 'GET') {
  await new Promise((resolve, reject) => {
    const socket = connect(
      {
        host: '127.0.0.1',
        port: 3000,
        servername: 'localhost',
        ca: certificate,
        rejectUnauthorized: true,
      },
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

function call(tls, port, path, headers = {}, method = 'GET', tlsOptions = {}) {
  return new Promise((resolve, reject) => {
    const send = tls ? httpsRequest : httpRequest;
    const outgoing = send(
      {
        hostname: '127.0.0.1',
        port,
        path,
        method,
        headers,
        ...(tls
          ? { ca: certificate, servername: 'localhost', rejectUnauthorized: true, ...tlsOptions }
          : {}),
        timeout: 2000,
      },
      (response) => {
        const chunks = [];
        response.on('data', (chunk) => {
          chunks.push(chunk);
        });
        response.on('end', () => {
          clearTimeout(deadline);
          const bytes = Buffer.concat(chunks);
          resolve({
            status: response.statusCode,
            headers: response.headers,
            body: bytes.toString(),
            bytes,
          });
        });
      },
    );
    const deadline = setTimeout(() => outgoing.destroy(new Error('Readiness timeout')), 2000);
    outgoing.on('timeout', () => outgoing.destroy(new Error('Readiness timeout')));
    outgoing.on('error', (error) => {
      clearTimeout(deadline);
      reject(error);
    });
    outgoing.end();
  });
}

function assertOptimizerDisabled(response, method) {
  assert.equal(response.status, 404, 'Image optimizer must remain disabled');
  if (method === 'HEAD') assert.equal(response.bytes.length, 0, 'Optimizer HEAD must be bodyless');
}

async function assertOptimizerImage(response) {
  assert.equal(response.status, 200, 'Valid image positive control must succeed');
  assert.equal(response.headers['content-type'], 'image/png');
  // Fully decode the HTTP bytes: a success code or a PNG-like header is not enough.
  const decoded = await sharp(response.bytes)
    .ensureAlpha()
    .raw()
    .toBuffer({ resolveWithObject: true });
  assert.equal(decoded.info.width, 2);
  assert.equal(decoded.info.height, 2);
  assert.deepEqual(decoded.data, optimizerPixels, 'Image positive control pixels changed');
}

async function ready(child, tls, port) {
  let lastStatus;
  const deadline = performance.now() + 20000;
  while (performance.now() < deadline) {
    owner.assertActive();
    if (startupFailures.has(child) || child.exitCode !== null || child.signalCode !== null)
      throw new Error('Fixture startup failed');
    try {
      const response = await call(tls, port, '/api/healthz');
      lastStatus = response.status;
      const bound = bindings.get(child).get(port);
      if (
        bound?.pid === child.pid &&
        bound.address === '127.0.0.1' &&
        child.exitCode === null &&
        child.signalCode === null &&
        response.status === 200 &&
        JSON.parse(response.body).status === 'healthy'
      )
        return;
    } catch {
      /* Readiness may race preparation; no application retry policy here. */
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(
    'Fixture did not become ready: ' +
      JSON.stringify({
        lastStatus,
        compilation: diagnosticIncludes('Failed to compile'),
        missingModule:
          diagnosticIncludes('Module not found') || diagnosticIncludes('Cannot find module'),
        typescript: diagnosticIncludes('TypeScript'),
        postcss: diagnosticIncludes('postcss'),
        symlink: diagnosticIncludes('symlink'),
        middleware: diagnosticIncludes('middleware'),
      }),
  );
}

async function run() {
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
  await checkpoint('native');
  const beforeTls = bootstrapCalls;
  for (const tlsOptions of [{ ca: wrongCertificate }, { servername: 'wrong.example' }]) {
    await assert.rejects(
      call(true, 3000, '/en/courses', { Host: 'tenant.example:3000' }, 'GET', tlsOptions),
      (error) =>
        [
          'DEPTH_ZERO_SELF_SIGNED_CERT',
          'SELF_SIGNED_CERT_IN_CHAIN',
          'ERR_TLS_CERT_ALTNAME_INVALID',
          'UNABLE_TO_VERIFY_LEAF_SIGNATURE',
        ].includes(error.code),
      'TLS must reject untrusted CA and wrong hostname',
    );
  }
  await observe(native);
  assert.equal(bootstrapCalls, beforeTls, 'Rejected TLS reached bootstrap');
  const nativeState = await observe(native);
  assert.equal(nativeState.debugDisabled, true, 'Framework dotenv re-enabled DEBUG');
  assert.equal(nativeState.environmentSafe, true, 'Child environment was not isolated');
  await callUpgrade('Host: localhost:3000\r\nHost: attacker.example');
  for (const path of ['/_next/webpack-hmr', '/unmatched', '/studio', '/api/healthz'])
    await callUpgrade('Host: localhost:3000', path);
  await callUpgrade('Host: localhost:3000', '/_next/webpack-hmr', 'POST');
  assert.equal((await observe(native)).sinkCalls, 0, 'Production upgrades reached Next');
  assert.deepEqual(upgrades, [], 'Production upgrades reached an automatic sink listener');
  assert.equal(bootstrapCalls, 0, 'Production upgrades reached bootstrap');

  await assertOptimizerImage(
    await call(true, 3000, optimizerImagePath, { Host: 'tenant.example:3000' }),
  );
  for (const method of ['GET', 'HEAD']) {
    const response = await call(true, 3000, optimizerPath, { Host: 'tenant.example:3000' }, method);
    assertOptimizerDisabled(response, method);
  }

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
    ['/en/status/not-found', 404, 2],
    ['/studio', 200, 2],
    ['/portal', 200, 2],
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
  const positive = await call(true, 3000, '/en/status/not-found?next=https%3A%2F%2Fevil.example', {
    Host: 'tenant.example:3000',
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-Forwarded-For': 'attacker',
    'X-Middleware-Subrequest': 'middleware:middleware:middleware',
  });
  assert.equal(listenerFailed, false, 'Bootstrap fixture refused the hop');
  assert.equal(positive.status, 404); // Actual branded status route is admitted without Education.
  assert.equal(bootstrapCalls, beforePositive + 2); // Middleware and request-local RSC bootstrap.
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
    assert.equal(
      responseScanner.contains(JSON.stringify(response)),
      false,
      'Private response containment failed',
    );
    assert.equal(
      JSON.stringify(response).includes('x-learnstack-'),
      false,
      'Carrier containment failed',
    );
  }
  assert.equal(diagnosticsLeaked(), false, 'Private material entered diagnostics');

  await stopTestChild(stock);
  await stopTestChild(native);
  // Next 15.5.27 loads next.config.ts for this custom production server; its
  // serialized build config contributes only isExperimentalCompile. Mutate the
  // copied runtime configuration and restart, without touching shared output.
  const configPath = join(app, 'next.config.ts');
  const disabledConfig = readFileSync(configPath, 'utf8');
  const enabledConfig = disabledConfig.replace(
    'images: { unoptimized: true }',
    'images: { unoptimized: false }',
  );
  assert.notEqual(enabledConfig, disabledConfig, 'Optimizer configuration mutant was not planted');
  writeFileSync(configPath, enabledConfig);
  const optimizerMutant = start([join(app, 'scripts/public-server.mjs')]);
  await ready(optimizerMutant, true, 3000);
  for (const method of ['GET', 'HEAD']) {
    const response = await call(true, 3000, optimizerPath, { Host: 'tenant.example:3000' }, method);
    assert.equal(response.status, 200, 'Enabling the optimizer must serve the valid image');
    if (method === 'GET') await assertOptimizerImage(response);
    if (method === 'HEAD') assert.equal(response.bytes.length, 0);
    assert.throws(
      () => assertOptimizerDisabled(response, method),
      {
        code: 'ERR_ASSERTION',
        actual: 200,
        expected: 404,
        message: /^Image optimizer must remain disabled/,
      },
      'Disabled-optimizer assertion did not reject the enabled configuration',
    );
  }
  await observe(optimizerMutant);
  assert.equal(diagnosticsLeaked(), false, 'Private material entered optimizer diagnostics');
  await stopTestChild(optimizerMutant);
  writeFileSync(configPath, disabledConfig);

  // Development gets a fresh owned build directory.
  rmSync(join(app, '.next'), { recursive: true, force: true });
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
      rejectUnauthorized: true,
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
  await observe(development);
  assert.equal(diagnosticsLeaked(), false, 'Private material entered development diagnostics');
  console.warn(
    'Native ingress: TLS, GET/HEAD admission, disabled image optimizer with configuration mutant, query redirects, production closure, DEBUG containment and development HMR passed.',
  );
}

async function preflight(port) {
  const probe = owner.ownServer(createPortProbe());
  await new Promise((resolve, reject) => {
    probe.once('error', () => reject(new Error('Fixture port ' + port + ' is already in use')));
    probe.listen({ host: '127.0.0.1', port, exclusive: true }, resolve);
  });
  await new Promise((resolve, reject) =>
    probe.close((error) => (error ? reject(error) : resolve())),
  );
}

async function checkpoint(stage) {
  if (!process.connected) return;
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      process.off('message', acknowledge);
      reject(new Error('Fixture control barrier deadline'));
    }, 5000);
    function acknowledge(message) {
      if (message?.kind !== 'continue' || message.stage !== stage) return;
      clearTimeout(timer);
      process.off('message', acknowledge);
      resolve();
    }
    process.on('message', acknowledge);
    process.send(
      {
        kind: 'checkpoint',
        stage,
        fixtureRoot,
        children: children.map((child) => child.pid),
      },
      (error) => {
        if (!error) return;
        clearTimeout(timer);
        process.off('message', acknowledge);
        reject(new Error('Fixture control channel failed'));
      },
    );
  });
  owner.assertActive();
}
