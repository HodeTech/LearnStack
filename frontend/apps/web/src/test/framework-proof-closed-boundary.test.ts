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
  const absent = { ...pending, stage: 'build', currentCase: null };
  expect(ownershipDiagnostic(JSON.stringify(absent), 'workers')).toMatchObject(absent);
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
    ...['readinessReasons', 'ownerSignals'].map(
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
    ['framework-router-faults.test.ts', 2],
    ['framework-response-bodyless.test.ts', 1],
    ['framework-runtime-ownership.test.ts', 2],
    ['framework-runtime-workers.test.ts', 1],
    ['framework-runtime-recovery.test.ts', 1],
    ['framework-external-provider-ca.test.ts', 1],
    ['demo-dotnet-owner.test.ts', 1],
  ] as const) {
    assertClosedCallbacks(name, readFileSync(join(directory, name), 'utf8'), expected);
  }
});

it('refuses omission of either router fault callback boundary', () => {
  const name = 'framework-router-faults.test.ts';
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
