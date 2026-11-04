// Pure source/evidence controls; extracted factories are inert controls, never initialized Next proof.
import assert from 'node:assert/strict';
import {
  readFileSync,
  writeFileSync,
  mkdtempSync,
  mkdirSync,
  realpathSync,
  rmSync,
  symlinkSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { Session } from 'node:inspector';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import zlib from 'node:zlib';
import { Script } from 'node:vm';

import { verifyFrameworkInstallation } from './framework-installation.mjs';
import { instrumentCopiedBffRuntime } from '../apps/web/scripts/bff-proof-host.mjs';
import {
  instrumentCopiedTransport,
  deriveTransportSpec,
} from '../apps/web/scripts/framework-transport-proof-source.mjs';
import { instrumentCopiedRouterMatrix } from '../apps/web/scripts/framework-router-matrix-source.mjs';

import { instrumentCopiedRouterFaults } from '../apps/web/scripts/framework-router-fault-source.mjs';
import { describeRouterParser } from '../apps/web/scripts/framework-router-generated-pipes.mjs';
import {
  deriveFaultJoinPoints,
  deriveFaultBoundaryPoints,
} from '../apps/web/scripts/framework-router-fault-join-points.mjs';
import { validateRouterFaultRows } from '../apps/web/scripts/framework-router-fault-host.mjs';
import { createRouterFaultObserver } from '../apps/web/scripts/framework-router-fault-runtime.mjs';

const frontend = fileURLToPath(new URL('../', import.meta.url));
const app = join(frontend, 'apps/web');
const ts = createRequire(join(app, 'package.json'))('typescript');

const identity = verifyFrameworkInstallation({ appRoot: app });
const bff = instrumentCopiedBffRuntime(
  readFileSync(join(app, 'scripts/public-server-runtime.mjs'), 'utf8'),
  {
    mutation: undefined,
    external: false,
    uiMode: true,
    isolatedProtocol: true,
    disabledOwnPreference: false,
    fakeIssuer: 'https://identity.example.test/realms/learnstack',
    fakeLogout: 'https://identity.example.test/realms/learnstack/protocol/openid-connect/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker: 'a'.repeat(43),
    clockBase: 1800000000000,
  },
);
const transport = instrumentCopiedTransport({ source: bff, identity });
const matrix = instrumentCopiedRouterMatrix({ ...transport, identity });
const inputs = {
  ...matrix,
  owner: readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8'),
  ledger: readFileSync(join(app, 'scripts/framework-router-pipe-ledger.mjs'), 'utf8'),
  requestRuntime: readFileSync(join(app, 'src/server/auth/request-runtime.ts'), 'utf8'),
};
const files = Object.freeze(
  matrix.pipes.map((s, index) =>
    Object.freeze({
      index,
      kind: 'installed',
      path: s.path,
      url: pathToFileURL(join(identity.nextRoot, s.path)).href,
      bytes: s.bytes,
      sha256: s.sha256,
      entries: Object.freeze([Object.freeze({ responseName: 'response', point: s.point })]),
    }),
  ),
);

test('current copied runtime/observer/ledger/request source parses with eight closed cases', () => {
  const copied = instrumentCopiedRouterFaults(inputs);
  for (const [name, value] of Object.entries(copied)) {
    const kind = ['route', 'requestRuntime'].includes(name) ? ts.ScriptKind.TS : ts.ScriptKind.JS;
    const parsed = ts.createSourceFile(name, value, ts.ScriptTarget.Latest, true, kind);
    assert.equal(parsed.parseDiagnostics.length, 0);
  }
  assert.match(copied.observer, /callFault\(frame,'join'/);
  assert.match(copied.requestRuntime, /capture\?\.capture\(promise,response\)/);
  assert.match(copied.runtime, /transportProof\?\.watchdog\(\)/);
  assert.match(copied.runtime, /transportProof\?\.stop\(\)/);
});

test('missing observer/runtime/aggregate anchors reject without partial output', () => {
  for (const [key, anchor] of [
    ['observer', '        pipeEvent(point.index);'],
    ['runtime', '    proofShutdown.watchdog = true;'],
    ['owner', '  const graceful ='],
    ['requestRuntime', '        const actual = Promise.resolve(promise);'],
  ])
    assert.throws(() =>
      instrumentCopiedRouterFaults({
        ...inputs,
        [key]: inputs[key].replace(anchor, 'missing copied anchor'),
      }),
    );
});

test('27 installed pipe and installed owner/router call sites derive from qualified bytes', () => {
  const points = deriveFaultJoinPoints(identity, files, describeRouterParser(identity));
  assert.equal(points.length, 27);
  for (const p of points) {
    assert.equal(p.endWorkName, 'explicitEndWork');
    assert.equal(p.ownerName, 'owner');
    assert.equal(p.result.names.length, 6);
  }
  const points2 = deriveFaultBoundaryPoints(identity, {
    ...deriveTransportSpec(identity),
    parser: describeRouterParser(identity),
  });
  assert.deepEqual(Object.keys(points2).sort(), [
    'cleanup',
    'drain',
    'ownerInstall',
    'routerJoin',
    'routerRegistration',
    'routerResult',
    'transition',
  ]);
});

test('wrong source size, response binding or installed subject refuses', () => {
  assert.throws(() =>
    deriveFaultJoinPoints(
      identity,
      Object.freeze([Object.freeze({ ...files[0], bytes: files[0].bytes + 1 }), ...files.slice(1)]),
      describeRouterParser(identity),
    ),
  );
  assert.throws(() =>
    deriveFaultJoinPoints(
      identity,
      Object.freeze([
        Object.freeze({
          ...files[0],
          entries: Object.freeze([
            Object.freeze({ ...files[0].entries[0], responseName: 'foreign' }),
          ]),
        }),
        ...files.slice(1),
      ]),
      describeRouterParser(identity),
    ),
  );
  const spec = { ...deriveTransportSpec(identity), parser: describeRouterParser(identity) };
  assert.throws(() =>
    deriveFaultBoundaryPoints(identity, {
      ...spec,
      router: { ...spec.router, path: 'dist/server/foreign.js' },
    }),
  );
});

// Actual helper + owned filesystem/parser control; this is not build admission or Next execution.
test('generated dynamic route paths retain exact bytes, containment and join bindings', () => {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-path-control-')));
  try {
    const controlApp = join(root, 'app');
    mkdirSync(controlApp);
    writeFileSync(join(controlApp, 'package.json'), '{"private":true}\n');
    symlinkSync(join(app, 'node_modules'), join(controlApp, 'node_modules'), 'dir');
    const controlIdentity = Object.freeze({ ...identity, appRoot: controlApp });
    const parser = describeRouterParser(identity);
    assert.deepEqual(describeRouterParser(controlIdentity), parser);
    const fixture = readFileSync(join(frontend, 'scripts/fixtures/router-minified-join.txt'));
    const provenance = JSON.parse(
      readFileSync(join(frontend, 'scripts/fixtures/router-minified-join.provenance.json'), 'utf8'),
    );
    assert.equal(fixture.length, provenance.fixture.bytes);
    assert.equal(createHash('sha256').update(fixture).digest('hex'), provenance.fixture.sha256);
    const bytes = Buffer.from('const generatedJoin = ' + fixture.toString('utf8').trim() + ';\n');
    const parsed = ts.createSourceFile(
      'generated.js',
      bytes.toString('utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.JS,
    );
    assert.equal(parsed.parseDiagnostics.length, 0);
    const fn = parsed.statements[0].declarationList.declarations[0].initializer;
    const entry = fn.body.statements[0];
    const start = parsed.getLineAndCharacterOfPosition(entry.getStart(parsed));
    const end = parsed.getLineAndCharacterOfPosition(entry.end);
    const point = Object.freeze({
      line: start.line,
      column: start.character,
      endLine: end.line,
      endColumn: end.character,
    });
    const records = ['[locale]', '[...slug]', '[[...slug]]'].map((segment, offset) => {
      const path = 'next/server/app/(public)/' + segment + '/page.js';
      const target = join(controlApp, '.next', path.slice('next/'.length));
      mkdirSync(join(target, '..'), { recursive: true });
      writeFileSync(target, bytes);
      return Object.freeze({
        index: files.length + offset,
        kind: 'generated',
        path,
        url: pathToFileURL(target).href,
        bytes: bytes.length,
        sha256: createHash('sha256').update(bytes).digest('hex'),
        entries: Object.freeze([Object.freeze({ responseName: 'a', point })]),
      });
    });
    // A generated manifest also inventories dynamic files that have no pipe entry.
    const emptyPath = 'next/server/app/(public)/[locale]/page_client-reference-manifest.js';
    const emptyTarget = join(controlApp, '.next', emptyPath.slice('next/'.length));
    const emptyBytes = Buffer.from('export const noPipeEntry = true;\n');
    writeFileSync(emptyTarget, emptyBytes);
    const emptyRecord = Object.freeze({
      index: files.length + records.length,
      kind: 'generated',
      path: emptyPath,
      url: pathToFileURL(emptyTarget).href,
      bytes: emptyBytes.length,
      sha256: createHash('sha256').update(emptyBytes).digest('hex'),
      entries: Object.freeze([]),
    });
    const points = deriveFaultJoinPoints(
      controlIdentity,
      Object.freeze([...files, ...records, emptyRecord]),
      parser,
    );
    assert.equal(points.length, 30);
    for (const generated of points.slice(27)) {
      assert.equal(generated.responseName, 'a');
      assert.equal(generated.endWorkName, 'c');
      assert.deepEqual([...generated.result.names], ['k', 'f', 'i', 'd', 'm', 'g']);
    }
    const derive = (record) =>
      deriveFaultJoinPoints(controlIdentity, Object.freeze([record, ...files]), parser);
    for (const path of [
      'other/server/app/[locale]/page.js',
      'next/server/../[locale]/page.js',
      'next/server/app//[locale]/page.js',
      'next/server/app/./[locale]/page.js',
      'next/server/app\\[locale]/page.js',
      'next/server/app/[locale]/page.json',
      'next/server/app/\0/page.js',
    ])
      assert.throws(() => derive(Object.freeze({ ...records[0], path })), {
        code: 'ERR_ASSERTION',
        operator: '==',
        actual: false,
        expected: true,
      });
    assert.throws(
      () =>
        derive(
          Object.freeze({ ...records[0], kind: 'installed', path: 'dist/server/[locale].js' }),
        ),
      { code: 'ERR_ASSERTION', operator: '==', actual: false, expected: true },
    );
    assert.throws(() =>
      derive(
        Object.freeze({
          ...records[0],
          url: pathToFileURL(join(controlApp, '.next/server/foreign.js')).href,
        }),
      ),
    );
    assert.throws(() => derive(Object.freeze({ ...emptyRecord, bytes: emptyBytes.length + 1 })));
    assert.throws(() => derive(Object.freeze({ ...records[0], bytes: bytes.length + 1 })));
    assert.throws(() => derive(Object.freeze({ ...records[0], sha256: '0'.repeat(64) })));
    const changed = Buffer.from(bytes);
    changed[0] ^= 1;
    writeFileSync(fileURLToPath(records[0].url), changed);
    assert.throws(() => derive(records[0]));
  } finally {
    rmSync(root, { recursive: true });
  }
});

// V8 compiles these pinned bytes without executing a subject or importing Next.
test('qualified owner and router observation points are natively breakable before execution', async () => {
  const spec = { ...deriveTransportSpec(identity), parser: describeRouterParser(identity) };
  const boundary = deriveFaultBoundaryPoints(identity, spec);
  const session = new Session();
  const post = (method, params = {}) =>
    new Promise((resolve, reject) =>
      session.post(method, params, (error, result) => (error ? reject(error) : resolve(result))),
    );
  const inside = (actual, expected) =>
    actual.lineNumber >= expected.line &&
    actual.lineNumber <= expected.endLine &&
    (actual.lineNumber !== expected.line || actual.columnNumber >= expected.column) &&
    (actual.lineNumber !== expected.endLine || actual.columnNumber < expected.endColumn);
  session.connect();
  try {
    await post('Debugger.enable');
    await post('Runtime.enable');
    for (const key of ['compression', 'router']) {
      const subject = spec[key];
      const bytes = readFileSync(join(identity.nextRoot, subject.path));
      assert.equal(bytes.length, subject.bytes);
      assert.equal(createHash('sha256').update(bytes).digest('hex'), subject.sha256);
      const compiled = await post('Runtime.compileScript', {
        expression: bytes.toString('utf8'),
        sourceURL: 'file:///learnstack-inert-router-point-control/' + key + '.js',
        persistScript: true,
      });
      assert.equal(compiled.exceptionDetails, undefined);
      assert.match(compiled.scriptId, /^[0-9]+$/);
      const loaded = await post('Debugger.getScriptSource', { scriptId: compiled.scriptId });
      assert.equal(createHash('sha256').update(loaded.scriptSource).digest('hex'), subject.sha256);
      const points = {
        ...subject.points,
        ...(key === 'compression'
          ? {
              install: boundary.ownerInstall,
              cleanup: boundary.cleanup,
              transition: boundary.transition,
              drain: boundary.drain,
            }
          : {
              registration: boundary.routerRegistration,
              join: boundary.routerJoin,
              result: boundary.routerResult.point,
            }),
      };
      for (const [name, point] of Object.entries(points)) {
        const result = await post('Debugger.setBreakpoint', {
          location: {
            scriptId: compiled.scriptId,
            lineNumber: point.line,
            columnNumber: point.column,
          },
        });
        assert.equal(inside(result.actualLocation, point), true, key + ':' + name);
        await post('Debugger.removeBreakpoint', { breakpointId: result.breakpointId });
      }
      if (key === 'compression') {
        const file = ts.createSourceFile(
          'owner.js',
          bytes.toString('utf8'),
          ts.ScriptTarget.Latest,
          true,
          ts.ScriptKind.JS,
        );
        const found = [];
        const visit = (node) => {
          if (ts.isFunctionDeclaration(node) && node.name?.text === 'setResponseDrainListener')
            found.push(node);
          ts.forEachChild(node, visit);
        };
        visit(file);
        assert.equal(found.length, 1);
        const statements = found[0].body.statements;
        const assignment = statements.at(-2),
          returned = statements.at(-1);
        assert.equal(assignment.getText(file), 'drainListener = listener;');
        assert.equal(returned.getText(file), 'return listener;');
        const a = file.getLineAndCharacterOfPosition(returned.getStart(file));
        const z = file.getLineAndCharacterOfPosition(returned.end);
        const oldPoint = {
          line: a.line,
          column: a.character,
          endLine: z.line,
          endColumn: z.character,
        };
        const result = await post('Debugger.setBreakpoint', {
          location: {
            scriptId: compiled.scriptId,
            lineNumber: oldPoint.line,
            columnNumber: oldPoint.column,
          },
        });
        assert.equal(inside(result.actualLocation, oldPoint), false);
        assert.equal(result.actualLocation.lineNumber, oldPoint.endLine);
        assert.equal(result.actualLocation.columnNumber, oldPoint.endColumn);
        await post('Debugger.removeBreakpoint', { breakpointId: result.breakpointId });
      }
    }
  } finally {
    session.disconnect();
  }
});

// Select the exact copied one-shot factory function; all native zlib calls remain real.
const runtime = readFileSync(join(app, 'scripts/framework-router-fault-runtime.mjs'), 'utf8');
const ast = ts.createSourceFile(
  'runtime.mjs',
  runtime,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.JS,
);
let factory;
function visit(node) {
  if (
    ts.isCallExpression(node) &&
    node.expression.getText(ast) === 'Object.defineProperty' &&
    node.arguments[0]?.getText(ast) === 'zlib' &&
    node.arguments[1]?.getText(ast) === "'createGzip'" &&
    ts.isObjectLiteralExpression(node.arguments[2])
  ) {
    const value = node.arguments[2].properties.find(
      (item) => ts.isPropertyAssignment(item) && item.name.getText(ast) === 'value',
    );
    if (value && ts.isFunctionExpression(value.initializer)) {
      assert.equal(factory, undefined);
      factory = value.initializer.getText(ast);
    }
  }
  ts.forEachChild(node, visit);
}
visit(ast);
assert.ok(factory);
function nativeFactory(name, dirty = false) {
  const gzip = Object.getOwnPropertyDescriptor(zlib, 'createGzip');
  const state = { sourceFault: true },
    response = {},
    handle = { errors: { cleanup: new Error('Fixed cleanup') } };
  const events = [];
  let caught;
  const gate = {
    constructorFault(_r, error) {
      caught = error;
    },
    cleanupFault(_r, error) {
      assert.equal(error, handle.errors.cleanup);
    },
    encoderFault(_r, error) {
      caught = error;
    },
  };
  const code = dirty
    ? factory.replace(/Object\.defineProperty\(zlib,\s*'createGzip',\s*gzip\);/, '')
    : factory;
  if (dirty) assert.notEqual(code, factory);
  const wrapper = new Function(
    'assert',
    'zlib',
    'gzip',
    'name',
    'finalCase',
    'gate',
    'response',
    'state',
    'event',
    'handle',
    'guarded',
    'return (' + code + ');',
  )(
    assert,
    zlib,
    gzip,
    name,
    'constructor-failure-gzip',
    gate,
    response,
    state,
    (k) => events.push(k),
    handle,
    (fn) => fn(),
  );
  Object.defineProperty(zlib, 'createGzip', { ...gzip, value: wrapper });
  return {
    gzip,
    state,
    events,
    handle,
    caught: () => caught,
    call: () => zlib.createGzip(),
    restore: () => Object.defineProperty(zlib, 'createGzip', gzip),
  };
}

test('real throwing constructor retains exact RangeError and restores native descriptor', () => {
  const control = nativeFactory('constructor-failure-gzip');
  try {
    let thrown;
    try {
      control.call();
    } catch (error) {
      thrown = error;
    }
    assert.equal(thrown, control.caught());
    assert.equal(thrown.code, 'ERR_OUT_OF_RANGE');
    assert.deepEqual(Object.getOwnPropertyDescriptor(zlib, 'createGzip'), control.gzip);
  } finally {
    control.restore();
  }
});

test('dirty omitted finally restoration is exposed after the same actual constructor failure', () => {
  const control = nativeFactory('constructor-failure-gzip', true);
  try {
    assert.throws(control.call, { code: 'ERR_OUT_OF_RANGE' });
    assert.throws(() =>
      assert.deepEqual(Object.getOwnPropertyDescriptor(zlib, 'createGzip'), control.gzip),
    );
  } finally {
    control.restore();
  }
});

test('source cleanup wrapper requests actual zlib close before throwing separate cleanup', async () => {
  const control = nativeFactory('source-cleanup-fault-gzip');
  try {
    const stream = control.call();
    const closed = new Promise((resolve) => stream.once('close', resolve));
    assert.throws(
      () => stream.destroy(),
      (error) => error === control.handle.errors.cleanup,
    );
    await closed;
    assert.equal(stream.closed, true);
    assert.deepEqual(control.events, ['cleanup-fault']);
    assert.deepEqual(Object.getOwnPropertyDescriptor(zlib, 'createGzip'), control.gzip);
  } finally {
    control.restore();
  }
});

test('early-fault wrapper receives actual write-after-end and actual encoder close', async () => {
  const control = nativeFactory('encoder-early-fault-gzip');
  try {
    const stream = control.call();
    control.state.stream = stream;
    await new Promise((resolve) => stream.once('close', resolve));
    assert.equal(control.caught().code, 'ERR_STREAM_WRITE_AFTER_END');
    assert.equal(stream.closed, true);
    assert.deepEqual(control.events, ['encoder-fault']);
    assert.deepEqual(Object.getOwnPropertyDescriptor(zlib, 'createGzip'), control.gzip);
  } finally {
    control.restore();
  }
});

const hash = 'a'.repeat(64),
  point = { line: 1, column: 0, endLine: 1, endColumn: 10 };
const context = {
  pid: 123,
  union: { sites: [{ fileIndex: 0, entryIndex: 0, sha256: hash }] },
  joins: [{ fileIndex: 0, entryIndex: 0, joinPoint: point }],
  boundary: { routerJoin: point },
  routerSha256: hash,
};
function prefix() {
  const proof = (phase) => ({
    phase,
    index: phase === 'pipe' ? 0 : -1,
    scriptId: phase === 'pipe' ? '11' : '12',
    sha256: hash,
    line: 1,
    column: 1,
  });
  const result = (phase) => ({
    phase,
    hasPrimary: phase === 'pipe',
    hasEnd: false,
    hasCleanup: false,
  });
  return [
    ['arm'],
    ['route'],
    ['pull'],
    ['pipe'],
    ['source-fault'],
    ['transition', 'failure'],
    ['observer-joined', 'fulfilled'],
    ['join', proof('pipe')],
    ['result', result('pipe')],
    ['response-finish'],
    ['response-close'],
    ['join', proof('router')],
    ['result', result('router')],
    ['handler', 'fulfilled'],
    ['retired'],
  ].map(([k, x], i) => ({ v: 1, p: 123, n: i + 1, c: 'pipe-before-selection', k, x: x ?? null }));
}
test('finite host parser accepts a complete synthetic preselection case only as parser control', () => {
  assert.equal(validateRouterFaultRows(prefix(), context).size, 1);
});
for (const [name, mutate] of [
  [
    'missing actual pipe join',
    (rows) =>
      rows.splice(
        rows.findIndex((row) => row.k === 'join'),
        1,
      ),
  ],
  [
    'transition after real join',
    (rows) => {
      const a = rows.findIndex((row) => row.k === 'transition'),
        b = rows.findIndex((row) => row.k === 'join');
      [rows[a], rows[b]] = [rows[b], rows[a]];
    },
  ],
  [
    'substituted source bytes',
    (rows) => {
      rows.find((row) => row.k === 'join').x.sha256 = 'b'.repeat(64);
    },
  ],
  [
    'suppressed primary failure',
    (rows) => {
      rows.find((row) => row.k === 'result').x.hasPrimary = false;
    },
  ],
  [
    'unhandled fault cannot pass',
    (rows) => {
      rows[4].k = 'fault';
    },
  ],
  [
    'join outside qualified range',
    (rows) => {
      rows.find((row) => row.k === 'join').x.column = 99;
    },
  ],
  [
    'zero-allocation case cannot hide encoder',
    (rows) => {
      rows.splice(4, 0, { ...rows[0], k: 'allocation' });
    },
  ],
])
  test('dirty ' + name + ' refuses finite host evidence', () => {
    const rows = prefix();
    mutate(rows);
    rows.forEach((row, i) => {
      row.n = i + 1;
    });
    assert.throws(() => validateRouterFaultRows(rows, context));
  });

function methodText(name) {
  const found = [];
  function visit(node) {
    if (ts.isMethodDeclaration(node) && node.name.getText(ast) === name) found.push(node);
    ts.forEachChild(node, visit);
  }
  visit(ast);
  assert.equal(found.length, 1);
  return found[0].getText(ast);
}

test('pipe end-work identity rejects the route task instead of the actual native aggregate', () => {
  const aggregate = new Promise(() => {}),
    route = new Promise(() => {});
  let joins = 0;
  const owner = {
    join() {
      joins++;
      return aggregate;
    },
  };
  const state = { name: 'end-work-reject-gzip', owner, aggregate };
  const pipe = new Function(
    'assert',
    'current',
    'sameOwner',
    'selected',
    'guarded',
    'event',
    'maybeRetire',
    'registration',
    'gate',
    'return ({' + methodText('pipe') + '}).pipe;',
  )(
    assert,
    () => state,
    () => owner,
    state,
    (fn) => fn(),
    () => {},
    () => {},
    () => {},
    { pipe() {} },
  );
  assert.throws(() => pipe({}, route));
  assert.equal(joins, 0);
});

test('pipe observer joins the original promise without claiming the actual pipe await', () => {
  const aggregate = new Promise(() => {});
  let joins = 0;
  const events = [];
  const owner = {
    join() {
      joins++;
      return aggregate;
    },
  };
  const state = { name: 'end-work-reject-gzip', owner, aggregate };
  const pipe = new Function(
    'assert',
    'current',
    'sameOwner',
    'selected',
    'guarded',
    'event',
    'maybeRetire',
    'registration',
    'gate',
    'return ({' + methodText('pipe') + '}).pipe;',
  )(
    assert,
    () => state,
    () => owner,
    state,
    (fn) => fn(),
    (name) => events.push(name),
    () => {},
    () => {},
    { pipe() {} },
  );
  pipe({}, aggregate);
  assert.equal(state.ownerPromise, aggregate);
  assert.equal(joins, 2);
  assert.deepEqual(events, ['pipe']);
  assert.equal(state.joins, undefined);
  assert.equal(state.ownerSettled, undefined);
});

function finishRemoval(dirty = false) {
  let fn;
  function visit(node) {
    if (
      ts.isBinaryExpression(node) &&
      node.left.getText(ast) === 'response.removeListener' &&
      ts.isFunctionExpression(node.right)
    )
      fn = node.right.getText(ast);
    ts.forEachChild(node, visit);
  }
  visit(ast);
  assert.ok(fn);
  const terminal = function onResponseTerminal() {},
    foreign = function onResponseTerminal() {};
  const present = new Set([terminal, foreign]);
  const response = { writableFinished: true };
  const state = {
    cleanupPoint: true,
    finish: true,
    terminalListener: terminal,
    removeListener(_name, listener) {
      present.delete(listener);
      return response;
    },
  };
  const error = new Error('Fixed finish cleanup'),
    events = [];
  if (dirty)
    fn = fn.replace(
      /listener\s*===\s*state\.terminalListener/,
      "listener?.name==='onResponseTerminal'",
    );
  const wrapped = new Function(
    'assert',
    'state',
    'response',
    'gate',
    'handle',
    'event',
    'return (' + fn + ');',
  )(
    assert,
    state,
    response,
    {
      cleanupFault(_response, value) {
        assert.equal(value, error);
        assert.equal(present.has(terminal), false);
      },
    },
    { errors: { cleanup: error } },
    (name) => events.push(name),
  );
  response.removeListener = wrapped;
  return { state, response, terminal, foreign, present, error, events, wrapped };
}

test('finish cleanup hook requires exact listener and qualified cleanup point, then native removal', () => {
  const value = finishRemoval();
  value.state.cleanupPoint = false;
  value.wrapped.call(value.response, 'finish', value.terminal);
  assert.deepEqual(value.events, []);
  value.present.add(value.terminal);
  value.state.cleanupPoint = true;
  value.wrapped.call(value.response, 'finish', value.foreign);
  assert.deepEqual(value.events, []);
  assert.throws(
    () => value.wrapped.call(value.response, 'finish', value.terminal),
    (error) => error === value.error,
  );
  assert.equal(value.present.has(value.terminal), false);
  assert.deepEqual(value.events, ['cleanup-fault']);
  assert.equal(value.response.removeListener, value.state.removeListener);
});

test('dirty name-only finish attribution is rejected by the exact native-removal witness', () => {
  const value = finishRemoval(true);
  assert.throws(() => value.wrapped.call(value.response, 'finish', value.foreign));
  assert.equal(value.present.has(value.terminal), true);
  assert.deepEqual(value.events, []);
});

test('exact minified fixture preserves real join locals and result precedence (parser only)', () => {
  const fixture = readFileSync(join(frontend, 'scripts/fixtures/router-minified-join.txt'));
  const provenance = JSON.parse(
    readFileSync(join(frontend, 'scripts/fixtures/router-minified-join.provenance.json'), 'utf8'),
  );
  assert.equal(fixture.length, provenance.fixture.bytes);
  assert.equal(createHash('sha256').update(fixture).digest('hex'), provenance.fixture.sha256);
  const parsed = ts.createSourceFile(
    'minified.js',
    '(' + fixture.toString('utf8').trim() + ')',
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  assert.equal(parsed.parseDiagnostics.length, 0);
  const fn = parsed.statements[0].expression.expression;
  assert.deepEqual(
    fn.parameters.map((p) => p.name.text),
    ['a', 'b', 'c'],
  );
  const helper = readFileSync(join(app, 'scripts/framework-router-fault-join-points.mjs'), 'utf8');
  const tree = ts.createSourceFile(
    'helper.mjs',
    helper,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const method = tree.statements.find(
    (node) => ts.isFunctionDeclaration(node) && node.name.text === 'resultPoint',
  );
  const point = new Function('assert', 'ts', 'location', 'return (' + method.getText(tree) + ');')(
    assert,
    ts,
    () => null,
  )(ts, parsed, fn);
  assert.deepEqual([...point.names], ['k', 'f', 'i', 'd', 'm', 'g']);
});

// Exact generated functions in an inert source carrier; no Next execution is claimed.
function terminalControl() {
  const text = instrumentCopiedRouterFaults(inputs).observer;
  const tree = ts.createSourceFile(
    'observer.mjs',
    text,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const found = [];
  function walk(node) {
    found.push(node);
    ts.forEachChild(node, walk);
  }
  walk(tree);
  const terminal = found.filter(
    (n) => ts.isFunctionDeclaration(n) && n.name?.text === 'terminalQualification',
  );
  const duplicate = found.filter(
    (n) =>
      ts.isCallExpression(n) &&
      n.expression.getText(tree) === 'inspector.on' &&
      n.arguments[0]?.text === 'Debugger.scriptParsed',
  )[0].arguments[1];
  const breakpoint = found.filter(
    (n) =>
      ts.isCallExpression(n) &&
      n.expression.getText(tree) === 'inspector.on' &&
      n.arguments[0]?.text === 'Debugger.breakpointResolved',
  )[0].arguments[1];
  const resume = found.filter(
    (n) =>
      ts.isCallExpression(n) &&
      n.expression.getText(tree) === 'inspector.post' &&
      n.arguments[0]?.text === 'Debugger.resume',
  )[0].arguments[1];
  assert.equal(terminal.length, 1);
  return new Function(
    'Buffer',
    'sha',
    `
    let observationFailed=false;
    const state={runtimeFailed:false,traceFailed:false,sourceMode:'valid'};
    const fault={failed:()=>state.runtimeFailed},pipeTrace={summary:()=>({failed:state.traceFailed})};
    const confirmed=true,breakpointsReady=true,inspectorConnected=true,pipeSequence=8;
    const active={name:'constructor-failure-gzip',handlerSettled:false};
    const pendingHandlers=new Set([{}]);
    const scripts=new Map([['compression','1'],['router','2']]);
    const points=new Map(),resolved=new Set();
    const inRange=()=>true,trace=()=>{},pipeFault=()=>{observationFailed=true;};
    const actual={compression:{url:'file:///fixed/compression.js',bytes:Buffer.from('compression')},router:{url:'file:///fixed/router.js',bytes:Buffer.from('router')}};
    const pipeScripts=new Map([[0,'3']]),pipeFiles=[{sha256:sha(Buffer.from('pipe'))}];
    const zlib={createGzip(){},createDeflate(){},Gzip:class {},Deflate:class {}};
    zlib.Gzip.prototype.once=function(){};zlib.Deflate.prototype.once=function(){};
    const native={gzip:zlib.createGzip,deflate:zlib.createDeflate,gzipOnce:zlib.Gzip.prototype.once,deflateOnce:zlib.Deflate.prototype.once,
      gzipDescriptor:Object.getOwnPropertyDescriptor(zlib,'createGzip'),deflateDescriptor:Object.getOwnPropertyDescriptor(zlib,'createDeflate'),
      gzipOnceDescriptor:Object.getOwnPropertyDescriptor(zlib.Gzip.prototype,'once'),deflateOnceDescriptor:Object.getOwnPropertyDescriptor(zlib.Deflate.prototype,'once')};
    const read=[];
    const inspector={post(method,{scriptId},callback){
      if(method!=='Debugger.getScriptSource')throw new Error('Unexpected control operation');read.push(scriptId);
      if(state.sourceMode==='late')return;
      callback(state.sourceMode==='error'?new Error('Fixed source refusal'):null,{scriptSource:state.sourceMode==='changed'?'changed':({'1':'compression','2':'router','3':'pipe'})[scriptId]});
    }};
    const refuse=()=>{throw new Error('Fixed terminal refusal');};
    ${terminal[0].getText(tree)}
    const duplicate=${duplicate.getText(tree)},breakpoint=${breakpoint.getText(tree)},resume=${resume.getText(tree)};
    return {run:terminalQualification,state,read,duplicate:()=>duplicate({params:{url:actual.compression.url,scriptId:'4'}}),
      breakpoint:()=>breakpoint({params:{breakpointId:'unknown',location:{}}}),resume:()=>resume(new Error('Fixed resume error')),
      descriptor:()=>Object.defineProperty(zlib,'createGzip',{enumerable:false})};
  `,
  )(Buffer, (bytes) => createHash('sha256').update(bytes).digest('hex'));
}
test('terminal qualification synchronously rechecks every loaded source without cleanup credit', () => {
  const control = terminalControl();
  assert.equal(control.run(), true);
  assert.deepEqual(control.read, ['1', '2', '3']);
});
for (const kind of ['duplicate', 'breakpoint', 'resume'])
  test('terminal qualification retains actual inherited ' + kind + ' failure', () => {
    const control = terminalControl();
    control[kind]();
    assert.throws(() => control.run());
    assert.deepEqual(control.read, []);
  });
test('terminal qualification refuses failed runtime, trace, changed descriptor and failed source rechecks', () => {
  for (const name of ['runtimeFailed', 'traceFailed']) {
    const control = terminalControl();
    control.state[name] = true;
    assert.throws(() => control.run());
  }
  const descriptor = terminalControl();
  descriptor.descriptor();
  assert.throws(() => descriptor.run());
  for (const name of ['late', 'error', 'changed']) {
    const control = terminalControl();
    control.state.sourceMode = name;
    assert.throws(() => control.run());
  }
});
test('watchdog refuses missing terminal qualification or prior sticky failure', () => {
  let watchdog;
  function walk(node) {
    if (ts.isMethodDeclaration(node) && node.name.getText(ast) === 'watchdog')
      watchdog = node.getText(ast);
    ts.forEachChild(node, walk);
  }
  walk(ast);
  assert.ok(watchdog);
  function run(failed, qualified) {
    const events = [];
    const fn = new Function(
      'assert',
      'failed',
      'selected',
      'finalCase',
      'performance',
      'event',
      'return ({' + watchdog + '}).watchdog;',
    )(
      assert,
      failed,
      {
        name: 'constructor-failure-gzip',
        unconfirmed: true,
        ownerSettled: false,
        handlerSettled: false,
        stopAt: 0,
      },
      'constructor-failure-gzip',
      { now: () => 5000 },
      (kind) => events.push(kind),
    );
    let refused = false;
    try {
      fn(qualified);
    } catch {
      refused = true;
    }
    return { refused, events };
  }
  assert.deepEqual(run(false, undefined), { refused: true, events: [] });
  assert.deepEqual(run(true, true), { refused: true, events: [] });
  assert.deepEqual(run(false, true), {
    refused: false,
    events: ['terminal-qualified', 'watchdog'],
  });
});

test('actual failed coordinator refuses a later arm even without an active event row', () => {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-fault-gap-')));
  const app = join(root, 'frontend/apps/web');
  mkdirSync(app, { recursive: true, mode: 0o700 });
  let observer,
    closed = false,
    notifications = 0;
  try {
    observer = createRouterFaultObserver(
      Object.freeze({ mode: 'production', identity: Object.freeze({ appRoot: app }) }),
      () => {
        notifications++;
      },
    );
    observer.fail();
    assert.equal(observer.failed(), true);
    assert.throws(() => observer.arm('pipe-before-selection', {}));
    assert.equal(readFileSync(join(root, '.router-fault.events')).length, 0);
    assert.ok(notifications > 0);
    assert.throws(() => observer.close());
    closed = true;
  } finally {
    if (observer && !closed) {
      try {
        observer.close();
      } catch {}
    }
    rmSync(root, { recursive: true });
  }
});

test('the pinned native inspector reads an actual loaded script synchronously', () => {
  const session = new Session();
  const filename = 'learnstack-router-terminal-source-control.js',
    source = '(() => 17)()';
  let scriptId;
  session.on('Debugger.scriptParsed', ({ params }) => {
    if (params.url === filename) {
      assert.equal(scriptId, undefined);
      scriptId = params.scriptId;
    }
  });
  session.connect();
  try {
    let enabled = false;
    session.post('Debugger.enable', (error) => {
      assert.ifError(error);
      enabled = true;
    });
    assert.equal(enabled, true);
    assert.equal(new Script(source, { filename }).runInThisContext(), 17);
    assert.match(scriptId, /^[0-9]+$/);
    let synchronous = false;
    session.post('Debugger.getScriptSource', { scriptId }, (error, result) => {
      assert.ifError(error);
      assert.equal(result.scriptSource, source);
      synchronous = true;
    });
    assert.equal(synchronous, true);
  } finally {
    session.disconnect();
  }
});
