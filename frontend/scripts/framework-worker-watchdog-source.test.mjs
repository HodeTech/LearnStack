// Pure emitted-render controls. The installed Next runtime is never imported or executed.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  instrumentFrameworkOwnershipMatrix,
  ownershipMatrixCases,
} from '../apps/web/scripts/framework-ownership-matrix-source.mjs';

const frontend = fileURLToPath(new URL('../', import.meta.url));
const ts = createRequire(join(frontend, 'apps/web/package.json'))('typescript');
const runtime = readFileSync(
  join(frontend, 'apps/web/scripts/public-server-runtime.mjs'),
  'utf8',
).replace(
  "server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {",
  "server.listen(0, '127.0.0.1', () => {",
);
const generated = instrumentFrameworkOwnershipMatrix({
  configuration: 'export default nextConfig;',
  runtime,
  child: '  child = createFrameworkRuntimeChildControl();',
  owner: '  const graceful =',
  rootPage: 'export default function Page() { return <main>Public</main>; }',
});

function execute(mutation) {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-worker-render-control-')));
  const app = join(root, 'frontend/apps/web'),
    controls = join(root, 'ownership-control');
  mkdirSync(app, { recursive: true });
  mkdirSync(controls, { mode: 0o700 });
  writeFileSync(join(controls, 'scenario'), 'worker-ordered-stop', { mode: 0o600 });
  writeFileSync(join(controls, 'events.jsonl'), '', { mode: 0o600 });
  writeFileSync(join(controls, 'render.arm'), '', { mode: 0o600 });
  try {
    // Replace only the irrelevant JSX return; execute the emitted gate itself.
    let source = generated.rootPage.replace('return <main>Public</main>;', "return 'Public';");
    assert.equal(source.split('framework.waitUntil(work);').length, 2);
    if (mutation === 'omit') source = source.replace('framework.waitUntil(work);', 'void work;');
    if (mutation === 'substitute')
      source = source.replace(
        'framework.waitUntil(work);',
        'framework.waitUntil(Promise.resolve());',
      );
    const output = ts.transpileModule(source, {
      compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
      reportDiagnostics: true,
    });
    assert.equal(output.diagnostics.length, 0);
    writeFileSync(join(app, 'page.mjs'), output.outputText, { mode: 0o600 });
    const probe = `import assert from 'node:assert/strict';
import {writeFileSync,readFileSync} from 'node:fs';
import Page from './page.mjs';
const registered=[];Object.defineProperty(globalThis,Symbol.for('@next/request-context'),{value:{get:()=>({waitUntil:work=>registered.push(work)})}});
const render=Page();assert.equal(registered.length,1);let settled=false;
registered[0].then(()=>{settled=true;});await new Promise(resolve=>setImmediate(resolve));
assert.equal(settled,false,'the actual registered promise remains pending with the render');
writeFileSync('../../../ownership-control/render.release','',{flag:'wx',mode:0o600});
assert.equal(await render,'Public');await registered[0];assert.equal(settled,true);
const events=readFileSync('../../../ownership-control/events.jsonl','utf8').trimEnd().split('\\n').map(JSON.parse);
assert.deepEqual(events.map(event=>event.event),['render-work-entered','render-work-returned']);`;
    const result = spawnSync(process.execPath, ['--input-type=module'], {
      cwd: app,
      input: probe,
      encoding: 'utf8',
      timeout: 2000,
    });
    assert.equal(result.signal, null);
    return result;
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
test('emitted page registers and awaits the same actual held promise', () => {
  const result = execute('none');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, '');
});
for (const mutation of ['omit', 'substitute'])
  test('dirty ' + mutation + ' cannot stand in for held registered work', () => {
    const result = execute(mutation);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /AssertionError/);
  });

