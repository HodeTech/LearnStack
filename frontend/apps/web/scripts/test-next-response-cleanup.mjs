// Actual-source controls only. The installed-package verifier and reverse-edit
// hashes supply original/patched bytes; installed bytes are never assumed stock.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createServer, request } from 'node:http';
import { createRequire, Module } from 'node:module';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

import ts from 'typescript';

import { loadQualifiedResponseSources } from '../../../scripts/next-response-patch/control-sources.mjs';
import { transformJavascript } from '../../../scripts/next-response-patch/transform.mjs';
import { protectPageResponse } from '../src/server/auth/page-response.ts';

const frontendRoot = fileURLToPath(new URL('../../../', import.meta.url));
const appRoot = join(frontendRoot, 'apps/web');
const require = createRequire(join(appRoot, 'package.json'));
const nextRoot = dirname(require.resolve('next/package.json'));
const captureKey = Symbol.for('learnstack.next.responseCleanupTestCapture');
const payload = Buffer.alloc(8192, 65);
const records = await loadQualifiedResponseSources({ frontendRoot });
const compressionRecord = records.find((record) => record.kind === 'compression');
assert(compressionRecord);
const RenderResult = require('next/dist/server/render-result.js').default;
const { sendRenderResult } = require('next/dist/server/send-payload.js');

function nodes(root, predicate) {
  const result = [];
  function visit(node) {
    if (predicate(node)) result.push(node);
    ts.forEachChild(node, visit);
  }
  visit(root);
  return result;
}

function parse(code, path) {
  const source = ts.createSourceFile(path, code, ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS);
  assert.equal(source.parseDiagnostics.length, 0);
  return source;
}

function loadCommonJs(code, filename, zlibObserver) {
  const loaded = new Module(filename);
  loaded.filename = filename;
  loaded.paths = Module._nodeModulePaths(dirname(filename));
  const nativeRequire = createRequire(filename);
  loaded.require = (specifier) =>
    specifier === 'zlib' && zlibObserver ? zlibObserver : nativeRequire(specifier);
  loaded._compile(code, filename);
  return loaded.exports;
}

function activatePipe(record, variant) {
  const code = record[variant];
  const source = parse(code, record.path);
  const markers = nodes(
    source,
    (node) => ts.isStringLiteral(node) && node.text === 'failed to pipe response',
  );
  assert.equal(markers.length, 1);
  let pipe = markers[0];
  while (pipe && !(ts.isFunctionDeclaration(pipe) && pipe.parameters.length === 3 && pipe.name))
    pipe = pipe.parent;
  assert(pipe?.name);
  // Expose the actual closure after its declaration; initialize its real module
  // scope rather than rebuilding a function with substituted lexical helpers.
  let instrumented =
    code.slice(0, pipe.end) +
    `;globalThis[Symbol.for('learnstack.next.responseCleanupTestCapture')]=${pipe.name.text};` +
    code.slice(pipe.end);
  let filename = join(nextRoot, record.path);
  if (record.path.startsWith('dist/esm/')) {
    // Next's ESM input is bundled by supported builds, not loaded by Node's CJS
    // package loader. This control adapts module syntax only, preserving the
    // actual pipe body; it does not claim the full build's ESM loader proof.
    instrumented = ts.transpileModule(instrumented, {
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
    }).outputText;
    filename = join(nextRoot, record.path.replace('dist/esm/', 'dist/'));
  }
  assert.equal(globalThis[captureKey], undefined);
  try {
    loadCommonJs(instrumented, filename);
    assert.equal(typeof globalThis[captureKey], 'function');
    return globalThis[captureKey];
  } finally {
    delete globalThis[captureKey];
  }
}

function activateRouterInitializer(record, variant, requestHandlerImpl) {
  const code = record[variant];
  const source = parse(code, record.path);
  const declarations = nodes(
    source,
    (node) =>
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'requestHandler',
  );
  assert.equal(declarations.length, 1);
  const initializer = declarations[0].initializer;
  assert(initializer);
  // Execute the exact qualified initializer with a real installed direct-end
  // producer. A full initialized router remains a separate integration gate.
  return new Function('requestHandlerImpl', `return (${initializer.getText(source)});`)(
    requestHandlerImpl,
  );
}

