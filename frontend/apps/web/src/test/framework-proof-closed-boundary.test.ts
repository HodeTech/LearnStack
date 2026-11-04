// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import type * as ChildProcess from 'node:child_process';
import {
  existsSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  rmSync,
  rmdirSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';

import ts from 'typescript';
import { afterEach, expect, it, vi } from 'vitest';

import {
  bodylessFailureLine,
  ownershipDiagnostic,
  routerCountDiagnostic,
  routerDiagnostic,
} from './framework-finite-diagnostic';
import { closedFrameworkProofSync } from './framework-proof-closed-boundary';
import {
  runFrameworkOwnershipGroup,
  validateFrameworkOwnershipGroup,
  validateRetainedOwnershipEvidence,
} from './framework-runtime-ownership';

vi.mock('node:child_process', async (importOriginal) => ({
  ...(await importOriginal<typeof ChildProcess>()),
  spawnSync: vi.fn(),
}));

afterEach(() => vi.mocked(spawnSync).mockReset());
const canary = 'PRIVATE-CANARY-DO-NOT-PRINT';
function refuses(action: () => unknown) {
  let failure: unknown;
  try {
    action();
  } catch (error) {
    failure = error;
  }
  expect(failure).toBeInstanceOf(Error);
  const error = failure as Error;
  expect(error.message).toBe('Framework proof refused');
  expect(error.cause).toBeUndefined();
  expect(String(error.stack)).not.toContain(canary);
  expect(JSON.stringify(Object.getOwnPropertyDescriptors(error))).not.toContain(canary);
}

it('relays only one complete closed bodyless failure line', () => {
  const prefix = 'Framework bodyless proof failed: ';
  const line = prefix + 'https-204-empty-stream-early-held-peer-close-patched:peer-end\n';
  for (const clean of [
    line,
    prefix + 'installation\n',
    prefix + 'certificate:setup\n',
    prefix + 'pair-comparison:tls\n',
    prefix + 'http-fence-write-then-end-original:native-credit:0\n',
    prefix + 'https-200-large-stream-gzip-patched:native-credit:256\n',
  ])
    expect(bodylessFailureLine(clean)).toBe(clean);
  for (const dirty of [
    undefined,
    null,
    {},
    [],
    Buffer.from(line),
    Object(line),
    canary,
    prefix + canary + ':setup\n',
    prefix + 'certificate:' + canary + '\n',
    prefix + 'ftp-204-empty-direct-patched:setup\n',
    prefix + 'http-999-empty-direct-patched:setup\n',
    prefix + 'http-204-empty-direct-foreign:setup\n',
    prefix + 'installation:setup\n',
    prefix + 'certificate\n',
    prefix + 'certificate:setup:0\n',
    line.slice(0, -1),
    line.replace('\n', '\r\n'),
    '\n' + line,
    line + '\n',
    line + line,
    line + canary,
    canary + '\n' + line,
    line.replace(':peer-end', ':peer-end:' + canary),
    prefix + 'certificate:setup' + '\0\n',
    'x'.repeat(257),
    'é'.repeat(129),
    ...['', '-1', '257', '01', '+1', '1.5', 'Infinity', canary, '1:2'].map(
      (credits) => prefix + 'http-fence-write-original:native-credit:' + credits + '\n',
    ),
    prefix + 'http-fence-write-original:native-credit\n',
  ])
    expect(bodylessFailureLine(dirty)).toBeNull();
  let touched = false;
  expect(
    bodylessFailureLine({
      toString() {
        touched = true;
        throw new Error(canary);
      },
    }),
  ).toBeNull();
  expect(touched).toBe(false);
});

it('keeps bodyless receiver vocabulary equal to the unchanged finite producer', () => {
  const producer = readFileSync(
    new URL('../../../../scripts/framework-response-bodyless.test.mjs', import.meta.url),
    'utf8',
  );
  const receiver = readFileSync(
    new URL('./framework-finite-diagnostic.ts', import.meta.url),
    'utf8',
  );
  function initializer(text: string, name: string) {
    const file = ts.createSourceFile('input.ts', text, ts.ScriptTarget.Latest, true);
    const values: string[] = [];
    function visit(node: ts.Node) {
      if (
        ts.isVariableDeclaration(node) &&
        ts.isIdentifier(node.name) &&
        node.name.text === name &&
        node.initializer
      )
        values.push(
          (ts.isAsExpression(node.initializer)
            ? node.initializer.expression
            : node.initializer
          ).getText(file),
        );
      ts.forEachChild(node, visit);
    }
    visit(file);
    expect(values).toHaveLength(1);
    return values[0]!;
  }
  function parity(text: string) {
    const normal = Array.from(runInNewContext(initializer(text, 'CASES')) as { name: string }[]);
    const fence = Array.from(runInNewContext(initializer(text, 'FENCE')) as string[]);
    const names = [...normal.map((row) => row.name), ...fence.map((name) => 'fence-' + name)];
    const accepted = Array.from(
      runInNewContext(initializer(receiver, 'bodylessCases')) as string[],
    );
    expect(normal).toHaveLength(25);
    expect(fence).toHaveLength(6);
    expect(accepted).toEqual(names);
    const file = ts.createSourceFile('producer.mjs', text, ts.ScriptTarget.Latest, true);
    const phases = new Set<string>();
    const loops: Record<string, string[][]> = { transport: [], variant: [] };
    function visit(node: ts.Node) {
      const value =
        ts.isVariableDeclaration(node) && node.name.getText(file) === 'failurePhase'
          ? node.initializer
          : ts.isBinaryExpression(node) &&
              node.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
              node.left.getText(file) === 'failurePhase'
            ? node.right
            : undefined;
      if (value) {
        expect(ts.isStringLiteral(value)).toBe(true);
        if (!ts.isStringLiteral(value)) throw new Error('Bodyless phase vocabulary refused');
        phases.add(value.text);
      }
      if (ts.isForOfStatement(node))
        for (const name of ['transport', 'variant'])
          if (node.initializer.getText(file) === 'const ' + name) {
            expect(ts.isArrayLiteralExpression(node.expression)).toBe(true);
            if (!ts.isArrayLiteralExpression(node.expression))
              throw new Error('Bodyless loop vocabulary refused');
            loops[name]!.push(
              node.expression.elements.map((element) => {
                if (!ts.isStringLiteral(element)) throw new Error('Bodyless loop value refused');
                return element.text;
              }),
            );
          }
      ts.forEachChild(node, visit);
    }
    visit(file);
    for (const [name, expected] of [
      ['transport', ['http', 'https']],
      ['variant', ['original', 'patched']],
    ] as const) {
      expect(loops[name]).toHaveLength(2);
      for (const values of loops[name]!) expect(values).toEqual(expected);
    }
    const acceptedPhases = Array.from(
      runInNewContext(initializer(receiver, 'bodylessPhases')) as string[],
    );
    expect(acceptedPhases).toHaveLength(13);
    expect([...phases].sort()).toEqual([...acceptedPhases].sort());
    const stages = Array.from(
      runInNewContext(initializer(receiver, 'bodylessStages'), {
        bodylessCases: accepted,
      }) as string[],
    );
    expect(stages).toHaveLength(126);
    expect(new Set(stages).size).toBe(126);
    expect(stages).toEqual([
      'certificate',
      'pair-comparison',
      ...['http', 'https'].flatMap((transport) =>
        names.flatMap((name) =>
          ['original', 'patched'].map((variant) => `${transport}-${name}-${variant}`),
        ),
      ),
    ]);
    for (const stage of stages)
      for (const phase of acceptedPhases) {
        const line = `Framework bodyless proof failed: ${stage}:${phase}${phase === 'native-credit' ? ':256' : ''}\n`;
        expect(Buffer.byteLength(line)).toBeLessThanOrEqual(256);
        expect(bodylessFailureLine(line)).toBe(line);
      }
  }
  parity(producer);
  expect(() =>
    parity(producer.replace("{ name: '200-empty-direct' }", "{ name: 'foreign' }")),
  ).toThrow();
  expect(() =>
    parity(producer.replace("failurePhase = 'peer-end'", "failurePhase = 'foreign'")),
  ).toThrow();
});

it('keeps the actual bodyless wrapper assertions closed while relaying only validated failure bytes', () => {
  const text = readFileSync(
    new URL('./framework-response-bodyless.test.ts', import.meta.url),
    'utf8',
  );
  const file = ts.createSourceFile('wrapper.ts', text, ts.ScriptTarget.Latest, true);
  const actions: ts.ArrowFunction[] = [];
  function visit(node: ts.Node) {
    if (ts.isCallExpression(node) && node.expression.getText(file) === 'closedFrameworkProofSync') {
      expect(node.arguments).toHaveLength(1);
      const action = node.arguments[0]!;
      expect(ts.isArrowFunction(action)).toBe(true);
      if (ts.isArrowFunction(action)) actions.push(action);
    }
    ts.forEachChild(node, visit);
  }
  visit(file);
  expect(actions).toHaveLength(1);
  const action = actions[0]!
    .getText(file)
    .replaceAll(
      'import.meta.url',
      JSON.stringify('file:///frontend/apps/web/src/test/framework-response-bodyless.test.ts'),
    );
  const program = ts.transpileModule(`(${action})();`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
  }).outputText;
  const line = 'Framework bodyless proof failed: https-fence-write-patched:native-credit:1\n';
  const success = JSON.stringify({
    kind: 'qualified-response-bodyless',
    transports: 2,
    variants: 2,
    normalCases: 25,
    fenceCases: 6,
    total: 124,
    stockRetained: 1,
    patchedJoined: true,
    originalPatchedWireEqual: true,
    installedUnchanged: true,
  });
  for (const scenario of [
    { stderr: line, status: 3, close: false, delivered: line },
    { stderr: line, status: 0, close: false, delivered: line, spawnFault: true },
    { stderr: line, status: 0, close: false, delivered: line, signal: 'SIGTERM' },
    { stderr: canary, status: 3, close: false, delivered: '' },
    { stderr: line + canary, status: 3, close: false, delivered: '' },
    { stderr: line.slice(0, -1), status: 3, close: false, delivered: '' },
    { stderr: line, status: 3, close: true, delivered: '' },
    { stderr: line, status: 0, close: false, delivered: '' },
    { stderr: '', status: 0, close: false, delivered: '' },
  ]) {
    const writes: string[] = [];
    let cleanup = 0;
    let options: unknown;
    const run = () =>
      runInNewContext(program, {
        URL,
        // The injected Vitest expect.any(Number) compares constructor identity.
        Number,
        fileURLToPath,
        join,
        expect,
        bodylessFailureLine,
        process: { execPath: process.execPath, env: { PATH: process.env.PATH } },
        tmpdir: () => '/private-tmp',
        mkdtempSync: () => '/private-wrapper',
        realpathSync: (path: string) => path,
        readFileSync: () => '{"name":"@learnstack/web"}',
        rmSync: () => {
          cleanup++;
        },
        spawnSync: (_command: unknown, _args: unknown, received: unknown) => {
          options = received;
          return {
            error: 'spawnFault' in scenario ? new Error(canary) : undefined,
            signal: 'signal' in scenario ? scenario.signal : null,
            status: scenario.status,
            stdout: success,
            stderr: scenario.stderr,
          };
        },
        writeSync: (fd: number, value: string) => {
          expect(fd).toBe(2);
          if (scenario.close) throw new Error(canary);
          writes.push(value);
          return Buffer.byteLength(value);
        },
      });
    if (scenario.status === 0 && scenario.stderr === '') expect(run).not.toThrow();
    else refuses(() => closedFrameworkProofSync(run));
    expect(writes.join('')).toBe(scenario.delivered);
    expect(writes.join('')).not.toContain(canary);
    expect(cleanup).toBe(1);
    expect(options).toMatchObject({
      cwd: '/private-wrapper',
      encoding: 'utf8',
      timeout: 25_000,
      maxBuffer: 1024 * 1024,
      env: { PATH: process.env.PATH, NODE_ENV: 'production' },
    });
  }
});

