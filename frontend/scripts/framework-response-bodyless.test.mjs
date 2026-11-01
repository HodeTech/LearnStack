// Finite qualified installed-response control. Never changes installed Next bytes.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, realpathSync, rmSync, mkdtempSync } from 'node:fs';
import { createServer as createHttpServer, request as httpRequest } from 'node:http';
import { createServer as createHttpsServer, request as httpsRequest } from 'node:https';
import { createRequire, Module } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { gunzipSync, inflateSync } from 'node:zlib';

import { loadQualifiedResponseSources } from './next-response-patch/control-sources.mjs';
import { protectPageResponse } from '../apps/web/src/server/auth/page-response.ts';
import { joinResponseCompression } from '../framework/next-response-cleanup/owner.mjs';

const frontendRoot = fileURLToPath(new URL('../', import.meta.url));
const appRoot = join(frontendRoot, 'apps/web');
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const payload = Buffer.alloc(8192, 65);
const shortBody = Buffer.from('small');
let require;
let nextRoot;
let compression;
let pipe;
let installed;
try {
  require = createRequire(join(appRoot, 'package.json'));
  nextRoot = dirname(require.resolve('next/package.json'));
  const records = await loadQualifiedResponseSources({ frontendRoot });
  compression = records.find(
    (row) => row.kind === 'compression' && row.path === 'dist/compiled/compression/index.js',
  );
  pipe = records.find((row) => row.kind === 'pipe' && row.path === 'dist/server/pipe-readable.js');
  assert.ok(compression && pipe, 'exact qualified common compression and CJS pipe');
  installed = [
    join(nextRoot, compression.path),
    join(nextRoot, pipe.path),
    join(frontendRoot, 'framework/next-response-cleanup/census.json'),
    join(frontendRoot, 'framework/next-response-cleanup/owner.mjs'),
    join(appRoot, 'src/server/auth/page-response.ts'),
  ].map((path) => ({ path, digest: sha(readFileSync(path)) }));
} catch {
  process.stderr.write('Framework bodyless proof failed: installation\n');
  process.exit(1);
}

const CASES = Object.freeze([
  ...[204, 304].flatMap((status) => [
    { name: `${status}-empty-direct`, status },
    { name: `${status}-empty-direct-early-gzip`, status, flush: true, encoding: 'gzip' },
    {
      name: `${status}-empty-stream-early-gzip`,
      status,
      flush: true,
      stream: true,
      encoding: 'gzip',
    },
    { name: `${status}-empty-direct-early-known-zero`, status, flush: true, length: '0' },
    {
      name: `${status}-empty-direct-early-deflate`,
      status,
      flush: true,
      accept: 'deflate',
      encoding: 'deflate',
    },
    {
      name: `${status}-empty-stream-early-held-peer-close`,
      status,
      flush: true,
      stream: true,
      encoding: 'gzip',
      holdEnd: true,
      peerClose: true,
    },
  ]),
  { name: 'head-empty-direct-early', method: 'HEAD', flush: true },
  { name: 'head-payload-stream-early', method: 'HEAD', flush: true, stream: true, body: 'large' },
  { name: '200-empty-direct' },
  { name: '200-empty-direct-early-gzip', flush: true, encoding: 'gzip' },
  { name: '200-empty-stream', stream: true },
  { name: '200-empty-stream-early-gzip', stream: true, flush: true, encoding: 'gzip' },
  { name: '200-large-direct-gzip', body: 'large', encoding: 'gzip' },
  { name: '200-large-stream-gzip', body: 'large', stream: true, encoding: 'gzip' },
  { name: '200-large-direct-deflate', body: 'large', accept: 'deflate', encoding: 'deflate' },
  {
    name: '200-large-stream-deflate',
    body: 'large',
    stream: true,
    accept: 'deflate',
    encoding: 'deflate',
  },
  { name: '200-large-direct-identity', body: 'large', accept: 'identity' },
  { name: '200-large-png', body: 'large', contentType: 'image/png' },
  { name: '200-small-direct', body: 'small' },
]);
const FENCE = Object.freeze([
  'flushHeaders',
  'end-empty',
  'writeHead',
  'write',
  'end-payload',
  'write-then-end',
]);
const HEADER_KEYS = Object.freeze([
  'cache-control',
  'content-encoding',
  'content-length',
  'content-type',
  'transfer-encoding',
  'vary',
]);
const ownerKey = Symbol.for('learnstack.next.responseCompressionOwner');
let failurePhase = 'setup';
let failureCredits = 0;