async function observe(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(message);
    await delay(2);
  }
}

async function runCase({ path, variant, pipe, router, early }) {
  const row = { path, variant, early, kind: router ? 'router-initializer' : 'pipe' };
  const events = [];
  const streams = [];
  const streamEvents = [];
  const chunks = [];
  const sockets = new Set();
  let response,
    client,
    peer,
    actual,
    releaseEnd,
    reachedEnd = false;
  let handler = 'pending',
    credits = 0,
    terminal = false;
  class ActualHeldEnd extends Promise {
    then(fulfilled, rejected) {
      reachedEnd = true;
      return super.then(fulfilled, rejected);
    }
  }
  const heldEnd = new ActualHeldEnd((resolve) => {
    releaseEnd = resolve;
  });
  const realZlib = require('zlib');
  const observedZlib = new Proxy(realZlib, {
    get(target, property, receiver) {
      if (property === 'createGzip' || property === 'createDeflate')
        return (...args) => {
          const stream = Reflect.apply(target[property], target, args);
          const index = streams.length;
          streams.push(stream);
          streamEvents.push([]);
          for (const event of ['finish', 'end', 'close', 'error'])
            stream.on(event, () => streamEvents[index].push(event));
          return stream;
        };
      return Reflect.get(target, property, receiver);
    },
  });
  const compression = loadCommonJs(
    compressionRecord[variant],
    join(nextRoot, compressionRecord.path),
    observedZlib,
  );
  const server = createServer((req, res) => {
    response = res;
    res.once('finish', () => {
      terminal = true;
      events.push('response-finish');
    });
    res.once('close', () => {
      terminal = true;
      events.push('response-close');
    });
    res.on('error', () => events.push('response-error'));
    protectPageResponse(
      res,
      () => {
        if (terminal) throw new Error('Control response already terminal');
      },
      () => {},
      () => {
        credits++;
        return () => {
          credits--;
        };
      },
    );
    compression()(req, res, () => {});
    res.setHeader('Content-Type', 'text/plain');
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Date', 'Sun, 01 Nov 2026 00:00:00 GMT');
    if (router) {
      if (early) {
        const wrappedEnd = res.end;
        res.end = function (...args) {
          const result = Reflect.apply(wrappedEnd, this, args);
          client.destroy();
          return result;
        };
      }
      actual = router(req, res);
    } else {
      const readable = new ReadableStream({
        start(controller) {
          controller.enqueue(payload);
          controller.close();
        },
      });
      actual = pipe(readable, res, early ? heldEnd : undefined);
    }
    actual.then(
      () => {
        handler = 'fulfilled';
        row.encoderClosedAtHandler = streamEvents.every((recorded) => recorded.includes('close'));
      },
      () => {
        handler = 'rejected';
      },
    );
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    client = request(
      {
        hostname: '127.0.0.1',
        port: server.address().port,
        agent: false,
        headers: { 'Accept-Encoding': 'gzip' },
      },
      (res) => {
        peer = res;
        row.status = res.statusCode;
        row.encoding = res.headers['content-encoding'];
        row.headers = res.headers;
        res.on('data', (chunk) => chunks.push(chunk));
        res.once('end', () => events.push('peer-end'));
        res.on('error', () => events.push('peer-error'));
      },
    );
    client.on('error', () => events.push('client-error'));
    client.end();
    if (early && !router) {
      await observe(() => reachedEnd && peer, 'Actual pipe end-work was not reached');
      peer.destroy();
      client.destroy();
      await observe(() => events.includes('response-close'), 'Actual peer close absent');
      if (variant === 'patched')
        await observe(() => streamEvents[0]?.includes('close'), 'Owned encoder close absent');
      assert.equal(handler, 'pending', 'Real end work was released by transport cleanup');
      row.independentEndWorkHeldAfterClose = true;
      releaseEnd();
    }
    await observe(() => events.includes('response-close'), 'Actual response close absent');
    assert.equal(streams.length, 1);
    if (early && variant === 'original') {
      await delay(30);
      assert.equal(handler, router ? 'fulfilled' : 'pending');
      assert.equal(streamEvents[0].includes('close'), false);
      row.negativeStockRetention = true;
    } else {
      await observe(() => handler !== 'pending', 'Actual handler did not settle');
      await actual;
      await observe(() => streamEvents[0].includes('close'), 'Actual encoder close absent');
      assert.equal(handler, 'fulfilled');
      if (variant === 'patched') assert.equal(row.encoderClosedAtHandler, true);
    }
    if (!early) {
      await observe(() => events.includes('peer-end'), 'Actual body end absent');
      const bytes = Buffer.concat(chunks);
      assert.deepEqual(gunzipSync(bytes), payload);
      row.wire = bytes.toString('base64');
      assert.equal(row.status, 200);
      assert.equal(row.encoding, 'gzip');
    } else assert.equal(events.includes('response-finish'), false);
    row.nativeCreditsBeforeDisposal = credits;
    if (!row.negativeStockRetention)
      assert.equal(
        credits,
        0,
        JSON.stringify({ path, variant, early, handler, events, streamEvents, credits }),
      );
    row.handlerBeforeDisposal = handler;
    row.actualEncoderCloseBeforeDisposal = streamEvents[0].includes('close');
  } finally {
    releaseEnd();
    peer?.destroy();
    client?.destroy();
    response?.destroy();
    const closed = new Promise((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
    });
    for (const socket of sockets) socket.destroy();
    await closed;
    await observe(() => sockets.size === 0, 'Owned control sockets did not close');
    for (const stream of streams) if (!stream.destroyed) stream.destroy();
    await observe(
      () => streamEvents.every((recorded) => recorded.includes('close')),
      'Diagnostic resource disposal did not close actual encoder',
    );
    row.handlerAfterDisposal = handler;
    row.nativeCreditsAfterDisposal = credits;
  }
  return row;
}

// Exercise the branch where writer.close has not begun. A real pending stream
// read is the explicit end task; response/encoder close cannot release it.
async function runEndWorkCase({ path, variant, pipe, scenario }) {
  const row = { path, variant, scenario };
  const events = [],
    streams = [],
    sockets = new Set();
  let response, client, peer, bodyController, endController, actual, primary;
  let handler = 'pending',
    endState = 'pending',
    cleanup = 'pending',
    credits = 0;
  const sourceError = new Error('Actual response source failure');
  const endError = new Error('Actual passed end-work failure');
  const reader = new ReadableStream({
    start(controller) {
      endController = controller;
    },
  }).getReader();
  const endWork = reader.read();
  endWork.then(
    () => {
      endState = 'fulfilled';
    },
    () => {
      endState = 'rejected';
    },
  );
  const realZlib = require('zlib');
  const observedZlib = new Proxy(realZlib, {
    get(target, property, receiver) {
      if (property === 'createGzip' || property === 'createDeflate')
        return (...args) => {
          const stream = Reflect.apply(target[property], target, args);
          streams.push(stream);
          stream.on('error', () => events.push('encoder-error'));
          stream.once('close', () => events.push('encoder-close'));
          return stream;
        };
      return Reflect.get(target, property, receiver);
    },
  });
  const compression = loadCommonJs(
    compressionRecord[variant],
    join(nextRoot, compressionRecord.path),
    observedZlib,
  );
  const server = createServer((req, res) => {
    response = res;
    res.on('error', () => events.push('response-error'));
    res.once('close', () => events.push('response-close'));
    protectPageResponse(
      res,
      () => {},
      () => {},
      () => {
        credits++;
        return () => {
          credits--;
        };
      },
    );
    compression()(req, res, () => {});
    res.setHeader('Content-Type', 'text/plain');
    res[Symbol.for('learnstack.next.responseCompressionOwner')]?.join().then(
      () => {
        cleanup = 'fulfilled';
      },
      () => {
        cleanup = 'rejected';
      },
    );
    const readable = new ReadableStream({
      start(controller) {
        bodyController = controller;
        if (scenario === 'source-before-selection') controller.error(sourceError);
        else if (scenario !== 'already-terminal') controller.enqueue(payload);
      },
      cancel() {
        events.push('actual-source-cancel');
      },
    });
    if (scenario === 'already-terminal') res.destroy();
    actual = pipe(readable, res, endWork);
    actual.then(
      () => {
        handler = 'fulfilled';
        row.encoderClosedAtHandler = streams.every((stream) => stream.closed);
      },
      (error) => {
        handler = 'rejected';
        primary = error;
        row.encoderClosedAtHandler = streams.every((stream) => stream.closed);
      },
    );
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    client = request(
      {
        hostname: '127.0.0.1',
        port: server.address().port,
        agent: false,
        headers: { 'Accept-Encoding': 'gzip' },
      },
      (res) => {
        peer = res;
        res.on('error', () => events.push('peer-error'));
        res.once('data', () => {
          events.push('actual-peer-data');
          if (scenario === 'peer-before-end') {
            res.destroy();
            client.destroy();
          } else if (scenario === 'end-reject-after-source-close') bodyController.close();
          else bodyController.error(sourceError);
        });
        res.resume();
      },
    );
    client.on('error', () => events.push('client-error'));
    client.end();
    if (scenario === 'end-reject-after-source-close') {
      await observe(() => events.includes('actual-peer-data'), 'Actual source did not write');
      await delay(2);
      assert.equal(handler, 'pending');
    } else {
      await observe(() => events.includes('response-close'), 'Actual response did not close');
      if (variant === 'patched')
        await observe(() => cleanup === 'fulfilled', 'Physical owner did not retire independently');
      else await observe(() => handler !== 'pending', 'Original early branch did not settle');
      assert.equal(
        handler,
        variant === 'patched'
          ? 'pending'
          : scenario === 'peer-before-end' || scenario === 'already-terminal'
            ? 'fulfilled'
            : 'rejected',
      );
    }
    assert.equal(endState, 'pending');
    assert.equal(credits, 0);
    const expectedStreams =
      scenario === 'source-before-selection' || scenario === 'already-terminal' ? 0 : 1;
    assert.equal(streams.length, expectedStreams);
    row.beforeEndSettlement = {
      handler,
      endState,
      cleanup,
      credits,
      actualEncoderClosed: streams.every((stream) => stream.closed),
    };
    row.originalEndWorkEscape =
      variant === 'original' && scenario !== 'end-reject-after-source-close';
    const rejectEnd =
      scenario === 'source-and-end-reject' || scenario === 'end-reject-after-source-close';
    if (rejectEnd) endController.error(endError);
    else endController.close();
    await observe(() => handler !== 'pending', 'Handler did not settle after its actual end task');
    if (variant === 'patched') {
      await observe(() => cleanup === 'fulfilled', 'Physical cleanup did not settle');
      assert.equal(row.encoderClosedAtHandler, true);
      if (rejectEnd)
        assert.equal(
          response[Symbol.for('learnstack.next.responseCompressionOwner')].failureState()
            .endFailure,
          endError,
        );
    }
    if (scenario === 'peer-before-end' || scenario === 'already-terminal')
      assert.equal(handler, 'fulfilled');
    else {
      assert.equal(handler, 'rejected');
      assert.equal(
        primary?.cause,
        scenario === 'end-reject-after-source-close' ? endError : sourceError,
      );
      assert.equal(primary?.__NEXT_ERROR_CODE, 'E180');
      row.originalCauseIdentityPreserved = true;
    }
    if (variant === 'original' && scenario === 'end-reject-after-source-close')
      assert.equal(events.includes('response-close'), false);
    else
      await observe(() => events.includes('response-close'), 'Final actual response close absent');
    assert.equal(credits, 0);
    row.afterEndSettlement = {
      handler,
      endState,
      cleanup,
      credits,
      actualEncoderClosed: streams.every((stream) => stream.closed),
      actualResponseClosed: events.includes('response-close'),
    };
  } finally {
    if (endState === 'pending') endController.close();
    peer?.destroy();
    client?.destroy();
    response?.destroy();
    const closed = new Promise((resolve) => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
    for (const stream of streams) if (!stream.destroyed) stream.destroy();
    await observe(
      () => streams.every((stream) => stream.closed),
      'Diagnostic encoder disposal did not close',
    );
    await observe(() => sockets.size === 0, 'Owned end-work control socket did not close');
    reader.releaseLock();
  }
  return row;
}

const results = [];
const tick = () => new Promise((resolve) => setImmediate(resolve));
const observeDrain = (predicate) => observe(predicate, 'Actual drain control did not settle');
async function runDrainCase(pipe, kind) {
  const realZlib = require('zlib');
  const events = [],
    writes = [],
    streams = [],
    sockets = new Set();
  const heldSource = kind === 'held-source';
  const heldEnd = kind === 'held-end' || kind === 'held-end-error';
  const refusal = kind !== 'healthy' && kind !== 'healthy-backpressure';
  const chunkSize = kind === 'healthy-backpressure' ? 512 * 1024 : 8192;
  const first = Buffer.alloc(chunkSize, 65),
    second = Buffer.alloc(chunkSize, 66),
    third = Buffer.alloc(chunkSize, 67);
  const failure = new Error('owned source or end failure');
  let controller, endController, actual, response, peer, client, actualError;
  let invalidated = false,
    state = 'unissued',
    endState = heldEnd ? 'pending' : 'not-held';
  let nativeCredits = 0,
    nativeIssued = 0,
    nativeCompleted = 0;
  const endReader = heldEnd
    ? new ReadableStream({
        start(value) {
          endController = value;
        },
      }).getReader()
    : null;
  const endWork = endReader?.read();
  if (endWork)
    void endWork.then(
      () => {
        endState = 'fulfilled';
      },
      () => {
        endState = 'rejected';
      },
    );
  const observedZlib = new Proxy(realZlib, {
    get(target, property, receiver) {
      if (property === 'createGzip')
        return (...args) => {
          const stream = Reflect.apply(target[property], target, args);
          streams.push(stream);
          events.push('encoder-allocated');
          stream.once('drain', () => events.push('actual-encoder-drain'));
          stream.once('close', () =>
            events.push(stream.writableEnded ? 'encoder-close-ended' : 'encoder-close-unended'),
          );
          if (refusal) invalidated = true;
          return stream;
        };
      return Reflect.get(target, property, receiver);
    },
  });
  const compress = loadCommonJs(
    compressionRecord.patched,
    join(nextRoot, compressionRecord.path),
    observedZlib,
  );
  const server = createServer((req, res) => {
    response = res;
    res.once('finish', () => events.push('response-finish'));
    res.once('close', () => {
      events.push('response-close');
      if (refusal)
        setImmediate(() => {
          try {
            controller.enqueue(second);
            controller.enqueue(third);
            events.push('two-source-chunks-after-close');
            if (kind === 'source-error') controller.error(failure);
            else if (!heldSource) controller.close();
          } catch {
            events.push('source-operation-refused');
          }
        });
    });
    protectPageResponse(
      res,
      () => {
        if (invalidated) throw new Error('owned invalidation');
      },
      () => {},
      () => {
        nativeCredits++;
        nativeIssued++;
        let completed = false;
        return () => {
          assert.equal(completed, false);
          completed = true;
          nativeCredits--;
          nativeCompleted++;
        };
      },
    );
    compress({ threshold: 0 })(req, res, () => {});
    res.setHeader('Content-Type', 'text/plain');
    const write = res.write;
    res.write = function (...args) {
      const result = Reflect.apply(write, this, args);
      writes.push({ returned: result, afterClose: events.includes('response-close') });
      return result;
    };
    const readable = new ReadableStream({
      start(value) {
        controller = value;
        controller.enqueue(first);
        if (!refusal) {
          controller.enqueue(second);
          controller.close();
        }
      },
      cancel() {
        events.push('source-cancel');
      },
    });
    state = 'pending';
    actual = pipe(readable, res, endWork);
    actual.then(
      () => {
        state = 'fulfilled';
      },
      (error) => {
        state = 'rejected';
        actualError = error;
      },
    );
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const wire = new Promise((resolve, reject) => {
    client = request(
      {
        hostname: '127.0.0.1',
        port: server.address().port,
        path: '/',
        agent: false,
        headers: { 'accept-encoding': 'gzip' },
      },
      (res) => {
        peer = res;
        const chunks = [];
        res.on('data', (chunk) => chunks.push(chunk));
        res.once('error', reject);
        res.once('end', () =>
          resolve({
            status: res.statusCode,
            encoding: res.headers['content-encoding'] ?? null,
            cache: res.headers['cache-control'] ?? null,
            body: Buffer.concat(chunks),
          }),
        );
      },
    );
    client.once('error', reject);
    client.end();
  });
  try {
    const result = await wire;
    await observeDrain(
      () => events.includes('response-close') && streams.length === 1 && streams[0].closed,
    );
    if (refusal) {
      assert.equal(result.status, 503);
      assert.equal(result.encoding, null);
      assert.equal(result.cache, 'no-store');
      assert.equal(result.body.length, 0);
      assert.ok(events.includes('encoder-close-unended'));
      await observeDrain(
        () =>
          events.includes('two-source-chunks-after-close') ||
          events.includes('source-operation-refused'),
      );
      assert.ok(events.includes('two-source-chunks-after-close'));
      if (heldSource || heldEnd) {
        for (let index = 0; index < 5; index++) await tick();
        assert.equal(
          state,
          'pending',
          'actual held work cannot be retired by response/encoder close',
        );
        if (heldSource) controller.close();
        else if (kind === 'held-end-error') endController.error(failure);
        else endController.close();
      }
    } else {
      assert.equal(result.status, 200);
      assert.equal(result.encoding, 'gzip');
      assert.deepEqual(gunzipSync(result.body), Buffer.concat([first, second]));
    }
    await observeDrain(() => state !== 'pending');
    if (kind === 'source-error' || kind === 'held-end-error') {
      assert.equal(state, 'rejected');
      assert.equal(actualError?.cause, failure);
      if (kind === 'held-end-error')
        assert.equal(
          response[Symbol.for('learnstack.next.responseCompressionOwner')].failureState()
            .endFailure,
          failure,
        );
    } else assert.equal(state, 'fulfilled');
    if (heldEnd) assert.equal(endState, kind === 'held-end-error' ? 'rejected' : 'fulfilled');
    await observeDrain(() => nativeCredits === 0);
    assert.equal(nativeCompleted, nativeIssued);
    if (!refusal)
      assert.ok(nativeIssued > 0, 'healthy real native writes have actual callback owners');
    if (refusal && kind !== 'source-error') {
      assert.equal(writes.filter((entry) => !entry.returned && entry.afterClose).length, 2);
      assert.equal(writes[0]?.returned, true);
    }
    if (kind === 'healthy-backpressure') {
      assert.ok(writes.some((entry) => !entry.returned && !entry.afterClose));
      assert.ok(events.includes('actual-encoder-drain'));
    }
    return {
      kind,
      status: result.status,
      handler: state,
      encoderClosed: streams[0].closed,
      sourceWasHeld: heldSource,
      endWasHeld: heldEnd,
      errorIdentity: kind === 'source-error' || kind === 'held-end-error',
      nativeIssued,
      nativeCompleted,
    };
  } finally {
    if (heldSource && state === 'pending') {
      try {
        controller.close();
      } catch {
        /* The actual source may already be terminal. */
      }
    }
    if (heldEnd && endState === 'pending') {
      try {
        endController.close();
      } catch {
        /* The actual end source may already be terminal. */
      }
    }
    peer?.destroy();
    client?.destroy();
    response?.destroy();
    const closed = new Promise((resolve) => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
    for (const stream of streams) if (!stream.destroyed) stream.destroy();
    await observeDrain(() => streams.every((stream) => stream.closed) && sockets.size === 0);
    endReader?.releaseLock();
  }
}

// Exact qualified originals prove both parser shapes, and each dirty control
// must refuse rather than silently applying a broader source rewrite.
function drainShapeControls() {
  const ownerSource = readFileSync(
    join(frontendRoot, 'framework/next-response-cleanup/owner.mjs'),
    'utf8',
  );
  const selected = [
    'dist/server/pipe-readable.js',
    'dist/compiled/next-server/pages-api.runtime.prod.js',
  ];
  const transform = (code, path) =>
    transformJavascript({ code, filename: path, kind: 'pipe', ownerSource });
  for (const path of selected) {
    const record = pipes.find((item) => item.path === path);
    assert.ok(record);
    const edits = transform(record.original, path);
    assert.ok(
      edits.some((item) => item.text.includes('__learnstackNextCleanupResponseClosed=true;')),
    );
    assert.ok(
      edits.some((item) =>
        item.text.includes('__learnstackNextCleanupResponseClosed?void 0:await'),
      ),
    );
  }
  const record = pipes.find((item) => item.path === selected[0]);
  assert.ok(record);
  for (const [before, after] of [
    ["res.once('close'", "res.once('error'"],
    ['await drained.promise', 'await other.promise'],
    ['if (!ok)', 'if (ok)'],
    ["res.off('drain', onDrain)", "res.off('drain', otherListener)"],
  ]) {
    assert.equal(record.original.split(before).length, 2, 'unique dirty control');
    assert.throws(() => transform(record.original.replace(before, after), record.path));
  }
  return selected.length + 4;
}

const endWorkResults = [];
const drainResults = [];
const pipes = records.filter((record) => record.kind === 'pipe');
const routers = records.filter((record) => record.kind === 'router');
assert.equal(pipes.length, 27);
assert.equal(routers.length, 2);
const drainShapeCases = drainShapeControls();
assert.equal(drainShapeCases, 6);
for (const record of pipes) {
  for (const variant of ['original', 'patched']) {
    const pipe = activatePipe(record, variant);
    for (const early of [false, true])
      results.push(await runCase({ path: record.path, variant, pipe, early }));
    if (variant === 'patched') {
      for (const kind of [
        'healthy',
        'healthy-backpressure',
        'fence-three',
        'held-source',
        'held-end',
        'source-error',
        'held-end-error',
      ])
        drainResults.push({ path: record.path, ...(await runDrainCase(pipe, kind)) });
    }
    for (const scenario of [
      'source-before-selection',
      'source-after-selection',
      'peer-before-end',
      'already-terminal',
      'source-and-end-reject',
      'end-reject-after-source-close',
    ])
      endWorkResults.push(await runEndWorkCase({ path: record.path, variant, pipe, scenario }));
  }
}
for (const record of routers) {
  const requestHandlerImpl = async (req, res) =>
    sendRenderResult({
      req,
      res,
      result: RenderResult.fromStatic(payload.toString()),
      generateEtags: false,
      poweredByHeader: false,
    });
  for (const variant of ['original', 'patched']) {
    const router = activateRouterInitializer(record, variant, requestHandlerImpl);
    for (const early of [false, true])
      results.push(await runCase({ path: record.path, variant, router, early }));
  }
}
assert.equal(drainResults.length, 189);
for (const record of pipes)
  assert.equal(drainResults.filter((row) => row.path === record.path).length, 7);
for (const record of [...pipes, ...routers]) {
  const healthy = results.filter((row) => row.path === record.path && !row.early);
  assert.equal(healthy.length, 2);
  assert.equal(healthy[0].wire, healthy[1].wire);
  assert.deepEqual(healthy[0].headers, healthy[1].headers);
}
process.stdout.write(
  JSON.stringify({
    kind: 'qualified-next-response-controls',
    cases: results.length + endWorkResults.length,
    actualPipeClosures: pipes.length,
    drainCases: drainResults.length,
    drainCopies: new Set(drainResults.map((row) => row.path)).size,
    drainShapeCases,
    drainRealCloseAndHandlerJoined: drainResults.every(
      (row) => row.encoderClosed && row.handler !== 'pending',
    ),
    exactRouterInitializers: routers.length,
    healthyWireMatches: pipes.length + routers.length,
    negativeStockRetentionControls: results.filter((row) => row.negativeStockRetention).length,
    explicitEndWorkCases: endWorkResults.length,
    originalEndWorkEscapeControls: endWorkResults.filter((row) => row.originalEndWorkEscape).length,
    patchedExplicitEndWorkJoined: endWorkResults
      .filter((row) => row.variant === 'patched')
      .every((row) => row.beforeEndSettlement.handler === 'pending'),
    patchedEndWorkCauseIdentityPreserved: endWorkResults
      .filter((row) => row.variant === 'patched' && row.afterEndSettlement.handler === 'rejected')
      .every((row) => row.originalCauseIdentityPreserved),
    patchedActualCloseBeforeHandler: results
      .filter((row) => row.variant === 'patched')
      .every((row) => row.encoderClosedAtHandler),
    esmModuleSyntaxAdapted: true,
    fullInitializedRouterQualified: false,
  }) + '\n',
);