// Execute the actual validator declarations, with no driver/Next/fixture startup imports.
// The extracted functions and finite vocabularies remain byte-exact source slices.
async function withFailureValidator(source, run) {
  const file = ts.createSourceFile(
    'matrix.mjs',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  assert.equal(file.parseDiagnostics.length, 0);
  const names = new Set([
    'exact',
    'groups',
    'operations',
    'failureCodes',
    'completedCaseMaximum',
    'failureCodeObservation',
    'eventNames',
    'admissionReasons',
    'readinessReasons',
    'ownerSignals',
    'validateReadinessObservation',
    'listenerFailureObservation',
    'validateEvents',
    'validateOwnerReceipt',
    'validateCompletedCase',
    'validateFailureEvidence',
    'projectFailureEvidence',
  ]);
  const selected = [];
  for (const statement of file.statements) {
    const name = ts.isFunctionDeclaration(statement)
      ? statement.name?.text
      : ts.isVariableStatement(statement) &&
          statement.declarationList.declarations.length === 1 &&
          ts.isIdentifier(statement.declarationList.declarations[0].name)
        ? statement.declarationList.declarations[0].name.text
        : undefined;
    if (names.has(name)) {
      names.delete(name);
      selected.push(statement.getText(file));
    }
  }
  assert.equal(names.size, 0, 'all actual validator dependencies selected exactly once');
  const support = ts.createSourceFile(
    'support.mjs',
    readFileSync(join(frontend, 'apps/web/scripts/bff-proof-support.mjs'), 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  assert.equal(support.parseDiagnostics.length, 0);
  const brands = new Set(['listenerFailures', 'bffListenerFailureDetail']);
  for (const statement of support.statements) {
    const name = ts.isFunctionDeclaration(statement)
      ? statement.name?.text
      : ts.isVariableStatement(statement) &&
          statement.declarationList.declarations.length === 1 &&
          ts.isIdentifier(statement.declarationList.declarations[0].name)
        ? statement.declarationList.declarations[0].name.text
        : undefined;
    if (brands.has(name)) {
      brands.delete(name);
      selected.push(statement.getText(support));
    }
  }
  assert.equal(brands.size, 0, 'actual WeakMap brand and accessor selected');
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-worker-failure-control-')));
  try {
    const path = join(root, 'validator.mjs');
    writeFileSync(
      path,
      "import assert from 'node:assert/strict';\nconst ownershipMatrixCases = " +
        JSON.stringify(ownershipMatrixCases) +
        ';\n' +
        selected.join('\n') +
        '\nexport {validateFailureEvidence, projectFailureEvidence, listenerFailureObservation, listenerFailures, failureCodeObservation};\n',
      { flag: 'wx', mode: 0o600 },
    );
    const module = await import(pathToFileURL(path).href);
    await run(module.validateFailureEvidence, module.projectFailureEvidence, module);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
function assertDiagnosticWiring(source) {
  const file = ts.createSourceFile(
    'matrix.mjs',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const functions = new Map();
  function visit(node) {
    if (ts.isFunctionDeclaration(node) && node.name) functions.set(node.name.text, node);
    ts.forEachChild(node, visit);
  }
  visit(file);
  const statements = (name) =>
    functions.get(name).body.statements.map((node) => node.getText(file).replace(/\s+/g, ''));
  assert.deepEqual(statements('launch').slice(0, 2), [
    "operation='launch';",
    'rootHttpStatus=null;',
  ]);
  for (const name of ['ready', 'worker']) {
    const body = statements(name);
    const ready = body.indexOf('awaitrunning.ready;');
    assert.equal(body[ready - 1], "operation='ready';");
    assert.deepEqual(
      body.slice(ready + 1, ready + 6),
      [
        "operation='http-root';",
        "constresponse=awaitown(running.get('/'));",
        'conststatus=response.status;',
        'rootHttpStatus=Number.isSafeInteger(status)&&status>=100&&status<=599?status:null;',
        'assert.equal(status,200);',
      ],
      name + ' observes one real root response before the original assertion',
    );
  }
  const entry = functions.get('runPrivateMatrix');
  const caught = entry.body.statements.findLast(ts.isTryStatement).catchClause;
  assert.equal(caught.variableDeclaration.name.getText(file), 'error');
  assert.deepEqual(
    caught.block.statements.slice(0, 2).map((node) => node.getText(file).replace(/\s+/g, '')),
    ['failureCode=failureCodeObservation(error);', 'constevidence=failureEvidence();'],
  );
  const snapshot = functions
    .get('failureEvidence')
    .body.statements.find(
      (node) =>
        ts.isVariableStatement(node) &&
        node.declarationList.declarations[0].name.getText(file) === 'evidence',
    ).declarationList.declarations[0].initializer;
  const values = new Map(
    snapshot.properties.map((node) => [
      node.name.getText(file),
      ts.isShorthandPropertyAssignment(node)
        ? node.name.getText(file)
        : node.initializer.getText(file),
    ]),
  );
  assert.equal(values.get('operation'), 'operation');
  assert.equal(values.get('failureCode'), 'failureCode');
  assert.equal(values.get('rootHttpStatus'), 'null');
  assert.ok(
    statements('failureEvidence').some((value) =>
      value.includes('if(evidence.current!==null)evidence.rootHttpStatus=rootHttpStatus;'),
    ),
  );
}
function failureCapsule() {
  return {
    group: 'workers',
    stage: 'worker-ordered-stop:hold-real-render',
    operation: 'worker-entered',
    rootHttpStatus: 200,
    failureCode: null,
    current: {
      case: 'worker-ordered-stop',
      mode: 'development',
      events: [],
      ownerReceipt: null,
      admissionReason: null,
      readiness: null,
    },
    completedCases: [],
    retained: true,
    cleanupConfirmed: false,
  };
}

test('failure and deadline paths share the actual closed held-render validator; omitted stage refuses', async () => {
  const source = readFileSync(
    join(frontend, 'apps/web/scripts/framework-ownership-matrix.mjs'),
    'utf8',
  );
  const file = ts.createSourceFile(
    'matrix.mjs',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const entry = file.statements.find(
    (statement) =>
      ts.isFunctionDeclaration(statement) && statement.name?.text === 'runPrivateMatrix',
  );
  assert.ok(entry);
  const paths = [];
  function visit(node) {
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'validateFailureEvidence'
    ) {
      assert.deepEqual(
        node.arguments.map((argument) => argument.getText(file)),
        ['evidence', 'group'],
      );
      let parent = node.parent,
        owner;
      while (parent && parent !== entry) {
        if (ts.isFunctionDeclaration(parent) && parent.name?.text === 'cancel') {
          owner = 'deadline';
          break;
        }
        if (ts.isCatchClause(parent)) owner = 'failure';
        parent = parent.parent;
      }
      paths.push(owner);
    }
    ts.forEachChild(node, visit);
  }
  visit(entry);
  assert.deepEqual(paths, ['deadline', 'failure']);
  assertDiagnosticWiring(source);
  for (const [anchor, mutant] of [
    ['failureCode = failureCodeObservation(error);', 'failureCode = null;'],
    [
      "operation = 'launch';\n    rootHttpStatus = null;",
      "operation = 'launch';\n    rootHttpStatus = 200;",
    ],
    ['const status = response.status;', 'const status = 200;'],
    ['rootHttpStatus = rootHttpStatus', 'rootHttpStatus = null'],
  ]) {
    assert.ok(source.includes(anchor));
    assert.throws(
      () => assertDiagnosticWiring(source.replace(anchor, mutant)),
      'actual observation wiring mutant is refused',
    );
  }

  await withFailureValidator(source, (validate, project, observations) => {
    for (const path of paths)
      assert.doesNotThrow(
        () => validate(failureCapsule(), 'workers'),
        path + ' finite capsule remains writable',
      );
    const clean = failureCapsule();
    clean.current.events = [
      {
        event: 'native-imported',
        case: 'worker-ordered-stop',
        pid: 8765,
        parentPid: 1234,
        milliseconds: 5,
      },
    ];
    assert.deepEqual(project(clean, 'workers'), {
      group: 'workers',
      stage: 'worker-ordered-stop:hold-real-render',
      currentCase: 'worker-ordered-stop',
      currentMode: 'development',
      completedCaseCount: 0,
      operation: 'worker-entered',
      rootHttpStatus: 200,
      failureCode: null,
      admissionReason: null,
      readiness: null,
      retained: true,
      cleanupConfirmed: false,
    });
    assert.doesNotMatch(JSON.stringify(project(clean, 'workers')), /pid|8765|1234/i);
    for (const code of [
      'ERR_ASSERTION',
      'ENOENT',
      'ESRCH',
      'EPERM',
      'EACCES',
      'ETIMEDOUT',
      'ECONNRESET',
      'ECONNREFUSED',
      'EPIPE',
    ]) {
      const error = Object.create(null, { code: { value: code } });
      assert.equal(observations.failureCodeObservation(error), code);
      const capsule = structuredClone(clean);
      capsule.failureCode = code;
      assert.equal(project(capsule, 'workers').failureCode, code);
    }
    let privateReads = 0;
    const dirtyError = { code: 'ERR_ASSERTION' };
    for (const key of ['stack', 'message', 'cause'])
      Object.defineProperty(dirtyError, key, {
        get() {
          privateReads++;
          throw new Error('PRIVATE-CANARY');
        },
      });
    assert.equal(observations.failureCodeObservation(dirtyError), 'ERR_ASSERTION');
    Object.defineProperty(dirtyError, 'code', {
      get() {
        privateReads++;
        throw new Error('PRIVATE-CANARY');
      },
    });
    assert.equal(observations.failureCodeObservation(dirtyError), null);
    assert.equal(privateReads, 0, 'no error getter, stack, message or cause is inspected');
    for (const error of [
      null,
      undefined,
      'PRIVATE-CANARY',
      1,
      Object.create({ code: 'ERR_ASSERTION' }),
      { code: 'PRIVATE-CANARY' },
      { code: Object('ERR_ASSERTION') },
      new Proxy(
        {},
        {
          getOwnPropertyDescriptor() {
            throw new Error('PRIVATE-CANARY');
          },
        },
      ),
    ])
      assert.equal(observations.failureCodeObservation(error), null);
    for (const key of ['operation', 'rootHttpStatus', 'failureCode']) {
      const omitted = structuredClone(clean);
      delete omitted[key];
      assert.throws(() => project(omitted, 'workers'));
    }
    for (const [key, values] of [
      ['operation', [null, undefined, '', 'PRIVATE-CANARY', 1, Object('ready')]],
      ['failureCode', [undefined, '', 'PRIVATE-CANARY', 1, Object('ERR_ASSERTION')]],
      ['rootHttpStatus', [undefined, 99, 600, 200.5, NaN, Infinity, '200', Object(200)]],
    ])
      for (const value of values) {
        const dirty = structuredClone(clean);
        dirty[key] = value;
        assert.throws(() => project(dirty, 'workers'));
      }
    for (const status of [null, 100, 200, 404, 599]) {
      const capsule = structuredClone(clean);
      capsule.rootHttpStatus = status;
      assert.equal(project(capsule, 'workers').rootHttpStatus, status);
    }
    for (const [group, maximum, scenario] of [
      ['startup', 17, 'ready-term'],
      ['workers', 5, 'worker-positive'],
      ['recovery', 7, 'prepare-disconnect'],
    ]) {
      const capsule = {
        ...failureCapsule(),
        group,
        stage: 'build',
        operation: 'build',
        current: null,
        rootHttpStatus: null,
      };
      const completed = {
        case: scenario,
        mode: group === 'workers' ? 'development' : 'production',
        events: [],
        ownerReceipt: null,
        productionManifest: {
          sha256: 'a'.repeat(64),
          inputs: 1,
          outputs: 1,
          node: '22.23.1',
          pnpm: '9.12.3',
        },
        outcome: { forced: false, ownerCode: 0, ownerSignal: null },
      };
      capsule.completedCases = Array(maximum).fill(completed);
      const projected = project(capsule, group);
      assert.equal(projected.completedCaseCount, maximum);
      assert.equal(projected.currentCase, null);
      assert.equal(projected.currentMode, null);
      assert.equal(projected.rootHttpStatus, null);
      capsule.rootHttpStatus = 200;
      assert.throws(() => project(capsule, group), 'no status without validated current case');
      capsule.rootHttpStatus = null;
      capsule.completedCases.push(completed);
      assert.throws(() => project(capsule, group), 'each group has its own case-count bound');
    }
    const branded = Object.freeze({
      reason: 'deadline',
      elapsedMilliseconds: 20000,
      exitCode: null,
      signal: 'none',
      frameworkReason: 'unobserved',
    });
    const error = new Error('PRIVATE-CANARY');
    observations.listenerFailures.set(error, branded); // Private extractor-only brand fixture.
    const observed = observations.listenerFailureObservation(error);
    assert.deepEqual(observed, {
      admissionReason: 'unobserved',
      readiness: {
        reason: 'deadline',
        elapsedMilliseconds: 20000,
        ownerExitCode: null,
        ownerSignal: 'none',
      },
    });
    assert.ok(Object.isFrozen(observed) && Object.isFrozen(observed.readiness));
    Object.assign(clean.current, observed);
    assert.deepEqual(project(clean, 'workers').readiness, observed.readiness);
    assert.doesNotMatch(JSON.stringify(project(clean, 'workers')), /PRIVATE-CANARY|exitCode|pid/);
    for (const reason of ['deadline', 'closed', 'error', 'shape'])
      for (const signal of ['none', 'SIGINT', 'SIGTERM', 'SIGKILL', 'other']) {
        observations.listenerFailures.set(error, {
          ...branded,
          reason,
          signal,
          elapsedMilliseconds: 120000,
          exitCode: 255,
        });
        assert.deepEqual(observations.listenerFailureObservation(error).readiness, {
          reason,
          elapsedMilliseconds: 120000,
          ownerExitCode: 255,
          ownerSignal: signal,
        });
      }
    let unbrandedReads = 0;
    const forged = {};
    for (const key of Object.keys(branded))
      Object.defineProperty(forged, key, {
        get() {
          unbrandedReads++;
          throw new Error('PRIVATE-CANARY');
        },
      });
    assert.equal(observations.listenerFailureObservation(forged), null);
    assert.equal(unbrandedReads, 0, 'arbitrary error fields cannot establish a brand');
    for (const key of Object.keys(branded)) {
      const reads = new Map();
      const changing = {};
      for (const field of Object.keys(branded))
        Object.defineProperty(changing, field, {
          enumerable: true,
          get() {
            const count = (reads.get(field) ?? 0) + 1;
            reads.set(field, count);
            return count === 1 ? branded[field] : 'PRIVATE-CANARY';
          },
        });
      observations.listenerFailures.set(error, changing);
      assert.deepEqual(observations.listenerFailureObservation(error), observed);
      assert.ok([...reads.values()].every((count) => count === 1));
      observations.listenerFailures.set(
        error,
        Object.defineProperty({ ...branded }, key, {
          get() {
            throw new Error('PRIVATE-CANARY');
          },
        }),
      );
      assert.equal(observations.listenerFailureObservation(error), null);
      const missing = { ...branded };
      delete missing[key];
      observations.listenerFailures.set(error, missing);
      assert.equal(observations.listenerFailureObservation(error), null);
    }
    for (const dirty of [
      { ...branded, extra: 'PRIVATE-CANARY' },
      { ...branded, reason: 'PRIVATE-CANARY' },
      { ...branded, signal: 'PRIVATE-CANARY' },
      { ...branded, frameworkReason: 'PRIVATE-CANARY' },
      ...[-1, 120001, 1.5, NaN, Infinity, Object(3)].map((elapsedMilliseconds) => ({
        ...branded,
        elapsedMilliseconds,
      })),
      ...[-1, 256, 1.5, NaN, Infinity, Object(3)].map((exitCode) => ({ ...branded, exitCode })),
    ]) {
      observations.listenerFailures.set(error, dirty);
      assert.equal(observations.listenerFailureObservation(error), null);
    }
    for (const readiness of [
      null,
      {},
      [],
      { ...observed.readiness, extra: 'PRIVATE-CANARY' },
      { ...observed.readiness, reason: 'PRIVATE-CANARY' },
      { ...observed.readiness, ownerSignal: 'PRIVATE-CANARY' },
      ...[-1, 120001, 1.5, null, '1'].map((elapsedMilliseconds) => ({
        ...observed.readiness,
        elapsedMilliseconds,
      })),
      ...[-1, 256, 1.5, '1'].map((ownerExitCode) => ({ ...observed.readiness, ownerExitCode })),
    ]) {
      const dirty = structuredClone(clean);
      dirty.current.readiness = readiness;
      assert.throws(() => project(dirty, 'workers'));
    }
    for (const key of Object.keys(observed.readiness)) {
      const dirty = structuredClone(clean);
      delete dirty.current.readiness[key];
      assert.throws(() => project(dirty, 'workers'));
    }
    const omitted = structuredClone(clean);
    delete omitted.current.readiness;
    assert.throws(() => project(omitted, 'workers'));
    const mismatched = structuredClone(clean);
    mismatched.current.admissionReason = null;
    assert.throws(() => project(mismatched, 'workers'));

    for (const mutate of [
      (v) => {
        v.extra = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.events[0].extra = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.admissionReason = 'PRIVATE-CANARY';
      },
      (v) => {
        v.stage = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.case = 'PRIVATE-CANARY';
      },
      (v) => {
        v.completedCases = Array(18).fill({});
      },
    ]) {
      const dirty = structuredClone(clean);
      mutate(dirty);
      assert.throws(() => project(dirty, 'workers'));
    }
    assert.throws(() => project(clean, 'startup'));
  });
  const entryLine = "    'worker-ordered-stop:hold-real-render',\n";
  assert.equal(source.split(entryLine).length, 2);
  await withFailureValidator(source.replace(entryLine, ''), (validate) => {
    for (const path of paths)
      assert.throws(
        () => validate(failureCapsule(), 'workers'),
        undefined,
        path + ' dirty omission refuses',
      );
  });
});

test('held-render failure stage is limited to the one worker case, development mode and workers group', async () => {
  const source = readFileSync(
    join(frontend, 'apps/web/scripts/framework-ownership-matrix.mjs'),
    'utf8',
  );
  await withFailureValidator(source, (validate) => {
    for (const mutate of [
      (value) => {
        value.stage = 'worker-positive:hold-real-render';
      },
      (value) => {
        value.stage = 'worker-ordered-stop:arbitrary-render';
      },
      (value) => {
        value.current.case = 'worker-positive';
      },
      (value) => {
        value.current.mode = 'production';
      },
      (value) => {
        value.current = null;
      },
    ]) {
      const value = failureCapsule();
      mutate(value);
      assert.throws(() => validate(value, 'workers'));
    }
    const wrongGroup = failureCapsule();
    wrongGroup.group = 'recovery';
    assert.throws(() => validate(wrongGroup, 'recovery'));
  });
});
