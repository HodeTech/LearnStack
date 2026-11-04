// @vitest-environment node
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import { once } from 'node:events';

import { describe, expect, it } from 'vitest';

import { createHeldChildControl, withTestCleanup } from './held-child-control';
import { createBffBrowserCleanup } from '../../scripts/bff-proof-support.mjs';
import { createFixtureOwner } from '../../scripts/fixture-support.mjs';
import { stopTestChild } from '../../scripts/stop-test-child.mjs';

function absentGroup(child: ChildProcess): boolean {
  if (child.pid === undefined) return true;
  try {
    process.kill(-child.pid, 0);
    return false;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ESRCH') return true;
    throw error;
  }
}

async function fixture(source: string, held?: Awaited<ReturnType<typeof createHeldChildControl>>) {
  expect(process.platform).not.toBe('win32');
  const owner = createFixtureOwner();
  const child = owner.ownChild(
    spawn(process.execPath, ['-e', source], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    }),
  );
  child.once('error', () => {});
  const completed = new Promise<{ code: number | null; signal: NodeJS.Signals | null }>((resolve) =>
    child.once('close', (code, signal) => resolve({ code, signal })),
  );
  const dispose = async () => {
    // The owner retains its coalesced group handle; never re-signal a numeric
    // identity after its absence has already retired that handle.
    await owner.dispose();
    await completed;
  };
  try {
    await new Promise<void>((resolve, reject) => {
      let settled = false;
      let output = '';
      const finish = (error?: Error) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        child.removeListener('error', failed);
        child.removeListener('close', closed);
        child.stdout!.removeListener('data', ready);
        if (error) reject(error);
        else resolve();
      };
      const failed = () => finish(new Error('Owned readiness spawn failed'));
      const closed = () => finish(new Error('Owned readiness exited'));
      const ready = (data: Buffer) => {
        output += data.toString();
        if (output.length > 6 || !'ready\n'.startsWith(output))
          finish(new Error('Owned readiness shape'));
        else if (output === 'ready\n') finish();
      };
      const timer = setTimeout(() => finish(new Error('Owned readiness deadline')), 5000);
      child.once('error', failed);
      child.once('close', closed);
      child.stdout!.on('data', ready);
    });
  } catch (error) {
    const attempts = await Promise.allSettled([held?.dispose(), dispose()]);
    const failures = attempts
      .filter((result) => result.status === 'rejected')
      .map((result) => result.reason);
    if (failures.length)
      throw new AggregateError([error, ...failures], 'Owned readiness and cleanup failed');
    throw error;
  }
  const cleanup = createBffBrowserCleanup(() => ({
    nativeServer: { process: child, completed },
    owner,
  }));
  return { child, completed, cleanup, dispose };
}

const delayedShutdown = `
  process.once('SIGTERM', () => setTimeout(() => process.exit(0), 2500));
  process.stdout.write('ready\\n');
  setInterval(() => {}, 1000);
`;

