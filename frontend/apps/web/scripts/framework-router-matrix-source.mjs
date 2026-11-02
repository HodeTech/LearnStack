// Fixed copied-source transform. Nothing here changes the installed package.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, realpathSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';

import { instrumentGeneratedRouterObserver } from './framework-router-generated-observer-source.mjs';

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
// This literal is part of the statically imported builder source closure.
// The copied route file becomes a separate qualified graph input before build.
export const copiedRouterMatrixRouteSource = `// Copied fixture input only. The fixed public-entry target is /en/courses/transport.
export const dynamic = "force-dynamic";
export const revalidate = 0;

const body = new Uint8Array(8192).fill(65);

export function GET() {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(body.subarray(0, 4096));
      controller.enqueue(body.subarray(4096));
      controller.close();
    },
  });
  return new Response(stream, {
    status: 200,
    headers: {
      "content-type": "text/plain; charset=utf-8",
      "cache-control": "no-store",
    },
  });
}
`;
const replaceOnce = (source, before, after) => {
  assert.equal(source.split(before).length, 2, 'Exact copied router-matrix anchor');
  return source.replace(before, after);
};
function regular(path) {
  assert.equal(realpathSync(path), path, 'Qualified pipe source path');
  const stat = lstatSync(path);
  assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'Qualified pipe source file');
  return readFileSync(path);
}
function unique(values, name) {
  assert.equal(values.length, 1, 'Exact ' + name);
  return values[0];
}
function descendants(ts, root, predicate) {
  const found = [];
  const visit = (node) => {
    if (predicate(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  visit(root);
  return found;
}
function location(file, node) {
  const start = file.getLineAndCharacterOfPosition(node.getStart(file));
  const end = file.getLineAndCharacterOfPosition(node.end);
  return Object.freeze({
    line: start.line,
    column: start.character,
    endLine: end.line,
    endColumn: end.character,
  });
}

/** Bind every patched pipe copy, including compiled prod/dev and shared CJS/ESM. */
export function deriveRouterPipeSpec(identity) {
  assert.ok(Object.isFrozen(identity) && identity.format === 1);
  assert.equal(realpathSync(identity.frontendRoot), identity.frontendRoot);
  assert.equal(realpathSync(identity.nextRoot), identity.nextRoot);
  const censusBytes = regular(
    join(identity.frontendRoot, 'framework/next-response-cleanup/census.json'),
  );
  const census = JSON.parse(censusBytes.toString('utf8'));
  assert.equal(sha(censusBytes), identity.censusSha256);
  assert.deepEqual(census.tuple, identity.tuple);
  const records = census.files.filter((item) => item.kind === 'pipe');
  assert.equal(records.length, 27, 'Complete installed pipe census');
  const ts = createRequire(join(identity.appRoot, 'package.json'))('typescript');
  return Object.freeze(
    records.map((record) => {
      assert.match(
        record.path,
        /^dist\/(?:compiled\/next-server\/|esm\/server\/|server\/)[a-z0-9./-]+\.js$/,
      );
      const bytes = regular(join(identity.nextRoot, record.path));
      assert.equal(bytes.length, record.bytes);
      assert.equal(sha(bytes), record.sha256);
      const file = ts.createSourceFile(
        record.path,
        bytes.toString('utf8'),
        ts.ScriptTarget.Latest,
        false,
        ts.ScriptKind.JS,
      );
      assert.equal(file.parseDiagnostics.length, 0);
      const joined = unique(
        descendants(
          ts,
          file,
          (node) =>
            (ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node)) &&
            node.name?.text === 'joinResponseCompression',
        ),
        'installed pipe join function',
      );
      assert.deepEqual(
        joined.parameters.map((item) => item.name.getText(file)),
        ['response', 'actualOperation', 'explicitEndWork'],
      );
      const key = unique(
        descendants(
          ts,
          joined.body,
          (node) =>
            ts.isVariableDeclaration(node) &&
            node.name.getText(file) === 'key' &&
            !!node.initializer &&
            ts.isCallExpression(node.initializer) &&
            node.initializer.expression.getText(file) === 'Symbol.for' &&
            node.initializer.arguments.length === 1 &&
            ts.isStringLiteral(node.initializer.arguments[0]) &&
            node.initializer.arguments[0].text === 'learnstack.next.responseCompressionOwner',
        ),
        'installed pipe entry key',
      );
      return Object.freeze({
        path: record.path,
        sha256: record.sha256,
        bytes: record.bytes,
        point: location(file, key),
      });
    }),
  );
}

function instrumentRuntime(source) {
  let runtime = replaceOnce(
    source,
    "import { readFileSync } from 'node:fs';",
    "import { closeSync, constants, openSync, readFileSync, writeSync } from 'node:fs';",
  );
  runtime = replaceOnce(
    runtime,
    '  let server;',
    '  let server;\n  let routerHttp;\n  const routerHttpSockets = new Set();',
  );
  runtime = replaceOnce(
    runtime,
    '      if (server?.listening) {',
    `      for (const socket of [...routerHttpSockets])
        ownShutdown(() => new Promise(resolve => {
          const ignoreCancellationError = () => {};
          socket.on('error', ignoreCancellationError);
          socket.once('close', () => {
            socket.removeListener('error', ignoreCancellationError);
            resolve();
          });
          try { socket.destroy(); } catch { failed = true; }
        }));
      if (routerHttp?.listening)
        ownShutdown(() => new Promise((resolve, reject) =>
          routerHttp.close(error => error ? reject(error) : resolve())));
      if (server?.listening) {`,
  );
  runtime = replaceOnce(
    runtime,
    "    server.on('connect', (_request, socket) => socket.destroy());\n    assertStarting();",
    `    server.on('connect', (_request, socket) => socket.destroy());
    assertStarting();
    // Copied fixture only: the same native callback handles one owned HTTP listener.
    routerHttp = createUpgradeSink((request, response) => {
      try {
        if (!server.emit('request', request, response)) throw new Error('Native callback absent');
      } catch {
        failed = true;
        response.destroy();
        control.requestStop('listener-fault');
      }
    });
    routerHttp.on('upgrade', (_request, socket) => socket.destroy());
    routerHttp.on('connect', (_request, socket) => socket.destroy());
    routerHttp.on('connection', socket => {
      routerHttpSockets.add(socket);
      socket.once('close', () => routerHttpSockets.delete(socket));
    });
    routerHttp.on('error', () => {
      failed = true;
      control.requestStop('listener-fault');
    });
    await ownSetup(new Promise((resolve, reject) => {
      const failedListen = () => reject(new Error('Copied HTTP listener failed'));
      routerHttp.once('error', failedListen);
      try {
        routerHttp.listen(0, '127.0.0.1', () => {
          routerHttp.removeListener('error', failedListen);
          try {
            assertStarting();
            const address = routerHttp.address();
            if (!address || typeof address === 'string' ||
                address.address !== '127.0.0.1' || address.family !== 'IPv4' ||
                !Number.isSafeInteger(address.port) || address.port < 1 || address.port > 65535)
              throw new Error('Copied HTTP address refused');
            const portPath = fileURLToPath(new URL('../../../../.router-http.port', import.meta.url));
            const fd = openSync(portPath,
              constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
              0o600);
            try {
              const line = JSON.stringify({version:1,pid:process.pid,port:address.port}) + '\\n';
              if (writeSync(fd, line) !== Buffer.byteLength(line))
                throw new Error('Copied HTTP port record short write');
            } finally { closeSync(fd); }
            resolve();
          } catch (error) {
            if (error === startupStopped) resolve();
            else {
              failed = true;
              control.requestStop('listener-fault');
              reject(new Error('Copied HTTP listener failed'));
            }
          }
        });
      } catch {
        routerHttp.removeListener('error', failedListen);
        failed = true;
        control.requestStop('listener-fault');
        reject(new Error('Copied HTTP listener failed'));
      }
    }));
    assertStarting();`,
  );
  return runtime;
}

// Apply fixed observer edits first; bind the qualified inventory afterward.
function prepareObserver(source) {
  const match = /^const SPEC = (\{[^\n]*\});$/m.exec(source);
  assert.ok(match, 'Exact injected transport spec');
  const base = JSON.parse(match[1]);
  assert.equal(base.version, 1);
  let observer = source;
  observer = replaceOnce(
    observer,
    "      if (request.method === 'GET' && request.url === '/studio') {",
    "      if (request.method === 'GET' && request.url === '/en/courses/transport') {",
  );
  observer = replaceOnce(
    observer,
    "  const eventPath = join(root, '.transport-proof.events');",
    `  if (!Array.isArray(SPEC.pipes) || SPEC.pipes.length !== 27) refuse();
  const pipeSubjects = SPEC.pipes.map((subject, index) => {
    if (!subject || typeof subject.path !== 'string' ||
        !/^dist\\/(?:compiled\\/next-server\\/|esm\\/server\\/|server\\/)[a-z0-9./-]+\\.js$/.test(subject.path) ||
        !/^[0-9a-f]{64}$/.test(subject.sha256) ||
        !Number.isSafeInteger(subject.bytes) || subject.bytes <= 0 ||
        !['line','column','endLine','endColumn'].every(name =>
          Number.isSafeInteger(subject.point?.[name]) && subject.point[name] >= 0)) refuse();
    const path = join(identity.nextRoot, subject.path);
    const bytes = regularBytes(path, 2 * 1024 * 1024);
    if (bytes.length !== subject.bytes || sha(bytes) !== subject.sha256) refuse();
    return {index, url:pathToFileURL(path).href, bytes, point:subject.point};
  });
  const pipeLedgerPath = join(root, '.router-pipe.events');
  const filterLedgerPath = join(root, '.router-filter.events');
  const eventPath = join(root, '.transport-proof.events');`,
  );
  observer = replaceOnce(
    observer,
    '  const pendingHandlers = new Set();',
    `  const pendingHandlers = new Set();
  const pipeScripts = new Map();
  let pipeDescriptor;
  let filterDescriptor;
  let pipeSequence = 0;
  let filterSequence = 0;
  const pipeEvent = (index) => {
    if (!active || !Number.isSafeInteger(index) || index < 0 || index >= 27 ||
        ++pipeSequence > 3 || pipeDescriptor === undefined) {
      observationFailed = true;
      return;
    }
    const line = JSON.stringify({version:1,pid:process.pid,sequence:pipeSequence,
      case:active.name,index}) + '\\n';
    if (Buffer.byteLength(line) > 128) { observationFailed = true; return; }
    try { if (writeSync(pipeDescriptor, line) !== Buffer.byteLength(line)) observationFailed = true; }
    catch { observationFailed = true; }
  };
  const filterEvent = (kind, headerClass) => {
    if (!active || !['filtered-header','header-unavailable'].includes(kind) ||
        ![null,'absent','string-text-plain','array-one-text-plain','other'].includes(headerClass) ||
        ++filterSequence > 3 || filterDescriptor === undefined) {
      observationFailed = true;
      return;
    }
    const line = JSON.stringify({version:1,pid:process.pid,sequence:filterSequence,
      case:active.name,subject:'compression',subjectSha256:SPEC.compression.sha256,
      kind,headerClass}) + '\\n';
    if (Buffer.byteLength(line) > 256) { observationFailed = true; return; }
    try { if (writeSync(filterDescriptor, line) !== Buffer.byteLength(line)) observationFailed = true; }
    catch { observationFailed = true; }
  };`,
  );
  observer = replaceOnce(
    observer,
    '      (active.allocated ? !active.encoderClosed : !active.noCompression)',
    '      active.pipeIndex === undefined ||\n      (active.allocated ? !active.encoderClosed : !active.noCompression)',
  );
  observer = replaceOnce(
    observer,
    '  function settleIfDone() {',
    `  const classifyFilteredHeader = (frame) => {
    let synchronous = false;
    let category;
    inspector.post('Debugger.evaluateOnCallFrame', {
      callFrameId: frame.callFrameId,
      // Fixed Node getter only. V8 rejects this read with throwOnSideEffect:true.
      expression: "(() => { const value = o.getHeader('Content-Type'); const plain = text => text.toLowerCase() === 'text/plain' || text.toLowerCase() === 'text/plain; charset=utf-8'; if (value === undefined) return 'absent'; if (typeof value === 'string' && plain(value)) return 'string-text-plain'; if (Array.isArray(value) && value.length === 1 && typeof value[0] === 'string' && plain(value[0])) return 'array-one-text-plain'; return 'other'; })()",
      returnByValue: true,
      throwOnSideEffect: false,
    }, (error, result) => {
      synchronous = true;
      category = error || result?.exceptionDetails ? null : result?.result?.value;
    });
    if (!synchronous || !['absent','string-text-plain','array-one-text-plain','other'].includes(category))
      throw new Error('Filtered header unavailable');
    return category;
  };
  function settleIfDone() {`,
  );
  observer = replaceOnce(
    observer,
    "  inspector.on('Debugger.breakpointResolved', ({ params }) => {",
    `  inspector.on('Debugger.scriptParsed', ({ params }) => {
    for (const subject of pipeSubjects)
      if (params.url === subject.url) {
        if (!breakpointsReady || pipeScripts.has(subject.index)) observationFailed = true;
        else pipeScripts.set(subject.index, params.scriptId);
      }
  });
  inspector.on('Debugger.breakpointResolved', ({ params }) => {`,
  );
  observer = replaceOnce(
    observer,
    "      if (point.kind === 'router') {",
    `      if (point.kind === 'pipe') {
        if (!active.router || active.pipeIndex !== undefined ||
            !pipeScripts.has(point.index)) throw new Error('Pipe attribution mismatch');
        active.pipeIndex = point.index;
        pipeEvent(point.index);
      } else if (point.kind === 'router') {`,
  );
  observer = replaceOnce(
    observer,
    "        active.noCompression = true;\n        event('none', reason);",
    `        if (reason === 'filtered') {
          if (!caller || testExpression(caller, 'o.__learnstackTransportProof === 1') !== true)
            throw new Error('Filtered response attribution mismatch');
          let headerClass;
          try { headerClass = classifyFilteredHeader(caller); }
          catch { filterEvent('header-unavailable', null); throw new Error('Filtered header unavailable'); }
          filterEvent('filtered-header', headerClass);
        }
        active.noCompression = true;
        event('none', reason);`,
  );
  observer = replaceOnce(
    observer,
    '        const selected = active?.response === response;\n        let result;',
    `        const selected = active?.response === response;
        // Copied native fixture precondition, only after the fixed arm and before the actual handler.
        if (selected && ['healthy-gzip','healthy-deflate'].includes(active.name)) {
          if (response.getHeader('Content-Type') !== undefined) refuse();
          response.setHeader('Content-Type', 'text/plain; charset=utf-8');
          if (response.getHeader('Content-Type') !== 'text/plain; charset=utf-8') refuse();
        }
        let result;`,
  );
  observer = replaceOnce(
    observer,
    '    inspector.connect();\n    inspectorConnected = true;',
    `    pipeDescriptor = openSync(pipeLedgerPath,
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
    filterDescriptor = openSync(filterLedgerPath,
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
    inspector.connect();
    inspectorConnected = true;`,
  );
  observer = replaceOnce(
    observer,
    '    breakpointsReady = true;',
    `    for (const subject of pipeSubjects) {
      const result = await post('Debugger.setBreakpointByUrl', {
        url: subject.url, lineNumber: subject.point.line, columnNumber: subject.point.column,
      });
      if (!result?.breakpointId || points.has(result.breakpointId)) refuse();
      points.set(result.breakpointId, {kind:'pipe',index:subject.index,expected:subject.point});
      for (const location of result.locations ?? []) {
        if (!inRange(location, subject.point) || resolved.has(result.breakpointId)) refuse();
        resolved.add(result.breakpointId);
      }
    }
    breakpointsReady = true;`,
  );
  observer = replaceOnce(
    observer,
    '    closeSync(descriptor);\n    throw error;',
    '    if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n    if (pipeDescriptor !== undefined) closeSync(pipeDescriptor);\n    closeSync(descriptor);\n    throw error;',
  );
  observer = replaceOnce(
    observer,
    '        resolved.size !== points.size',
    `        [...points].filter(([,point]) => point.kind !== 'pipe')
          .some(([id]) => !resolved.has(id))`,
  );
  observer = replaceOnce(
    observer,
    '              handlerSettled: false,',
    '              handlerSettled: false,\n              pipeIndex: undefined,',
  );
  observer = replaceOnce(
    observer,
    '        for (const breakpointId of points.keys())',
    `        if (pipeSequence !== 3 || pipeScripts.size === 0) refuse();
        for (const [index, scriptId] of pipeScripts) {
          const source = await post('Debugger.getScriptSource', {scriptId});
          if (typeof source.scriptSource !== 'string' ||
              sha(Buffer.from(source.scriptSource)) !== sha(pipeSubjects[index].bytes)) refuse();
        }
        for (const breakpointId of points.keys())`,
  );
  observer = replaceOnce(
    observer,
    '        inspector.disconnect();\n        closeSync(descriptor);',
    '        inspector.disconnect();\n        if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n        if (pipeDescriptor !== undefined) closeSync(pipeDescriptor);\n        closeSync(descriptor);',
  );
  return (pipes) =>
    replaceOnce(observer, match[0], 'const SPEC = ' + JSON.stringify({ ...base, pipes }) + ';');
}

/** Apply only after the existing transport transform to an isolated copied fixture. */
export function instrumentCopiedRouterMatrix({ runtime, observer, identity }) {
  assert.equal(typeof runtime, 'string');
  assert.equal(typeof observer, 'string');
  const copiedRuntime = instrumentRuntime(runtime);
  const bindObserver = prepareObserver(observer);
  const pipes = deriveRouterPipeSpec(identity);
  return Object.freeze({
    runtime: copiedRuntime,
    observer: instrumentGeneratedRouterObserver(bindObserver(pipes), identity),
    pipes,
  });
}