function load(record, variant, zlibObserver) {
  const filename = join(nextRoot, record.path);
  const copy = new Module(filename);
  copy.filename = filename;
  copy.paths = Module._nodeModulePaths(dirname(filename));
  const realRequire = createRequire(filename);
  copy.require = (name) => (name === 'zlib' && zlibObserver ? zlibObserver : realRequire(name));
  copy._compile(record[variant], filename);
  return copy.exports;
}
function deferred() {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
async function observe(predicate, label) {
  const until = Date.now() + 3000;
  while (!predicate()) {
    if (Date.now() >= until) throw new Error('Bodyless control did not observe ' + label);
    await delay(2);
  }
}
function selectedHeaders(headers) {
  return Object.fromEntries(HEADER_KEYS.map((name) => [name, headers[name] ?? null]));
}
function expectedBody(config) {
  if (config.body === 'large') return payload;
  if (config.body === 'small') return shortBody;
  return Buffer.alloc(0);
}
function checkedWire(config, wire) {
  if ([204, 304].includes(config.status) || config.method === 'HEAD') {
    assert.equal(wire.length, 0, 'bodyless wire stays empty');
    return;
  }
  const decoded =
    config.encoding === 'gzip'
      ? gunzipSync(wire)
      : config.encoding === 'deflate'
        ? inflateSync(wire)
        : wire;
  assert.deepEqual(decoded, expectedBody(config), 'healthy decoded body is unchanged');
}
function recordStream(stream, events) {
  const row = { stream, events: [] };
  events.push({ kind: 'allocation', value: stream.constructor.name });
  for (const kind of ['finish', 'end', 'close', 'error'])
    stream.on(kind, () => {
      row.events.push(kind);
      events.push({ kind: 'encoder-' + kind });
    });
  return row;
}

async function oneCase({ transport, variant, config, cert, key, fence = false }) {
  failurePhase = 'setup';
  failureCredits = 0;
  const name = fence ? 'fence-' + config : config.name;
  const row = { transport, variant, name, fence, events: [] };
  const streams = [];
  const sockets = new Set();
  const wire = [];
  const end = deferred();
  if (!config.holdEnd) end.resolve();
  let response;
  let client;
  let peer;
  let handler;
  let handlerState = 'pending';
  let nativeCredits = 0;
  let live = true;
  let requestDone = false;
  let requestError;
  let peerResponse;
  const nativeZlib = require('node:zlib');
  const observedZlib = new Proxy(nativeZlib, {
    get(target, property, receiver) {
      if (property === 'createGzip' || property === 'createDeflate')
        return (...args) => {
          const stream = Reflect.apply(target[property], target, args);
          streams.push(recordStream(stream, row.events));
          return stream;
        };
      return Reflect.get(target, property, receiver);
    },
  });
  const middleware = load(compression, variant, observedZlib);
  const pipeToNodeResponse = load(pipe, variant).pipeToNodeResponse;
  assert.equal(typeof pipeToNodeResponse, 'function');
  const transportApi =
    transport === 'https'
      ? { createServer: createHttpsServer, request: httpsRequest }
      : { createServer: createHttpServer, request: httpRequest };
  const server = transportApi.createServer(
    transport === 'https' ? { cert, key } : {},
    (request, res) => {
      try {
        response = res;
        res.once('finish', () => row.events.push({ kind: 'response-finish' }));
        res.once('close', () => row.events.push({ kind: 'response-close' }));
        res.on('error', () => row.events.push({ kind: 'response-error' }));
        protectPageResponse(
          res,
          () => {
            row.events.push({ kind: 'native-check' });
            if (!live) throw new Error('Private bodyless fence invalidated');
          },
          () => {},
          () => {
            nativeCredits++;
            return () => {
              nativeCredits--;
            };
          },
        );
        middleware()(request, res, () => {});
        if (variant === 'patched')
          assert.ok(
            Object.isFrozen(res[ownerKey]),
            'actual patched middleware installed its owner',
          );
        else assert.equal(res[ownerKey], undefined, 'reconstructed original has no owner');
        res.statusCode = fence ? 200 : (config.status ?? 200);
        res.setHeader('Content-Type', config.contentType ?? 'text/plain');
        res.setHeader('Cache-Control', 'no-store');
        res.setHeader('Date', 'Sun, 01 Nov 2026 00:00:00 GMT');
        if (!fence && config.length) res.setHeader('Content-Length', config.length);
        if (fence) {
          live = false;
          row.events.push({ kind: 'native-invalidated' });
        }
        const operation = () => {
          if (fence) {
            switch (config) {
              case 'flushHeaders':
                res.flushHeaders();
                break;
              case 'end-empty':
                res.end();
                break;
              case 'writeHead':
                res.writeHead(200);
                break;
              case 'write':
                res.write(payload);
                break;
              case 'end-payload':
                res.end(payload);
                break;
              case 'write-then-end':
                res.write(payload);
                res.end();
                break;
              default:
                throw new Error('Unknown bodyless fence operation');
            }
            return;
          }
          if (config.flush) res.flushHeaders();
          if (!config.stream) {
            res.end(config.body ? expectedBody(config) : undefined);
            return;
          }
          const readable = new ReadableStream({
            start(controller) {
              if (config.body) controller.enqueue(expectedBody(config));
              controller.close();
            },
          });
          return pipeToNodeResponse(readable, res, config.holdEnd ? end.promise : undefined);
        };
        handler =
          variant === 'patched'
            ? joinResponseCompression(res, operation)
            : Promise.resolve().then(operation);
        handler.then(
          () => {
            handlerState = 'fulfilled';
            row.encoderClosedAtHandler = streams.every((item) => item.events.includes('close'));
            row.events.push({ kind: 'handler-ok' });
          },
          () => {
            handlerState = 'rejected';
            row.events.push({ kind: 'handler-failed' });
          },
        );
      } catch {
        row.events.push({ kind: 'server-callback-failed' });
        res.destroy();
      }
    },
  );
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    client = transportApi.request(
      {
        hostname: '127.0.0.1',
        port: server.address().port,
        method: fence ? 'GET' : (config.method ?? 'GET'),
        path: '/',
        agent: false,
        ...(transport === 'https' ? { servername: 'localhost', ca: cert } : {}),
        headers: { 'Accept-Encoding': fence ? 'gzip' : (config.accept ?? 'gzip') },
      },
      (incoming) => {
        peer = incoming;
        peerResponse = {
          status: incoming.statusCode,
          headers: selectedHeaders(incoming.headers),
          allHeaders: incoming.headers,
        };
        if (transport === 'https') row.authorized = incoming.socket.authorized;
        incoming.on('data', (chunk) => wire.push(chunk));
        incoming.once('end', () => {
          requestDone = true;
          row.events.push({ kind: 'peer-end' });
        });
        incoming.once('error', (error) => {
          requestError = error;
        });
      },
    );
    client.setTimeout(3000, () => client.destroy(new Error('Private bodyless request deadline')));
    client.once('error', (error) => {
      requestError = error;
    });
    client.end();
    failurePhase = 'peer-end';
    await observe(() => requestDone || requestError, 'peer end');
    if (requestError) throw requestError;
    if (config.peerClose) {
      peer.destroy();
      client.destroy();
      await observe(
        () => row.events.some((item) => item.kind === 'response-close'),
        'real peer close',
      );
      assert.equal(handlerState, 'pending', 'held end work stays pending after response close');
      if (variant === 'patched')
        await observe(() => streams[0]?.events.includes('close'), 'patched real encoder close');
      else assert.equal(streams[0]?.events.includes('close'), false);
      row.heldBeforeRelease = true;
      end.resolve();
    }
    failurePhase = 'handler-settlement';
    if (variant === 'patched' || !config.peerClose)
      await observe(() => handlerState !== 'pending', 'real handler settlement');
    failurePhase = 'response-close';
    await observe(
      () => row.events.some((item) => item.kind === 'response-close'),
      'actual response close',
    );
    if (variant === 'original' && config.peerClose) {
      await delay(30);
      assert.equal(handlerState, 'pending', 'stock held pipe remains dirty');
      assert.equal(streams[0]?.events.includes('close'), false);
      row.stockRetained = true;
    } else {
      assert.equal(handlerState, 'fulfilled', 'real handler succeeds');
      await handler;
    }
    failurePhase = 'status';
    assert.equal(peerResponse.status, fence ? 503 : (config.status ?? 200));
    failurePhase = 'selection';
    assert.equal(
      peerResponse.headers['content-encoding'],
      fence ? null : (config.encoding ?? null),
    );
    assert.equal(
      streams.length,
      fence ? (['flushHeaders', 'end-empty'].includes(config) ? 0 : 1) : config.encoding ? 1 : 0,
    );
    const bytes = Buffer.concat(wire);
    failurePhase = 'wire';
    if (fence) {
      assert.equal(bytes.length, 0);
      assert.equal(peerResponse.headers['cache-control'], 'no-store');
      assert.equal(peerResponse.headers['content-length'], '0');
      assert.equal(peerResponse.headers['content-type'], null);
      assert.equal(peerResponse.headers.vary, null);
      const allocationAt = row.events.findIndex((item) => item.kind === 'allocation');
      const finishAt = row.events.findIndex((item) => item.kind === 'response-finish');
      const closeAt = row.events.findIndex((item) => item.kind === 'response-close');
      assert.ok(finishAt >= 0 && closeAt > finishAt, 'real finish then close');
      if (streams.length) {
        assert.ok(allocationAt >= 0 && allocationAt < finishAt);
        const nativeCheckAt = row.events.findIndex((item) => item.kind === 'native-check');
        assert.ok(allocationAt < nativeCheckAt, 'real allocation precedes failed native check');
        if (variant === 'original') {
          assert.equal(streams[0].events.includes('close'), false);
          row.stockRetained = true;
        } else {
          assert.equal(streams[0].events.includes('close'), true);
          assert.equal(
            streams[0].events.includes('end'),
            false,
            'fenced encoder never readable-ended',
          );
        }
      }
    } else checkedWire(config, bytes);
    failurePhase = 'owner-sealed';
    if (variant === 'patched') {
      const owner = response?.[ownerKey];
      assert.ok(owner, 'patched middleware retains its exact owner');
      if (!streams.length)
        assert.equal(owner.allocationClosed(), true, 'no-resource selection seals allocation');
      if (streams.length) {
        failurePhase = 'encoder-closed';
        assert.equal(
          streams[0].events.includes('close'),
          true,
          'join waits for actual encoder close',
        );
        failurePhase = 'encoder-close-before-handler';
        assert.equal(row.encoderClosedAtHandler, true, 'handler cannot precede encoder close');
      }
      failurePhase = 'late-allocation-refused';
      let lateFactoryEntered = false;
      assert.throws(() =>
        owner.createCompressor(() => {
          lateFactoryEntered = true;
          throw new Error('Unexpected late compressor factory entry');
        }),
      );
      assert.equal(lateFactoryEntered, false, 'joined owner refuses late resource creation');
    }
    failurePhase = 'native-credit';
    failureCredits = nativeCredits;
    assert.equal(nativeCredits, 0);
    failurePhase = 'tls';
    if (transport === 'https') assert.equal(row.authorized, true);
    row.wire = bytes;
    row.headers = peerResponse.headers;
    row.allHeaders = peerResponse.allHeaders;
    row.status = peerResponse.status;
    row.encoderCount = streams.length;
    row.encoderClosedBeforeDisposal = streams.every((item) => item.events.includes('close'));
    row.handler = handlerState;
    row.passedBeforeDisposal = true;
    return row;
  } finally {
    // Diagnostic disposal is never a positive resource/handler assertion.
    end.resolve();
    peer?.destroy();
    client?.destroy();
    response?.destroy();
    const closed = new Promise((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
    for (const socket of sockets) socket.destroy();
    await closed;
    await observe(() => sockets.size === 0, 'owned sockets retired');
    for (const item of streams) if (!item.stream.destroyed) item.stream.destroy();
    await observe(
      () => streams.every((item) => item.events.includes('close')),
      'diagnostic encoder close',
    );
  }
}

const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-response-bodyless-')));
const keyPath = join(root, 'key.pem');
const certPath = join(root, 'cert.pem');
const rows = [];
let complete = false;
let stage = 'certificate';
try {
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      keyPath,
      '-out',
      certPath,
      '-subj',
      '/CN=localhost',
      '-days',
      '1',
      '-addext',
      'subjectAltName=DNS:localhost',
    ],
    { timeout: 5000, stdio: 'ignore' },
  );
  const cert = readFileSync(certPath);
  const key = readFileSync(keyPath);
  for (const transport of ['http', 'https']) {
    for (const config of CASES)
      for (const variant of ['original', 'patched']) {
        stage = transport + '-' + config.name + '-' + variant;
        rows.push(await oneCase({ transport, variant, config, cert, key }));
      }
    for (const operation of FENCE)
      for (const variant of ['original', 'patched']) {
        stage = transport + '-fence-' + operation + '-' + variant;
        rows.push(await oneCase({ transport, variant, config: operation, cert, key, fence: true }));
      }
  }
  stage = 'pair-comparison';
  for (const transport of ['http', 'https'])
    for (const name of [
      ...CASES.map((item) => item.name),
      ...FENCE.map((item) => 'fence-' + item),
    ]) {
      const pair = rows.filter((row) => row.transport === transport && row.name === name);
      assert.equal(pair.length, 2, 'original and patched pair exists');
      assert.deepEqual(pair[0].headers, pair[1].headers, 'healthy/refusal headers unchanged');
      assert.deepEqual(pair[0].allHeaders, pair[1].allHeaders, 'complete HTTP headers unchanged');
      assert.deepEqual(pair[0].wire, pair[1].wire, 'healthy/refusal wire unchanged');
    }
  assert.ok(installed.every(({ path, digest }) => sha(readFileSync(path)) === digest));
  assert.equal(rows.length, 124, 'closed case and transport population');
  const summary = {
    kind: 'qualified-response-bodyless',
    transports: 2,
    variants: 2,
    normalCases: CASES.length,
    fenceCases: FENCE.length,
    total: rows.length,
    stockRetained: rows.filter((row) => row.variant === 'original' && row.stockRetained).length,
    patchedJoined: rows
      .filter((row) => row.variant === 'patched')
      .every(
        (row) =>
          row.passedBeforeDisposal &&
          row.encoderClosedBeforeDisposal &&
          row.handler === 'fulfilled',
      ),
    originalPatchedWireEqual: true,
    installedUnchanged: true,
  };
  assert.ok(summary.stockRetained > 0);
  assert.equal(summary.patchedJoined, true);
  complete = true;
  process.stdout.write(JSON.stringify(summary) + '\n');
} catch {
  // Only fixed case names and a finite failure phase leave this runner.
  process.stderr.write(
    'Framework bodyless proof failed: ' +
      stage +
      ':' +
      failurePhase +
      (failurePhase === 'native-credit' ? ':' + Math.min(256, failureCredits) : '') +
      '\n',
  );
  process.exitCode = 1;
} finally {
  // A failed physical join or uncertain case retains its private tree.
  if (complete) rmSync(root, { recursive: true, force: true });
}