describe('BFF native launcher shutdown ownership', () => {
  it('keeps the original assertion as first cause while attempting every independent test cleanup', async () => {
    const primary = new Error('original test assertion');
    const first = new Error('first cleanup');
    const last = new Error('last cleanup');
    const attempted: number[] = [];
    const error = await withTestCleanup(async () => {
      throw primary;
    }, [
      () => {
        attempted.push(1);
        throw first;
      },
      () => {
        attempted.push(2);
      },
      async () => {
        attempted.push(3);
        throw last;
      },
    ]).catch((error) => error);
    expect(attempted).toEqual([1, 2, 3]);
    expect(error).toBeInstanceOf(AggregateError);
    expect(error.errors).toEqual([primary, first, last]);
  });
  it('pairs the close-before-group rejection with exit/group/close completion for actual inherited stdio', async () => {
    const inheritedStdio = (descendant: string) => `
      const { spawn } = require('node:child_process');
      const descendant = spawn(process.execPath, ['-e', ${JSON.stringify(descendant)}],
        { stdio: ['ignore', 1, 2, 'ipc'] });
      descendant.once('message', () => {
        descendant.unref();
        process.once('SIGTERM', () => process.exit(0));
        process.stdout.write('ready\\n');
      });
      setInterval(() => {}, 1000);
    `;
    const oldControl = await createHeldChildControl();
    const oldOrder = await fixture(inheritedStdio(oldControl.source()), oldControl);
    await withTestCleanup(async () => {
      await oldControl.ready();
      oldOrder.child.kill('SIGTERM');
      let timer!: ReturnType<typeof setTimeout>;
      const observedClose = await Promise.race([
        oldOrder.completed.then(() => true),
        new Promise<boolean>((resolve) => {
          timer = setTimeout(() => resolve(false), 5000);
        }),
      ]);
      clearTimeout(timer);
      expect(observedClose).toBe(false);
      expect(oldOrder.child.exitCode).toBe(0);
      expect(oldOrder.child.signalCode).toBeNull();
      expect(absentGroup(oldOrder.child)).toBe(false);
      // The held descendant cooperates through its still-live authenticated
      // channel. A retired leader's cached PGID never authorizes its release.
      await oldControl.release();
      expect(await stopTestChild(oldOrder.child)).toEqual({ forced: false });
      expect(await oldOrder.completed).toEqual({ code: 0, signal: null });
    }, [() => oldControl.dispose(), () => oldOrder.dispose()]);
    const reorderedControl = await createHeldChildControl();
    const reordered = await fixture(inheritedStdio(reorderedControl.source()), reorderedControl);
    await withTestCleanup(async () => {
      await reorderedControl.ready();
      const exited = once(reordered.child, 'exit');
      const cleanup = reordered.cleanup.run();
      await exited;
      expect(absentGroup(reordered.child)).toBe(false);
      await reorderedControl.release();
      await expect(cleanup).resolves.toBeUndefined();
      expect(await reordered.completed).toEqual({ code: 0, signal: null });
      expect(await stopTestChild(reordered.child)).toEqual({ forced: false });
      expect(absentGroup(reordered.child)).toBe(true);
      expect(reordered.cleanup.failureStage).toBeNull();
    }, [() => reorderedControl.dispose(), () => reordered.dispose()]);
  }, 15_000);
  it('pairs the real two-second fallback control with a joined exit inside the launcher budget', async () => {
    const baseline = await fixture(delayedShutdown);
    try {
      expect(await stopTestChild(baseline.child)).toEqual({ forced: true });
      expect(await baseline.completed).toEqual({ code: null, signal: 'SIGKILL' });
      expect(absentGroup(baseline.child)).toBe(true);
    } finally {
      await expect(baseline.dispose()).rejects.toThrow('Fixture cleanup failed');
    }
    const joined = await fixture(delayedShutdown);
    try {
      await expect(joined.cleanup.run()).resolves.toBeUndefined();
      expect(await joined.completed).toEqual({ code: 0, signal: null });
      expect(await stopTestChild(joined.child)).toEqual({ forced: false });
      expect(absentGroup(joined.child)).toBe(true);
      expect(joined.cleanup.failureStage).toBeNull();
    } finally {
      await joined.dispose();
    }
  }, 15_000);

  it('rejects an uncooperative launcher even when forced fallback removes its real group', async () => {
    const state = await fixture(`
      process.on('SIGTERM', () => {});
      process.stdout.write('ready\\n');
      setInterval(() => {}, 1000);
    `);
    try {
      await expect(state.cleanup.run()).rejects.toThrow('BFF browser cleanup failed');
      expect(state.cleanup.failureStage).toBe('cleanup-native-wait');
      expect(await state.completed).toEqual({ code: null, signal: 'SIGKILL' });
      expect(await stopTestChild(state.child)).toEqual({ forced: true });
      expect(absentGroup(state.child)).toBe(true);
    } finally {
      await expect(state.dispose()).rejects.toThrow('Fixture cleanup failed');
    }
  }, 15_000);

  it.each(['nonzero', 'signal'] as const)(
    'rejects actual %s completion despite unforced group absence',
    async (kind) => {
      const state = await fixture(`
      process.once('SIGTERM', () => {
        ${kind === 'nonzero' ? 'process.exit(17);' : 'process.kill(process.pid, "SIGINT");'}
      });
      process.stdout.write('ready\\n');
      setInterval(() => {}, 1000);
    `);
      try {
        await expect(state.cleanup.run()).rejects.toThrow('BFF browser cleanup failed');
        expect(state.cleanup.failureStage).toBe(
          kind === 'nonzero' ? 'cleanup-native-exit' : 'cleanup-native-signal',
        );
        expect(await state.completed).toEqual(
          kind === 'nonzero' ? { code: 17, signal: null } : { code: null, signal: 'SIGINT' },
        );
        expect(await stopTestChild(state.child)).toEqual({ forced: false });
        expect(absentGroup(state.child)).toBe(true);
      } finally {
        await state.dispose();
      }
    },
  );

  it('refuses a zero-exit launcher with a held descendant without signaling a stale group', async () => {
    const control = await createHeldChildControl();
    const descendant = control.source({ ignoreSigterm: true, stdoutReady: true });
    const state = await fixture(
      `
      const { spawn } = require('node:child_process');
      const child = spawn(process.execPath, ['-e', ${JSON.stringify(descendant)}],
        { stdio: ['ignore', 'pipe', 'ignore'] });
      child.stdout.once('data', () => {
        child.stdout.destroy(); child.unref();
        process.once('SIGTERM', () => process.exit(0));
        process.stdout.write('ready\\n');
      });
      setInterval(() => {}, 1000);
    `,
      control,
    );
    let survivingGroup = false;
    void state.completed.then(() => {
      survivingGroup = !absentGroup(state.child);
    });
    await withTestCleanup(async () => {
      await control.ready();
      await expect(state.cleanup.run()).rejects.toThrow('BFF browser cleanup failed');
      expect(await state.completed).toEqual({ code: 0, signal: null });
      expect(survivingGroup).toBe(true);
      expect(state.cleanup.failureStage).toBe('cleanup-native-stop');
      await expect(stopTestChild(state.child)).rejects.toThrow(
        'Owned fixture process group did not stop',
      );
      expect(absentGroup(state.child)).toBe(false);
    }, [
      () => control.dispose(),
      async () => {
        if (state.child.exitCode === null && state.child.signalCode === null)
          state.child.kill('SIGTERM');
        await state.completed;
      },
      () => expect(state.dispose()).rejects.toThrow('Fixture cleanup failed'),
    ]);
    expect(absentGroup(state.child)).toBe(true);
  }, 15_000);
});
