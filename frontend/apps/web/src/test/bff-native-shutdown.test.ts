// @vitest-environment node
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';

import { describe, expect, it } from 'vitest';

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

async function fixture(source: string) {
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
    await dispose();
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
  it('pairs the real two-second fallback control with a joined exit inside the launcher budget', async () => {
    const baseline = await fixture(delayedShutdown);
    try {
      expect(await stopTestChild(baseline.child)).toEqual({ forced: true });
      expect(await baseline.completed).toEqual({ code: null, signal: 'SIGKILL' });
      expect(absentGroup(baseline.child)).toBe(true);
    } finally {
      await baseline.dispose();
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
      await state.dispose();
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

  it('refuses a zero-exit launcher whose owned descendant still requires forcing', async () => {
    const descendant = `
      process.on('SIGTERM', () => {});
      process.stdout.write('ready\\n');
      setInterval(() => {}, 1000);
    `;
    const state = await fixture(`
      const { spawn } = require('node:child_process');
      const child = spawn(process.execPath, ['-e', ${JSON.stringify(descendant)}],
        { stdio: ['ignore', 'pipe', 'ignore'] });
      child.stdout.once('data', () => {
        child.stdout.destroy(); child.unref();
        process.once('SIGTERM', () => process.exit(0));
        process.stdout.write('ready\\n');
      });
      setInterval(() => {}, 1000);
    `);
    let survivingGroup = false;
    void state.completed.then(() => {
      survivingGroup = !absentGroup(state.child);
    });
    try {
      await expect(state.cleanup.run()).rejects.toThrow('BFF browser cleanup failed');
      expect(await state.completed).toEqual({ code: 0, signal: null });
      expect(survivingGroup).toBe(true);
      expect(state.cleanup.failureStage).toBe('cleanup-native-forced');
      expect(await stopTestChild(state.child)).toEqual({ forced: true });
      expect(absentGroup(state.child)).toBe(true);
    } finally {
      await state.dispose();
    }
  }, 15_000);
});
