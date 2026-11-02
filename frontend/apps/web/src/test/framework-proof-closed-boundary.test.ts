// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import type * as ChildProcess from 'node:child_process';
import { existsSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import ts from 'typescript';
import { afterEach, expect, it, vi } from 'vitest';

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
