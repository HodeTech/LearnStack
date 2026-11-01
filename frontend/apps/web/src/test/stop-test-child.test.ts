// @vitest-environment node
import type { ChildProcess } from 'node:child_process';

import { afterEach, expect, it, vi } from 'vitest';

import { assertUnforcedTestChildStop, stopTestChild } from '../../scripts/stop-test-child.mjs';

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
});

function absent() {
  return Object.assign(new Error('absent'), { code: 'ESRCH' });
}

it('never signals a cached group after leader exit, and only retires after observed absence', async () => {
  vi.useFakeTimers();
  const child = { pid: 234567, exitCode: 0, signalCode: null } as unknown as ChildProcess;
  const signals: (number | string | undefined)[] = [];
  let probes = 0;
  vi.spyOn(process, 'kill').mockImplementation((_pid, signal) => {
    signals.push(signal);
    if (signal === 0 && ++probes >= 3) throw absent();
    return true;
  });
  const cleanup = stopTestChild(child);
  await vi.advanceTimersByTimeAsync(60);
  await expect(cleanup).resolves.toEqual({ forced: false });
  expect(signals).toEqual([0, 0, 0]);
  const before = signals.length;
  expect(stopTestChild(child)).toBe(cleanup);
  await stopTestChild(child);
  expect(signals).toHaveLength(before);
});

it('does not escalate a saved group after the leader exits during graceful stop', async () => {
  vi.useFakeTimers();
  const child = { pid: 234568, exitCode: null, signalCode: null } as unknown as ChildProcess;
  const signals: (number | string | undefined)[] = [];
  vi.spyOn(process, 'kill').mockImplementation((_pid, signal) => {
    signals.push(signal);
    if (signal === 'SIGTERM') Object.assign(child, { exitCode: 0 });
    return true;
  });
  const cleanup = stopTestChild(child).catch((error) => error);
  await vi.advanceTimersByTimeAsync(5000);
  expect(await cleanup).toMatchObject({ message: 'Owned fixture process group did not stop' });
  expect(signals.filter((signal) => signal !== 0)).toEqual(['SIGTERM']);
});

it('keeps a naturally draining leaderless group unforced after the fallback window', async () => {
  vi.useFakeTimers();
  const child = { pid: 234570, exitCode: null, signalCode: null } as unknown as ChildProcess;
  const signals: (number | string | undefined)[] = [];
  const began = Date.now();
  vi.spyOn(process, 'kill').mockImplementation((_pid, signal) => {
    signals.push(signal);
    if (signal === 'SIGTERM') Object.assign(child, { exitCode: 0 });
    if (signal === 0 && Date.now() - began >= 3000) throw absent();
    return true;
  });
  const cleanup = stopTestChild(child);
  await vi.advanceTimersByTimeAsync(3040);
  await expect(cleanup).resolves.toEqual({ forced: false });
  expect(signals.filter((signal) => signal !== 0)).toEqual(['SIGTERM']);
});

it('retains the existing forced stop observation while refusing it as private-root cleanup', async () => {
  vi.useFakeTimers();
  const child = { pid: 234569, exitCode: null, signalCode: null } as unknown as ChildProcess;
  let killed = false;
  const signals: (number | string | undefined)[] = [];
  vi.spyOn(process, 'kill').mockImplementation((_pid, signal) => {
    signals.push(signal);
    if (signal === 0 && killed) throw absent();
    if (signal === 'SIGKILL') {
      killed = true;
      Object.assign(child, { signalCode: 'SIGKILL' });
    }
    return true;
  });
  const cleanup = stopTestChild(child);
  await vi.advanceTimersByTimeAsync(2040);
  const result = await cleanup;
  expect(result).toEqual({ forced: true });
  expect(signals.filter((signal) => signal !== 0)).toEqual(['SIGTERM', 'SIGKILL']);
  expect(() => assertUnforcedTestChildStop(child, result)).toThrow('forced or unconfirmed');
});

it.each(['SIGKILL', 'SIGABRT', 'SIGSEGV'])(
  'refuses already observed fatal %s despite an unforced absent outer group',
  (signal) => {
    expect(() =>
      assertUnforcedTestChildStop({ signalCode: signal } as ChildProcess, { forced: false }),
    ).toThrow('forced or unconfirmed');
  },
);

it('keeps a normal unforced stop admissible without claiming separate family retirement', () => {
  for (const signalCode of [null, 'SIGINT', 'SIGTERM'])
    expect(() =>
      assertUnforcedTestChildStop({ signalCode } as ChildProcess, { forced: false }),
    ).not.toThrow();
});
