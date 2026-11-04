// Fixed copied-runtime observer template. This is never a production entry/profile.
import { createHash } from 'node:crypto';
import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  realpathSync,
  unlinkSync,
  writeSync,
} from 'node:fs';
import { Session } from 'node:inspector';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import zlib from 'node:zlib';

const SPEC = '__COPIED_TRANSPORT_SPEC__';
const MARK = '__learnstackTransportProof';
const CASES = new Set([
  'healthy-gzip',
  'healthy-deflate',
  'healthy-identity',
  'fence-gzip',
  'fence-deflate',
]);
const REASONS = new Set([
  'filtered',
  'no transform',
  'size below threshold',
  'already encoded',
  'HEAD request',
  'not acceptable',
  'owned allocation sealed',
]);
const SUBJECTS = Object.freeze({
  compression: 'dist/compiled/compression/index.js',
  router: 'dist/server/lib/router-server.js',
});
const sha = (data) => createHash('sha256').update(data).digest('hex');
const refuse = () => {
  throw new Error('Copied transport proof refused');
};
function regularBytes(path, max) {
  if (realpathSync(path) !== path) refuse();
  const stat = lstatSync(path);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > max) refuse();
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const actual = fstatSync(fd);
    if (
      !actual.isFile() ||
      actual.dev !== stat.dev ||
      actual.ino !== stat.ino ||
      actual.size !== stat.size
    )
      refuse();
    return readFileSync(fd);
  } finally {
    closeSync(fd);
  }
}
function inRange(actual, expected) {
  if (actual.lineNumber < expected.line || actual.lineNumber > expected.endLine) return false;
  if (actual.lineNumber === expected.line && actual.columnNumber < expected.column) return false;
  if (actual.lineNumber === expected.endLine && actual.columnNumber >= expected.endColumn)
    return false;
  return true;
}
/** Setup executes only after child validation and is tracked as real native setup work. */
export async function startCopiedTransportObserver(framework) {
  const identity = framework?.identity;
  if (
    !identity ||
    SPEC?.version !== 1 ||
    !['production', 'development'].includes(framework.mode) ||
    identity.censusSha256 !== SPEC.censusSha256 ||
    process.versions.node !== SPEC.tuple.node ||
    identity.tuple?.next !== SPEC.tuple.next ||
    identity.tuple?.pnpm !== SPEC.tuple.pnpm
  )
    refuse();
  for (const [key, path] of Object.entries(SUBJECTS)) {
    const subject = SPEC[key];
    const kinds = key === 'compression' ? ['allocation', 'close', 'none'] : ['router'];
    if (
      !subject ||
      subject.path !== path ||
      !/^[0-9a-f]{64}$/.test(subject.sha256 ?? '') ||
      !Number.isSafeInteger(subject.bytes) ||
      subject.bytes <= 0 ||
      JSON.stringify(Object.keys(subject.points ?? {}).sort()) !== JSON.stringify(kinds.sort())
    )
      refuse();
    for (const point of Object.values(subject.points))
      if (
        !point ||
        !['line', 'column', 'endLine', 'endColumn'].every(
          (name) => Number.isSafeInteger(point[name]) && point[name] >= 0,
        ) ||
        point.endLine < point.line ||
        (point.endLine === point.line && point.endColumn <= point.column)
      )
        refuse();
  }
  const root = resolve(identity.appRoot, '../../..');
  if (
    realpathSync(root) !== root ||
    !lstatSync(root).isDirectory() ||
    identity.appRoot !== join(root, 'frontend/apps/web') ||
    realpathSync(identity.appRoot) !== identity.appRoot
  )
    refuse();
  const census = regularBytes(
    join(identity.frontendRoot, 'framework/next-response-cleanup/census.json'),
    16 * 1024 * 1024,
  );
  if (sha(census) !== SPEC.censusSha256) refuse();
  const actual = {};
  for (const key of ['compression', 'router']) {
    const subject = SPEC[key];
    const path = join(identity.nextRoot, subject.path);
    const bytes = regularBytes(path, 4 * 1024 * 1024);
    if (bytes.length !== subject.bytes || sha(bytes) !== subject.sha256) refuse();
    actual[key] = { url: pathToFileURL(path).href, bytes };
  }
  const eventPath = join(root, '.transport-proof.events');
  const armPath = join(root, '.transport-proof.arm');
  const descriptor = openSync(
    eventPath,
    constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
    0o600,
  );
  const native = {
    gzip: zlib.createGzip,
    deflate: zlib.createDeflate,
    gzipOnce: zlib.Gzip.prototype.once,
    deflateOnce: zlib.Deflate.prototype.once,
  };
  const inspector = new Session();
  let inspectorConnected = false,
    closed = false,
    observationFailed = false,
    confirmed = false;
  let breakpointsReady = false;
  let sequence = 0,
    active;
  const pendingHandlers = new Set();
  const scripts = new Map(),
    points = new Map(),
    resolved = new Set();
  const post = (method, params = {}) =>
    new Promise((done, fail) =>
      inspector.post(method, params, (error, result) => (error ? fail(error) : done(result))),
    );
  const event = (kind, value = null) => {
    if (
      observationFailed ||
      !active ||
      ++sequence > 100 ||
      ![
        'router',
        'allocation',
        'none',
        'encoder-close',
        'response-finish',
        'response-close',
        'handler-ok',
        'handler-failed',
      ].includes(kind)
    ) {
      observationFailed = true;
      return;
    }
    const record = { version: 1, pid: process.pid, sequence, case: active.name, kind, value };
    const line = JSON.stringify(record) + '\n';
    if (Buffer.byteLength(line) > 256) {
      observationFailed = true;
      return;
    }
    try {
      if (writeSync(descriptor, line) !== Buffer.byteLength(line)) observationFailed = true;
    } catch {
      observationFailed = true;
    }
  };
  const testExpression = (frame, expression) => {
    let synchronous = false,
      result;
    inspector.post(
      'Debugger.evaluateOnCallFrame',
      { callFrameId: frame.callFrameId, expression, returnByValue: true, throwOnSideEffect: true },
      (error, value) => {
        synchronous = true;
        result = error || value?.exceptionDetails ? undefined : value?.result?.value;
      },
    );
    if (!synchronous || result === undefined) throw new Error('Inspection unavailable');
    return result;
  };
  function settleIfDone() {
    if (
      !active ||
      !active.handlerSettled ||
      !active.responseClosed ||
      (active.allocated ? !active.encoderClosed : !active.noCompression)
    )
      return;
    delete active.response[MARK];
    active = undefined;
  }
  inspector.on('Debugger.scriptParsed', ({ params }) => {
    for (const [key, subject] of Object.entries(actual))
      if (params.url === subject.url) {
        if (!breakpointsReady || scripts.has(key)) observationFailed = true;
        else scripts.set(key, params.scriptId);
      }
  });
  inspector.on('Debugger.breakpointResolved', ({ params }) => {
    const point = points.get(params.breakpointId);
    if (!point || !inRange(params.location, point.expected) || resolved.has(params.breakpointId))
      observationFailed = true;
    else resolved.add(params.breakpointId);
  });
  inspector.on('Debugger.paused', ({ params }) => {
    try {
      const ids = params.hitBreakpoints;
      if (!Array.isArray(ids) || ids.length !== 1 || !points.has(ids[0])) {
        observationFailed = true;
        return;
      }
      if (!active) return;
      const point = points.get(ids[0]),
        frame = params.callFrames[0];
      const expression = point.kind === 'router' ? `res.${MARK} === 1` : `response.${MARK} === 1`;
      // Unmarked incidental requests are never attributed to the selected case.
      if (!frame || testExpression(frame, expression) !== true) return;
      if (point.kind === 'router') {
        event('router');
        active.router = true;
      } else if (point.kind === 'allocation') {
        const type = testExpression(frame, 'stream.constructor.name');
        const expected = active.name.endsWith('gzip') ? 'Gzip' : 'Deflate';
        if (!active.router || active.allocated || type !== expected)
          throw new Error('Allocation mismatch');
        active.allocated = true;
        event('allocation', type === 'Gzip' ? 'gzip' : 'deflate');
        if (active.name.startsWith('fence-')) {
          if (active.invalidated) throw new Error('Duplicate invalidation');
          active.invalidated = true;
          if (active.invalidate() !== undefined) throw new Error('Asynchronous invalidation');
        }
      } else if (point.kind === 'close') {
        const type = testExpression(frame, 'stream.constructor.name');
        const closedStream = testExpression(frame, 'stream.closed');
        const ended = testExpression(frame, 'stream.writableEnded');
        const stack = params.callFrames.slice(0, 4).map((item) => item.functionName);
        const expected = active.name.endsWith('gzip') ? 'Gzip' : 'Deflate';
        if (
          !active.allocated ||
          active.encoderClosed ||
          type !== expected ||
          closedStream !== true ||
          typeof ended !== 'boolean' ||
          stack.join(',') !== 'onStreamClose,onceWrapper,emit,emitCloseNT'
        )
          throw new Error('Actual close mismatch');
        active.encoderClosed = true;
        event('encoder-close', ended ? 'ended' : 'unended');
        active.resolveEncoder();
        settleIfDone();
      } else {
        const caller = params.callFrames.find((item) => item.functionName === 'nocompress');
        const reason = caller && testExpression(caller, 'a');
        if (!REASONS.has(reason)) throw new Error('Selection mismatch');
        active.noCompression = true;
        event('none', reason);
      }
    } catch {
      observationFailed = true;
    } finally {
      try {
        inspector.post('Debugger.resume', (error) => {
          if (error) observationFailed = true;
        });
      } catch {
        observationFailed = true;
      }
    }
  });
  try {
    inspector.connect();
    inspectorConnected = true;
    await post('Debugger.enable');
    for (const key of ['compression', 'router'])
      for (const [kind, expected] of Object.entries(SPEC[key].points)) {
        const result = await post('Debugger.setBreakpointByUrl', {
          url: actual[key].url,
          lineNumber: expected.line,
          columnNumber: expected.column,
        });
        if (!result?.breakpointId || points.has(result.breakpointId)) refuse();
        points.set(result.breakpointId, { kind, key, expected });
        for (const location of result.locations ?? []) {
          if (!inRange(location, expected) || resolved.has(result.breakpointId)) refuse();
          resolved.add(result.breakpointId);
        }
      }
    breakpointsReady = true;
  } catch (error) {
    if (inspectorConnected) inspector.disconnect();
    closeSync(descriptor);
    throw error;
  }
  return Object.freeze({
    async confirmLoaded() {
      if (
        closed ||
        confirmed ||
        observationFailed ||
        scripts.size !== 2 ||
        resolved.size !== points.size
      )
        refuse();
      for (const [key, subject] of Object.entries(actual)) {
        const source = await post('Debugger.getScriptSource', { scriptId: scripts.get(key) });
        if (
          typeof source.scriptSource !== 'string' ||
          sha(Buffer.from(source.scriptSource)) !== sha(subject.bytes)
        )
          refuse();
      }
      confirmed = true;
    },
    wrapHandler(handler, invalidate) {
      if (!confirmed || closed || typeof handler !== 'function' || typeof invalidate !== 'function')
        refuse();
      return function (request, response, ...rest) {
        if (request.method === 'GET' && request.url === '/studio') {
          let name;
          try {
            const bytes = regularBytes(armPath, 32);
            name = bytes.toString('utf8').trim();
            if (!CASES.has(name) || active || Object.hasOwn(response, MARK)) refuse();
            unlinkSync(armPath);
          } catch (error) {
            if (error?.code !== 'ENOENT') throw error;
          }
          if (name) {
            Object.defineProperty(response, MARK, { value: 1, configurable: true });
            let resolveResponse, resolveEncoder;
            const responseDone = new Promise((resolve) => {
              resolveResponse = resolve;
            });
            const encoderDone = new Promise((resolve) => {
              resolveEncoder = resolve;
            });
            active = {
              name,
              response,
              invalidate,
              allocated: false,
              encoderClosed: false,
              noCompression: false,
              router: false,
              invalidated: false,
              responseClosed: false,
              handlerSettled: false,
              responseDone,
              encoderDone,
              resolveEncoder,
            };
            response.once('finish', () => event('response-finish'));
            response.once('close', () => {
              if (active?.response === response) {
                active.responseClosed = true;
                event('response-close');
                resolveResponse();
                settleIfDone();
              }
            });
          }
        }
        const selected = active?.response === response;
        let result;
        try {
          result = Reflect.apply(handler, this, [request, response, ...rest]);
        } catch (error) {
          if (selected && active?.response === response) {
            active.handlerSettled = true;
            event('handler-failed');
            settleIfDone();
          }
          throw error;
        }
        if (selected) {
          try {
            let tracked;
            tracked = Promise.prototype.then.call(
              result,
              () => {
                pendingHandlers.delete(tracked);
                if (active?.response === response) {
                  active.handlerSettled = true;
                  event('handler-ok');
                  settleIfDone();
                }
              },
              () => {
                pendingHandlers.delete(tracked);
                if (active?.response === response) {
                  active.handlerSettled = true;
                  event('handler-failed');
                  settleIfDone();
                }
              },
            );
            pendingHandlers.add(tracked);
            void tracked.catch(() => {
              pendingHandlers.delete(tracked);
              observationFailed = true;
            });
          } catch {
            observationFailed = true;
          }
        }
        return result;
      };
    },
    async close() {
      if (closed) return;
      closed = true;
      try {
        const selected = active;
        while (pendingHandlers.size) await Promise.allSettled([...pendingHandlers]);
        if (selected && !selected.responseClosed) await selected.responseDone;
        if (selected?.allocated && !selected.encoderClosed) await selected.encoderDone;
        if (
          active ||
          observationFailed ||
          !confirmed ||
          zlib.createGzip !== native.gzip ||
          zlib.createDeflate !== native.deflate ||
          zlib.Gzip.prototype.once !== native.gzipOnce ||
          zlib.Deflate.prototype.once !== native.deflateOnce
        )
          refuse();
        for (const breakpointId of points.keys())
          await post('Debugger.removeBreakpoint', { breakpointId });
      } finally {
        inspector.disconnect();
        closeSync(descriptor);
      }
    },
  });
}