it('keeps successful synchronous results unchanged and contains malformed output', () => {
  const value = Object.freeze({ ok: true });
  expect(closedFrameworkProofSync(() => value)).toBe(value);
  refuses(() => closedFrameworkProofSync(() => JSON.parse(canary)));
});

it('projects only closed ownership failure evidence and rejects dirty or oversized input', () => {
  const clean = {
    group: 'workers',
    stage: 'worker-ordered-stop:hold-real-render',
    currentCase: 'worker-ordered-stop',
    currentMode: 'development',
    completedCaseCount: 4,
    operation: 'retire',
    rootHttpStatus: 200,
    failureCode: 'ERR_ASSERTION',
    admissionReason: 'owner_busy',
    readiness: { reason: 'closed', elapsedMilliseconds: 10, ownerExitCode: 1, ownerSignal: 'none' },
    cleanupConfirmed: false,
    retained: true,
  };
  expect(ownershipDiagnostic(JSON.stringify(clean), 'workers')).toEqual({
    schema: 1,
    kind: 'framework-ownership-failure',
    ...clean,
  });
  for (const dirty of [
    { ...clean, extra: canary },
    { ...clean, stage: canary },
    { ...clean, currentCase: canary },
    { ...clean, admissionReason: canary },
    { ...clean, currentMode: canary },
    { ...clean, operation: canary },
    { ...clean, failureCode: canary },
    { ...clean, completedCaseCount: canary },
    { ...clean, rootHttpStatus: canary },
    { ...clean, retained: canary },
    { ...clean, group: canary },
    { ...clean, currentCase: null },
    { ...clean, oversized: canary.repeat(200) },
  ])
    expect(ownershipDiagnostic(JSON.stringify(dirty), 'workers')).toBeNull();
  expect(ownershipDiagnostic(canary, 'workers')).toBeNull();
  expect(ownershipDiagnostic(JSON.stringify(clean), 'startup')).toBeNull();
  const zero = {
    ...clean,
    readiness: { ...clean.readiness, elapsedMilliseconds: 0, ownerExitCode: 0 },
  };
  expect(ownershipDiagnostic(JSON.stringify(zero), 'workers')).toMatchObject(zero);
  const pending = { ...clean, admissionReason: null, readiness: null };
  expect(ownershipDiagnostic(JSON.stringify(pending), 'workers')).toMatchObject(pending);
  const absent = {
    ...pending,
    stage: 'build',
    currentCase: null,
    currentMode: null,
    rootHttpStatus: null,
  };
  expect(ownershipDiagnostic(JSON.stringify(absent), 'workers')).toMatchObject(absent);
  for (const [group, maximum] of [
    ['startup', 17],
    ['workers', 5],
    ['recovery', 7],
  ] as const) {
    for (const completedCaseCount of [0, maximum]) {
      const row = { ...absent, group, completedCaseCount };
      expect(ownershipDiagnostic(JSON.stringify(row), group)).toMatchObject(row);
    }
    for (const completedCaseCount of [-1, maximum + 1, 1.5, null, '1'])
      expect(
        ownershipDiagnostic(JSON.stringify({ ...absent, group, completedCaseCount }), group),
      ).toBeNull();
  }
  for (const currentMode of ['production', 'development']) {
    const row = {
      ...clean,
      stage: 'worker-exit-zero:start',
      currentCase: 'worker-exit-zero',
      currentMode,
    };
    expect(ownershipDiagnostic(JSON.stringify(row), 'workers')).toMatchObject(row);
  }
  for (const dirty of [
    { ...clean, currentMode: 'production' },
    { ...clean, currentMode: null },
    { ...absent, currentMode: 'production' },
    { ...absent, rootHttpStatus: 200 },
    ...[-1, 99, 600, 1.5, '200', {}, []].map((rootHttpStatus) => ({ ...clean, rootHttpStatus })),
  ])
    expect(ownershipDiagnostic(JSON.stringify(dirty), 'workers')).toBeNull();
  for (const rootHttpStatus of [null, 100, 200, 503, 599]) {
    const row = { ...clean, rootHttpStatus };
    expect(ownershipDiagnostic(JSON.stringify(row), 'workers')).toMatchObject(row);
  }
  for (const failureCode of [
    null,
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
    const row = { ...clean, failureCode };
    expect(ownershipDiagnostic(JSON.stringify(row), 'workers')).toMatchObject(row);
  }
  for (const operation of [
    'build',
    'launch',
    'ready',
    'http-root',
    'signal',
    'retire',
    'row',
    'worker-request',
    'worker-entered',
    'worker-identity',
    'records',
    'worker-fault',
    'runtime-absent',
    'competitor',
    'worker-release',
    'worker-response',
    'worker-returned',
    'owner-completed',
    'restart-build',
    'cleanup',
  ]) {
    const row = { ...clean, operation };
    expect(ownershipDiagnostic(JSON.stringify(row), 'workers')).toMatchObject(row);
  }
  for (const key of Object.keys(clean)) {
    const dirty: Record<string, unknown> = { ...clean };
    delete dirty[key];
    expect(ownershipDiagnostic(JSON.stringify(dirty), 'workers')).toBeNull();
  }
  const raw = JSON.stringify(clean);
  expect(
    ownershipDiagnostic(raw + ' '.repeat(2048 - Buffer.byteLength(raw)), 'workers'),
  ).toMatchObject(clean);
  expect(
    ownershipDiagnostic(raw + ' '.repeat(2049 - Buffer.byteLength(raw)), 'workers'),
  ).toBeNull();
  for (const reason of ['deadline', 'closed', 'error', 'shape'])
    for (const ownerSignal of ['none', 'SIGINT', 'SIGTERM', 'SIGKILL', 'other']) {
      const observed = {
        ...clean,
        admissionReason: 'unobserved',
        readiness: {
          reason,
          elapsedMilliseconds: 120000,
          ownerExitCode: null,
          ownerSignal,
        },
      };
      expect(ownershipDiagnostic(JSON.stringify(observed), 'workers')).toMatchObject(observed);
    }
  for (const readiness of [
    null,
    {},
    [],
    { ...clean.readiness, extra: canary },
    { ...clean.readiness, reason: canary },
    { ...clean.readiness, ownerSignal: canary },
    ...[-1, 120001, 1.5, null, '1'].map((elapsedMilliseconds) => ({
      ...clean.readiness,
      elapsedMilliseconds,
    })),
    ...[-1, 256, 1.5, '1'].map((ownerExitCode) => ({ ...clean.readiness, ownerExitCode })),
  ])
    expect(ownershipDiagnostic(JSON.stringify({ ...clean, readiness }), 'workers')).toBeNull();
  for (const key of Object.keys(clean.readiness)) {
    const readiness: Record<string, unknown> = { ...clean.readiness };
    delete readiness[key];
    expect(ownershipDiagnostic(JSON.stringify({ ...clean, readiness }), 'workers')).toBeNull();
  }
  expect(
    ownershipDiagnostic(JSON.stringify({ ...clean, admissionReason: null }), 'workers'),
  ).toBeNull();
  expect(
    ownershipDiagnostic(JSON.stringify({ ...clean, readiness: undefined }), 'workers'),
  ).toBeNull();
  const opaque = Object.defineProperty({}, 'readiness', {
    get() {
      throw new Error(canary);
    },
  });
  expect(ownershipDiagnostic(opaque, 'workers')).toBeNull();
});

it('projects router process classifications without exporting caught payloads', () => {
  const script = 'framework-router-fault.test.mjs';
  const bad = {
    code: 'ETIMEDOUT',
    status: null,
    signal: 'SIGTERM',
    stdout: canary,
    stderr: canary,
    message: canary,
    stack: canary,
    extra: canary,
  };
  const observed = routerDiagnostic(script, bad, 10040);
  expect(observed).toEqual({
    schema: 1,
    kind: 'framework-router-control-failure',
    script,
    class: 'timeout',
    status: null,
    signal: 'SIGTERM',
    elapsedMs: 10040,
    completedControls: 0,
  });
  expect(JSON.stringify(observed)).not.toContain(canary);
  expect(routerDiagnostic(canary, bad, 1)).toBeNull();
  expect(routerDiagnostic(script, { status: 3, message: canary }, 4)?.class).toBe('status');
  expect(routerDiagnostic(script, { signal: 'SIGKILL', message: canary }, 4)?.class).toBe('signal');
  expect(routerCountDiagnostic(script, 'tests', 25)).toMatchObject({
    class: 'count-mismatch',
    counter: 'tests',
    elapsedMs: 25,
  });
  expect(routerCountDiagnostic(script, canary, 25)).toBeNull();
  expect(routerCountDiagnostic(canary, 'tests', 25)).toBeNull();
  expect(JSON.stringify(routerCountDiagnostic(script, 'pass', Infinity))).not.toContain(canary);
  expect(routerDiagnostic(script, { code: 'ENOENT' }, 1)?.class).toBe('spawn');
  expect(routerDiagnostic(script, { code: canary }, Infinity)).toMatchObject({
    class: 'unknown',
    status: null,
    signal: null,
    elapsedMs: null,
  });
  const reads = { status: 0, signal: 0, code: 0 };
  const changing = {
    get status() {
      return ++reads.status === 1 ? 3 : canary;
    },
    get signal() {
      return ++reads.signal === 1 ? 'SIGTERM' : canary;
    },
    get code() {
      return ++reads.code === 1 ? 'ETIMEDOUT' : canary;
    },
  };
  const snapshot = routerDiagnostic(script, changing, 4);
  expect(reads).toEqual({ status: 1, signal: 1, code: 1 });
  expect(snapshot).toMatchObject({ class: 'timeout', status: 3, signal: 'SIGTERM' });
  expect(JSON.stringify(snapshot)).not.toContain(canary);
  for (const status of [Object(3), NaN, Infinity, -1, 256, 1.5, Number.MAX_SAFE_INTEGER])
    expect(routerDiagnostic(script, { status }, 4)).toMatchObject({
      class: 'unknown',
      status: null,
    });
  for (const field of ['status', 'signal', 'code'])
    expect(
      routerDiagnostic(
        script,
        Object.defineProperty({}, field, {
          get() {
            throw new Error(canary);
          },
        }),
        1,
      ),
    ).toBeNull();
  expect(
    routerDiagnostic(
      script,
      Object.defineProperty({}, 'code', {
        get() {
          throw new Error(canary);
        },
      }),
      1,
    ),
  ).toBeNull();
});

it('observes only a bounded contiguous TAP prefix without granting success or reading getters', () => {
  const script = 'framework-router-fault.test.mjs';
  const failed = (stdout: unknown) => ({
    code: 'ETIMEDOUT',
    status: 1,
    signal: null,
    stdout,
    stderr: canary,
    message: canary,
  });
  const observe = (stdout: unknown) => routerDiagnostic(script, failed(stdout), 10000);
  const passed = (count: number) =>
    Array.from({ length: count }, (_, index) => `ok ${index + 1} - ${canary}`).join('\n') + '\n';
  for (const [name, maximum] of [
    ['framework-router-fault.test.mjs', 31],
    ['framework-router-fault-gate.test.mjs', 10],
    ['framework-router-registration.test.mjs', 4],
    ['framework-router-next-chain.test.mjs', 3],
    ['framework-router-fault-prefix.test.mjs', 13],
    ['framework-router-registration-capture.test.mjs', 9],
  ] as const) {
    const result = routerDiagnostic(name, failed(passed(maximum)), 10000);
    expect(result).toMatchObject({ class: 'timeout', status: 1, completedControls: maximum });
    expect(JSON.stringify(result)).not.toContain(canary);
    expect(
      routerDiagnostic(name, failed(passed(maximum + 1)), 10000)?.completedControls,
    ).toBeNull();
  }
  expect(observe(passed(3) + `not ok 4 - ${canary}\nok 5 - ${canary}\n`)).toMatchObject({
    class: 'timeout',
    completedControls: 3,
  });
  expect(observe(`not ok 1 - ${canary}\n`)?.completedControls).toBe(0);
  expect(observe('ok 1 - partial')?.completedControls).toBe(0);
  expect(observe(passed(1) + 'ok 2 - partial')?.completedControls).toBe(1);
  for (const stdout of ['', canary, `# ${canary}\n  ok 1 - ${canary}\n`])
    expect(observe(stdout)?.completedControls).toBe(0);
  expect(observe(passed(2).replaceAll('\n', '\r\n'))?.completedControls).toBe(2);
  for (const stdout of [
    `ok 2 - ${canary}\n`,
    passed(1) + `ok 3 - ${canary}\n`,
    passed(1) + `ok 1 - ${canary}\n`,
    `ok 0 - ${canary}\n`,
    `ok 01 - ${canary}\n`,
    `ok 1 ${canary}\n`,
    `ok 9007199254740992 - ${canary}\n`,
    'x'.repeat(65537),
    'é'.repeat(32769),
    Buffer.alloc(65537),
    Buffer.from([0xff]),
    undefined,
    null,
    {},
    [],
    Object(passed(1)),
    new Uint8Array([1]),
  ])
    expect(observe(stdout)?.completedControls).toBeNull();
  const exact = passed(1) + '#'.repeat(65536 - Buffer.byteLength(passed(1)));
  expect(observe(exact)?.completedControls).toBe(1);
  expect(observe(Buffer.from(exact, 'utf8'))?.completedControls).toBe(1);
  const padded = Buffer.from('XX' + passed(2) + 'YY');
  expect(observe(padded.subarray(2, padded.length - 2))?.completedControls).toBe(2);
  let reads = 0;
  const getter = Object.defineProperty({ code: 'ETIMEDOUT' }, 'stdout', {
    get() {
      reads++;
      throw new Error(canary);
    },
  });
  expect(routerDiagnostic(script, getter, 10000)?.completedControls).toBeNull();
  const inherited = Object.create({ stdout: passed(1) });
  expect(routerDiagnostic(script, inherited, 10000)?.completedControls).toBeNull();
  const bytes = Buffer.from(passed(2), 'utf8');
  for (const name of ['toString', 'length', 'byteLength', 'byteOffset', 'buffer'])
    Object.defineProperty(bytes, name, {
      get() {
        reads++;
        throw new Error(canary);
      },
    });
  const result = observe(bytes);
  expect(result?.completedControls).toBe(2);
  expect(reads).toBe(0);
  expect(JSON.stringify(result)).not.toContain(canary);
  expect(routerDiagnostic(script, { code: 'ETIMEDOUT' }, 10000)?.completedControls).toBeNull();
});

// The receiver intentionally revalidates. Its finite set must track the actual validator.
it('keeps receiver cases, stages and admission reasons equal to matrix authority', () => {
  const matrix = readFileSync(
    new URL('../../scripts/framework-ownership-matrix.mjs', import.meta.url),
    'utf8',
  );
  const source = readFileSync(
    new URL('../../scripts/framework-ownership-matrix-source.mjs', import.meta.url),
    'utf8',
  );
  const receiver = readFileSync(
    new URL('./framework-finite-diagnostic.ts', import.meta.url),
    'utf8',
  );
  function initializer(text: string, name: string): string {
    const file = ts.createSourceFile(
      'input.ts',
      text,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TS,
    );
    const found: string[] = [];
    function visit(node: ts.Node) {
      if (
        ts.isVariableDeclaration(node) &&
        ts.isIdentifier(node.name) &&
        node.name.text === name &&
        node.initializer
      ) {
        const value = ts.isAsExpression(node.initializer)
          ? node.initializer.expression
          : node.initializer;
        found.push(value.getText(file));
      }
      ts.forEachChild(node, visit);
    }
    visit(file);
    expect(found).toHaveLength(1);
    return found[0]!;
  }
  const matrixCases = Array.from(
    runInNewContext(initializer(source, 'ownershipMatrixCases')) as string[],
  );
  const receiverCases = Array.from(runInNewContext(initializer(receiver, 'cases')) as string[]);
  const support = readFileSync(
    new URL('../../scripts/bff-proof-support.mjs', import.meta.url),
    'utf8',
  );
  function listenerVocabulary(text: string) {
    const file = ts.createSourceFile(
      'support.mjs',
      text,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.JS,
    );
    const listener = file.statements.filter(
      (node): node is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(node) && node.name?.text === 'waitForBffProofListener',
    );
    expect(listener).toHaveLength(1);
    const mintedReasons: string[] = [],
      mintedSignals: string[] = [];
    let signalFields = 0;
    function signals(node: ts.Node) {
      if (ts.isStringLiteral(node)) mintedSignals.push(node.text);
      ts.forEachChild(node, signals);
    }
    function visit(node: ts.Node) {
      if (ts.isCallExpression(node) && node.expression.getText(file) === 'refusal') {
        const reason = node.arguments[0];
        expect(reason && ts.isStringLiteral(reason)).toBe(true);
        if (reason && ts.isStringLiteral(reason)) mintedReasons.push(reason.text);
      }
      if (ts.isPropertyAssignment(node) && node.name.getText(file) === 'signal') {
        signalFields++;
        signals(node.initializer);
      }
      ts.forEachChild(node, visit);
    }
    visit(listener[0]!);
    expect(signalFields).toBe(1);
    expect([...new Set(mintedReasons)].sort()).toEqual(
      Array.from(runInNewContext(initializer(matrix, 'readinessReasons')) as string[]).sort(),
    );
    expect([...new Set(mintedSignals)].sort()).toEqual(
      Array.from(runInNewContext(initializer(matrix, 'ownerSignals')) as string[]).sort(),
    );
  }
  listenerVocabulary(support);
  expect(() =>
    listenerVocabulary(support.replace("refusal('deadline',", "refusal('foreign',")),
  ).toThrow();
  expect(() => listenerVocabulary(support.replace("? 'none'", "? 'foreign'"))).toThrow();
  const matrixReasons = Array.from(
    runInNewContext(initializer(matrix, 'admissionReasons')) as string[],
  );
  const receiverReasons = Array.from(runInNewContext(initializer(receiver, 'reasons')) as string[]);
  const matrixStages = Array.from(
    runInNewContext(initializer(matrix, 'stages'), {
      ownershipMatrixCases: matrixCases,
    }) as string[],
  );
  const receiverStages = Array.from(
    runInNewContext(initializer(receiver, 'stages'), { cases: receiverCases }) as string[],
  );
  for (const [actual, expected] of [
    [receiverCases, matrixCases],
    [receiverReasons, matrixReasons],
    [receiverStages, matrixStages],
    ...['readinessReasons', 'ownerSignals', 'operations', 'failureCodes'].map(
      (name) =>
        [
          Array.from(runInNewContext(initializer(receiver, name)) as string[]),
          Array.from(runInNewContext(initializer(matrix, name)) as string[]),
        ] as const,
    ),
  ] as const) {
    expect(actual).toEqual(expected);
    expect(actual.slice(1)).not.toEqual(expected);
    expect([...actual, canary]).not.toEqual(expected);
  }
  expect(runInNewContext('(' + initializer(receiver, 'completedCaseMaximum') + ')')).toEqual(
    runInNewContext('(' + initializer(matrix, 'completedCaseMaximum') + ')'),
  );
  const routerWrapper = readFileSync(
    new URL('./framework-router-faults.cases.ts', import.meta.url),
    'utf8',
  );
  function routerCaps(text: string) {
    const file = ts.createSourceFile(
      'router.ts',
      text,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TS,
    );
    const entries: Array<readonly [string, number]> = [];
    let signalThreshold: ts.Expression | undefined;
    let caseThreshold: ts.Expression | undefined;
    function visit(node: ts.Node) {
      if (ts.isCallExpression(node) && node.expression.getText(file) === 'execFileSync') {
        const args = node.arguments[1];
        if (
          args &&
          ts.isArrayLiteralExpression(args) &&
          args.elements[0]?.getText(file) === "'--test'"
        ) {
          const options = node.arguments[2];
          if (!options || !ts.isObjectLiteralExpression(options))
            throw new Error('Router control options refused');
          const setting = options.properties.find(
            (property) =>
              ts.isPropertyAssignment(property) && property.name.getText(file) === 'timeout',
          );
          if (!setting || !ts.isPropertyAssignment(setting) || signalThreshold)
            throw new Error('Router control signal threshold refused');
          signalThreshold = setting.initializer;
        }
      }
      if (ts.isCallExpression(node) && node.expression.getText(file) === 'test') {
        const title = node.arguments[0];
        if (
          title &&
          ts.isStringLiteral(title) &&
          title.text ===
            'closed router fault source, native factory and finite-evidence controls pass'
        )
          caseThreshold = node.arguments[2];
      }
      if (ts.isForOfStatement(node) && node.initializer.getText(file) === 'const [script, count]') {
        const expression = ts.isAsExpression(node.expression)
          ? node.expression.expression
          : node.expression;
        expect(ts.isArrayLiteralExpression(expression)).toBe(true);
        if (!ts.isArrayLiteralExpression(expression))
          throw new Error('Router control census refused');
        for (const entry of expression.elements) {
          expect(ts.isArrayLiteralExpression(entry)).toBe(true);
          if (!ts.isArrayLiteralExpression(entry) || entry.elements.length !== 2)
            throw new Error('Router control census refused');
          const [name, count] = entry.elements;
          if (!name || !count || !ts.isStringLiteral(name) || !ts.isNumericLiteral(count))
            throw new Error('Router control census refused');
          entries.push([name.text, Number(count.text)]);
        }
      }
      ts.forEachChild(node, visit);
    }
    visit(file);
    expect(entries).toHaveLength(6);
    expect(signalThreshold).toBeDefined();
    expect(caseThreshold).toBeDefined();
    const setting = signalThreshold!.getText(file);
    for (const [script] of [...entries, ['foreign-script.mjs', 0] as const])
      expect(runInNewContext(setting, { script })).toBe(
        script === 'framework-router-fault.test.mjs' ? 20_000 : 10_000,
      );
    expect(runInNewContext(caseThreshold!.getText(file))).toBe(90_000);
    return Object.fromEntries(entries);
  }
  const receiverCaps = runInNewContext('(' + initializer(receiver, 'scriptCaps') + ')');
  expect(receiverCaps).toEqual(routerCaps(routerWrapper));
  expect(Object.keys(receiverCaps)).toEqual(
    Array.from(runInNewContext(initializer(receiver, 'scripts'))),
  );
  expect(receiverCaps).not.toEqual(
    routerCaps(
      routerWrapper.replace(
        "['framework-router-fault.test.mjs', 31]",
        "['framework-router-fault.test.mjs', 30]",
      ),
    ),
  );
});

it('keeps the validated matrix projection before the single bounded stdout write', () => {
  const source = readFileSync(
    new URL('../../scripts/framework-ownership-matrix.mjs', import.meta.url),
    'utf8',
  );
  function census(text: string) {
    const file = ts.createSourceFile(
      'matrix.mjs',
      text,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.JS,
    );
    const functions = file.statements.filter(ts.isFunctionDeclaration);
    const project = functions.filter((node) => node.name?.text === 'projectFailureEvidence');
    const parent = functions.filter((node) => node.name?.text === 'runFrameworkOwnershipMatrix');
    expect(project).toHaveLength(1);
    expect(parent).toHaveLength(1);
    const privateMatrix = functions.filter((node) => node.name?.text === 'runPrivateMatrix');
    expect(privateMatrix).toHaveLength(1);
    const readinessCallbacks: ts.ArrowFunction[] = [];
    function visit(node: ts.Node) {
      if (ts.isCallExpression(node) && node.expression.getText(file) === 'running.ready.catch') {
        expect(node.arguments).toHaveLength(1);
        const callback = node.arguments[0]!;
        expect(ts.isArrowFunction(callback)).toBe(true);
        if (ts.isArrowFunction(callback)) readinessCallbacks.push(callback);
      }
      ts.forEachChild(node, visit);
    }
    visit(privateMatrix[0]!);
    expect(readinessCallbacks).toHaveLength(1);
    const observation = readinessCallbacks[0]!.body;
    expect(ts.isBlock(observation)).toBe(true);
    if (!ts.isBlock(observation)) throw new Error('Readiness observation refused');
    expect(observation.statements).toHaveLength(2);
    expect(observation.statements[0]!.getText(file)).toBe(
      'const observed = listenerFailureObservation(error);',
    );
    const accepted = observation.statements[1]!;
    expect(ts.isIfStatement(accepted)).toBe(true);
    if (!ts.isIfStatement(accepted) || !ts.isBlock(accepted.thenStatement))
      throw new Error('Readiness observation refused');
    expect(accepted.expression.getText(file)).toBe('observed !== null');
    expect(accepted.thenStatement.statements.map((node) => node.getText(file))).toEqual([
      'current.admissionReason = observed.admissionReason;',
      'current.readiness = observed.readiness;',
    ]);
    const body = project[0]!.body!;
    expect(body.statements).toHaveLength(2);
    expect(body.statements[0]!.getText(file)).toBe('validateFailureEvidence(evidence, group);');
    const result = body.statements[1]!;
    expect(
      ts.isReturnStatement(result) &&
        result.expression &&
        ts.isObjectLiteralExpression(result.expression),
    ).toBe(true);
    if (
      !ts.isReturnStatement(result) ||
      !result.expression ||
      !ts.isObjectLiteralExpression(result.expression)
    )
      throw new Error('Projection refused');
    expect(
      result.expression.properties.map((prop) =>
        ts.isPropertyAssignment(prop) ? prop.name.getText(file) : 'invalid',
      ),
    ).toEqual([
      'group',
      'stage',
      'currentCase',
      'currentMode',
      'completedCaseCount',
      'operation',
      'rootHttpStatus',
      'failureCode',
      'admissionReason',
      'readiness',
      'cleanupConfirmed',
      'retained',
    ]);
    const textParent = parent[0]!.getText(file);
    expect(textParent.split('projectFailureEvidence(evidence, group)')).toHaveLength(2);
    expect(textParent.split("writeSync(1, JSON.stringify(projected) + '\\n')")).toHaveLength(2);
    expect(textParent.indexOf('projectFailureEvidence(evidence, group)')).toBeLessThan(
      textParent.indexOf('writeSync(1, JSON.stringify(projected)'),
    );
  }
  census(source);
  for (const [before, after] of [
    ['const observed = listenerFailureObservation(error);', 'const observed = null;'],
    ['current.readiness = observed.readiness;', 'void observed.readiness;'],
  ] as const)
    expect(() => census(source.replace(before, after))).toThrow();
  expect(() =>
    census(source.replace('  validateFailureEvidence(evidence, group);', '  void evidence;')),
  ).toThrow();
  expect(() =>
    census(
      source.replace(
        '    retained: evidence.retained,',
        '    retained: evidence.retained, leaked: evidence.current,',
      ),
    ),
  ).toThrow();
});

// Each run copies/transpiles the actual wrapper with an inert driver and a finite
// inner-result observation. Only the closed-sink copy additionally closes stderr
// after the child result, immediately before the actual diagnostic emitter.
it('keeps actual wrapper failures generic while emitting finite observations', async () => {
  const actual = await vi.importActual<typeof ChildProcess>('node:child_process');
  const root = mkdtempSync(join(tmpdir(), 'learnstack-finite-wrapper-'));
  let safeToRemove = false;
  try {
    const app = join(root, 'frontend/apps/web');
    const testDir = join(app, 'src/test');
    const script = join(app, 'scripts/finite-diagnostic-control.mjs');
    mkdirSync(testDir, { recursive: true });
    mkdirSync(join(app, 'scripts'), { recursive: true });
    const original = readFileSync(
      new URL('./framework-runtime-ownership.ts', import.meta.url),
      'utf8',
    );
    const driver = 'scripts/verify-framework-runtime-ownership.mjs';
    expect(original.split(driver)).toHaveLength(2);
    const observationAnchor =
      '    if (result.error !== undefined || result.signal !== null || result.status !== 0)';
    expect(original.split(observationAnchor)).toHaveLength(2);
    const observation =
      "    process.stdout.write(JSON.stringify({ status: result.status, signal: result.signal, spawnFault: result.error !== undefined }) + '\\n');\n";
    const transpile = (text: string) =>
      ts.transpileModule(text, {
        compilerOptions: {
          module: ts.ModuleKind.ESNext,
          target: ts.ScriptTarget.ES2022,
        },
      }).outputText;
    for (const close of [false, true]) {
      const source =
        (close ? "import { closeSync as finiteCloseSync } from 'node:fs';\n" : '') +
        original
          .replace(driver, 'scripts/finite-diagnostic-control.mjs')
          .replace(
            observationAnchor,
            observation + (close ? '    finiteCloseSync(2);\n' : '') + observationAnchor,
          );
      // Rewrite emitted relative specifiers solely so the private .mjs copies resolve.
      const wrapper = transpile(source)
        .replace("'./framework-proof-closed-boundary'", "'./framework-proof-closed-boundary.mjs'")
        .replace("'./framework-finite-diagnostic'", "'./framework-finite-diagnostic.mjs'");
      expect(wrapper).toContain("'./framework-proof-closed-boundary.mjs'");
      expect(wrapper).toContain("'./framework-finite-diagnostic.mjs'");
      writeFileSync(
        join(
          testDir,
          close ? 'framework-runtime-ownership-closed.mjs' : 'framework-runtime-ownership.mjs',
        ),
        wrapper,
      );
    }
    for (const name of ['framework-proof-closed-boundary', 'framework-finite-diagnostic'])
      writeFileSync(
        join(testDir, name + '.mjs'),
        transpile(readFileSync(new URL('./' + name + '.ts', import.meta.url), 'utf8')),
      );
    const clean = {
      group: 'workers',
      stage: 'worker-ordered-stop:hold-real-render',
      currentCase: 'worker-ordered-stop',
      currentMode: 'development',
      completedCaseCount: 4,
      operation: 'retire',
      rootHttpStatus: 200,
      failureCode: 'ERR_ASSERTION',
      admissionReason: 'owner_busy',
      readiness: {
        reason: 'closed',
        elapsedMilliseconds: 10,
        ownerExitCode: 1,
        ownerSignal: 'none',
      },
      cleanupConfirmed: false,
      retained: true,
    };
    const scenarios = [
      { stdout: JSON.stringify(clean), diagnostic: true, close: false },
      { stdout: canary, diagnostic: false, close: false },
      { stdout: JSON.stringify({ ...clean, extra: canary }), diagnostic: false, close: false },
      {
        stdout: JSON.stringify({ ...clean, extra: canary.repeat(200) }),
        diagnostic: false,
        close: false,
      },
      { stdout: JSON.stringify({ ...clean, group: 'startup' }), diagnostic: false, close: false },
      { stdout: '', diagnostic: false, close: false },
      { stdout: JSON.stringify(clean), diagnostic: false, close: true },
    ];
    for (const scenario of scenarios) {
      // The inert program writes only these bounded bytes and cannot fork or queue work.
      expect(Buffer.byteLength(scenario.stdout, 'utf8')).toBeLessThan(16_384);
      expect(Buffer.byteLength(canary, 'utf8')).toBeLessThan(16_384);
      writeFileSync(
        script,
        `process.stdout.write(${JSON.stringify(scenario.stdout)});process.stderr.write(${JSON.stringify(canary)});process.exitCode=3;`,
      );
      const wrapperPath = join(
        testDir,
        scenario.close
          ? 'framework-runtime-ownership-closed.mjs'
          : 'framework-runtime-ownership.mjs',
      );
      const probe = `import {runFrameworkOwnershipGroup} from ${JSON.stringify(pathToFileURL(wrapperPath).href)};
        try {runFrameworkOwnershipGroup('workers');process.stdout.write('unexpected-success');}
        catch(e) {process.stdout.write(e instanceof Error && e.message==='Framework proof refused' && e.cause===undefined ? 'generic-failure' : 'unexpected-error');}`;
      const result = actual.spawnSync(process.execPath, ['--input-type=module', '-e', probe], {
        encoding: 'utf8',
        timeout: 10_000,
        maxBuffer: 16_384,
        stdio: ['ignore', 'pipe', 'pipe'],
        env: {
          PATH: process.env.PATH,
          NODE_ENV: 'production',
          NODE_TLS_REJECT_UNAUTHORIZED: '1',
          NEXT_TELEMETRY_DISABLED: '1',
        },
      });
      expect(result.error).toBeUndefined();
      expect(result.status).toBe(0);
      expect(result.signal).toBeNull();
      expect(result.stdout).not.toContain(canary);
      const outerRows = result.stdout.trim().split('\n');
      expect(outerRows).toHaveLength(2);
      expect(JSON.parse(outerRows[0]!)).toEqual({ status: 3, signal: null, spawnFault: false });
      expect(outerRows[1]).toBe('generic-failure');
      expect(result.stderr).not.toContain(canary);
      const entries = result.stderr.trim().split('\n').filter(Boolean);
      expect(entries).toHaveLength(scenario.diagnostic ? 1 : 0);
      if (scenario.diagnostic) {
        expect(entries[0]).toMatch(/^Frontend proof diagnostic: /);
        expect(JSON.parse(entries[0]!.slice('Frontend proof diagnostic: '.length))).toEqual({
          schema: 1,
          kind: 'framework-ownership-failure',
          ...clean,
        });
      }
    }
    // All seven nonforking inner children and outer probes were positively joined.
    safeToRemove = true;
  } finally {
    if (safeToRemove) rmSync(root, { recursive: true, force: true });
  }
}, 30_000);

it('contains actual ownership schema refusals and retained-evidence parsing', () => {
  const receipt = {
    group: 'startup',
    cases: [],
    disposition: 'retained-missing-family',
    launchOutcomes: [],
    sourceInputSha256: 'a'.repeat(64),
  };
  refuses(() => validateFrameworkOwnershipGroup({ ...receipt, [canary]: true }, 'startup'));
  refuses(() => validateFrameworkOwnershipGroup({ ...receipt, group: canary }, 'startup'));
  expect(() =>
    validateRetainedOwnershipEvidence('{"retained":true}', { retained: true }),
  ).not.toThrow();
  refuses(() => validateRetainedOwnershipEvidence(canary, {}));
  refuses(() => validateRetainedOwnershipEvidence(JSON.stringify({ [canary]: true }), {}));
});

it('contains an actual inert child nonzero stderr without exposing its output', () => {
  const run = () =>
    execFileSync(
      process.execPath,
      ['-e', `process.stderr.write(${JSON.stringify(canary)});process.exit(3)`],
      {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: 5_000,
      },
    );
  let raw: unknown;
  try {
    run();
  } catch (error) {
    raw = error;
  }
  expect(raw).toMatchObject({ status: 3, stderr: canary });
  refuses(() => closedFrameworkProofSync(run));
});

it('keeps cancellation and cleanup failures closed while executing the original finally', () => {
  let cleanup = 0;
  const timeout = Object.assign(new Error(canary), {
    code: 'ETIMEDOUT',
    signal: 'SIGTERM',
    stderr: canary,
  });
  refuses(() =>
    closedFrameworkProofSync(() => {
      try {
        throw timeout;
      } finally {
        cleanup++;
      }
    }),
  );
  expect(cleanup).toBe(1);
  const failCleanup = () => {
    cleanup++;
    throw new Error(canary);
  };
  refuses(() =>
    closedFrameworkProofSync(() => {
      try {
        throw timeout;
      } finally {
        failCleanup();
      }
    }),
  );
  expect(cleanup).toBe(2);
});

it('contains actual runner malformed stdout while retaining nonempty owned evidence', () => {
  let parent: string | undefined;
  vi.mocked(spawnSync).mockImplementation(((
    _exe: unknown,
    _args: unknown,
    options: { env: { TMPDIR: string } },
  ) => {
    parent = options.env.TMPDIR;
    writeFileSync(join(parent, 'known-test-evidence'), 'x');
    return { error: undefined, signal: null, status: 0, stdout: canary, stderr: '' };
  }) as typeof spawnSync);
  try {
    refuses(() => runFrameworkOwnershipGroup('startup'));
    expect(parent).toBeDefined();
    expect(existsSync(join(parent!, 'known-test-evidence'))).toBe(true);
  } finally {
    if (parent) {
      unlinkSync(join(parent, 'known-test-evidence'));
      rmdirSync(parent);
    }
  }
});

function assertClosedCallbacks(name: string, text: string, expected: number) {
  const file = ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  let count = 0;
  function visit(node: ts.Node) {
    if (ts.isCallExpression(node)) {
      const callee = node.expression.getText(file);
      const callback = node.arguments[1];
      if (
        (callee === 'test' || callee === 'it' || callee.startsWith('it.each(')) &&
        callback &&
        ts.isArrowFunction(callback)
      ) {
        count++;
        expect(ts.isCallExpression(callback.body)).toBe(true);
        if (!ts.isCallExpression(callback.body)) throw new Error('Closed proof callback refused');
        expect(callback.body.expression.getText(file)).toBe('closedFrameworkProofSync');
        expect(callback.body.arguments).toHaveLength(1);
        const action = callback.body.arguments[0]!;
        expect(ts.isArrowFunction(action)).toBe(true);
        if (!ts.isArrowFunction(action)) throw new Error('Closed proof action refused');
        expect(
          action.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.AsyncKeyword) ??
            false,
        ).toBe(false);
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(file);
  expect(count).toBe(expected);
}

it('keeps every closed Step 3 wrapper callback inside the synchronous boundary', () => {
  const directory = fileURLToPath(new URL('./', import.meta.url));
  for (const [name, expected] of [
    ['framework-response-transport.test.ts', 2],
    ['framework-response-overlap.test.ts', 2],
    ['framework-router-matrix.test.ts', 2],
    ['framework-router-faults.cases.ts', 2],
    ['framework-response-bodyless.test.ts', 1],
    ['framework-runtime-ownership.test.ts', 2],
    ['framework-runtime-workers.cases.ts', 1],
    ['framework-runtime-recovery.cases.ts', 1],
    ['framework-external-provider-ca.test.ts', 1],
    ['demo-dotnet-owner.test.ts', 1],
  ] as const) {
    assertClosedCallbacks(name, readFileSync(join(directory, name), 'utf8'), expected);
  }
  const source = readFileSync(join(directory, 'framework-runtime-ownership.test.ts'), 'utf8');
  const file = ts.createSourceFile(
    'ownership.ts',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  const suites = file.statements.flatMap((statement) =>
    ts.isExpressionStatement(statement) &&
    ts.isCallExpression(statement.expression) &&
    statement.expression.expression.getText(file) === 'describe.sequential'
      ? [statement.expression]
      : [],
  );
  expect(suites).toHaveLength(1);
  const callback = suites[0]?.arguments[1];
  if (!callback || !ts.isArrowFunction(callback) || !ts.isBlock(callback.body))
    throw new Error('Sequential ownership registration refused');
  const expected = [
    ['registerRuntimeWorkerCases', './framework-runtime-workers.cases'],
    ['registerRuntimeRecoveryCases', './framework-runtime-recovery.cases'],
    ['registerRouterFaultCases', './framework-router-faults.cases'],
  ];
  const calls = callback.body.statements.flatMap((statement) =>
    ts.isExpressionStatement(statement) &&
    ts.isCallExpression(statement.expression) &&
    statement.expression.expression.getText(file).startsWith('register')
      ? [statement.expression]
      : [],
  );
  expect(calls.map((call) => call.expression.getText(file))).toEqual(
    expected.map(([name]) => name),
  );
  expect(calls.every((call) => call.arguments.length === 0)).toBe(true);
  const imports = file.statements.flatMap((statement) => {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier))
      return [];
    const bindings = statement.importClause?.namedBindings;
    if (!bindings || !ts.isNamedImports(bindings)) return [];
    return bindings.elements
      .filter((binding) => binding.name.text.startsWith('register'))
      .map((binding) => {
        expect(binding.propertyName).toBeUndefined();
        return [binding.name.text, (statement.moduleSpecifier as ts.StringLiteral).text];
      });
  });
  expect(imports.sort()).toEqual([...expected].sort());
});

it('refuses omission of either router fault callback boundary', () => {
  const name = 'framework-router-faults.cases.ts';
  const text = readFileSync(join(fileURLToPath(new URL('./', import.meta.url)), name), 'utf8');
  assertClosedCallbacks(name, text, 2);
  const file = ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const ranges: Array<{ start: number; end: number; body: string }> = [];
  function visit(node: ts.Node) {
    if (ts.isCallExpression(node) && node.expression.getText(file) === 'test') {
      const callback = node.arguments[1];
      if (callback && ts.isArrowFunction(callback) && ts.isCallExpression(callback.body)) {
        const action = callback.body.arguments[0];
        if (action && ts.isArrowFunction(action))
          ranges.push({
            start: callback.body.getStart(file),
            end: callback.body.end,
            body: action.body.getText(file),
          });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(file);
  expect(ranges).toHaveLength(2);
  for (const range of ranges) {
    const dirty = text.slice(0, range.start) + range.body + text.slice(range.end);
    expect(dirty).not.toBe(text);
    expect(() => assertClosedCallbacks(name, dirty, 2)).toThrow();
  }
});
