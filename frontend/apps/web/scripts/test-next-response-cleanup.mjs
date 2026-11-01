// Actual-source controls only. The installed-package verifier and reverse-edit
// hashes supply original/patched bytes; installed bytes are never assumed stock.
import assert from 'node:assert/strict';
import { createServer, request } from 'node:http';
import { createRequire, Module } from 'node:module';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

import ts from 'typescript';

import { loadQualifiedResponseSources } from '../../../scripts/next-response-patch/control-sources.mjs';
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
const endWorkResults = [];
const pipes = records.filter((record) => record.kind === 'pipe');
const routers = records.filter((record) => record.kind === 'router');
assert.equal(pipes.length, 27);
assert.equal(routers.length, 2);
for (const record of pipes) {
  for (const variant of ['original', 'patched']) {
    const pipe = activatePipe(record, variant);
    for (const early of [false, true])
      results.push(await runCase({ path: record.path, variant, pipe, early }));
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
