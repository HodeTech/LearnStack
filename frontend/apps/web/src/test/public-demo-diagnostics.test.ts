// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  PublicDemoFailure,
  publicDemoFailure,
  publicDemoOperation,
} from '../../scripts/public-demo-diagnostics.mjs';

const canary = 'private-provider-token-host-url-canary';
const context = { mode: 'audit', laneIndex: 0, kind: 'catalog', run: 2, workerStage: 'audit' };

describe('closed public demo operation diagnostics', () => {
  it('returns a successful operation result only after owned cleanup without recording failure', async () => {
    const events: string[] = [];
    const result = await publicDemoOperation({
      input: context,
      child: () => undefined,
      record: () => {
        events.push('failure');
      },
      run: async (phase) => {
        phase('result');
        events.push('result');
        return { complete: true };
      },
      cleanup: async () => {
        events.push('cleanup');
      },
    });
    expect(result).toEqual({ complete: true });
    expect(events).toEqual(['result', 'cleanup']);
  });

  it.each([
    ['browser-spawn', 'unknown'],
    ['startup-port', 'chromium-exited'],
    ['startup-port', 'port-invalid'],
    ['startup-port', 'deadline'],
    ['worker', 'worker-failure'],
    ['result', 'unknown'],
  ])('records a failure at %s with closed reason %s', async (stage, reason) => {
    let failure: unknown;
    let cleanups = 0;
    const error =
      reason === 'unknown' || reason === 'worker-failure'
        ? Object.assign(new Error(canary), { name: canary, code: canary })
        : Object.assign(new PublicDemoFailure(reason), { message: canary });
    const operation = publicDemoOperation({
      input: { ...context, url: canary, token: canary },
      child: () => ({ exitCode: 1, signalCode: 'SIGTRAP', stderr: canary }),
      record: (row: unknown) => {
        failure ??= row;
      },
      run: async (phase: (value: string) => void) => {
        phase(stage);
        throw error;
      },
      cleanup: async () => {
        cleanups++;
      },
    });
    await expect(operation).rejects.toBe(error);
    expect(failure).toEqual({
      stage,
      reason,
      mode: 'audit',
      lane: 1,
      kind: 'catalog',
      run: 2,
      workerStage: stage === 'worker' ? 'audit' : null,
      exitCode: 1,
      signal: 'SIGTRAP',
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
    expect(cleanups).toBe(1);
  });

  it('records cleanup failure without converting a successful operation into a pass', async () => {
    let failure: unknown;
    const error = new Error(canary);
    await expect(
      publicDemoOperation({
        input: context,
        child: () => ({ exitCode: 0, signalCode: null }),
        record: (row: unknown) => {
          failure ??= row;
        },
        run: async (phase: (value: string) => void) => {
          phase('result');
          return 'complete';
        },
        cleanup: async () => {
          throw error;
        },
      }),
    ).rejects.toBe(error);
    expect(failure).toEqual({
      stage: 'cleanup',
      reason: 'unknown',
      mode: 'audit',
      lane: 1,
      kind: 'catalog',
      run: 2,
      workerStage: null,
      exitCode: 0,
      signal: null,
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
  });

  it('retains the first worker failure and phase when cleanup also fails', async () => {
    let failure: unknown;
    const original = new Error(canary);
    await expect(
      publicDemoOperation({
        input: context,
        child: () => ({ exitCode: null, signalCode: null }),
        record: (row: unknown) => {
          failure ??= row;
        },
        run: async (phase: (value: string) => void) => {
          phase('worker');
          throw original;
        },
        cleanup: async () => {
          throw new PublicDemoFailure('deadline');
        },
      }),
    ).rejects.toBe(original);
    expect(failure).toMatchObject({ stage: 'worker', reason: 'worker-failure' });
    expect(JSON.stringify(failure)).not.toContain(canary);
  });

  it('does not export arbitrary error, context, stage or process fields', () => {
    const failure = publicDemoFailure(
      canary,
      Object.assign(new Error(canary), {
        reason: canary,
        name: canary,
        code: canary,
        stack: canary,
      }),
      {
        mode: canary,
        laneIndex: canary,
        kind: canary,
        run: canary,
        url: canary,
        workerStage: canary,
      },
      { exitCode: canary, signalCode: canary, stderr: canary },
    );
    expect(failure).toEqual({
      stage: 'proof',
      reason: 'unknown',
      mode: null,
      lane: null,
      kind: null,
      run: null,
      workerStage: null,
      exitCode: null,
      signal: null,
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
    const poisoned = Object.assign(new PublicDemoFailure('deadline'), { reason: canary });
    const worker = publicDemoFailure('worker', poisoned, { ...context, workerStage: canary });
    expect(worker).toMatchObject({ reason: 'worker-failure', workerStage: null });
    expect(JSON.stringify(worker)).not.toContain(canary);
  });

  it('the actual worker publishes only its closed failed phase before a browser connection', () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-diagnostics-'));
    try {
      const input = join(root, 'input.json');
      const result = join(root, 'result.json');
      writeFileSync(
        input,
        JSON.stringify({
          ...context,
          port: 0,
          result,
          url: `https://${canary}.invalid`,
          provider: canary,
        }),
      );
      const execution = spawnSync(
        process.execPath,
        [fileURLToPath(new URL('../../scripts/public-demo-worker.mjs', import.meta.url)), input],
        { encoding: 'utf8', timeout: 15000 },
      );
      expect(execution.status).toBe(1);
      expect(JSON.parse(readFileSync(result, 'utf8'))).toEqual({ failure: { stage: 'connect' } });
      expect(execution.stderr).toContain('Public demo worker refused: connect');
      expect(execution.stdout + execution.stderr + readFileSync(result, 'utf8')).not.toContain(
        canary,
      );
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
