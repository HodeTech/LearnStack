// @vitest-environment node
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';

import { describe, expect, it, vi } from 'vitest';

import { stopTestChild } from '../../scripts/stop-test-child.mjs';

function absent(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return false;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ESRCH') return true;
    throw error;
  }
}

async function exit(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null || child.signalCode !== null) return;
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Leader deadline')), 5000);
    child.once('error', reject);
    child.once('exit', () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

describe('owned production fixture cleanup', () => {
  it.each([false, true])(
    'stops an uncooperative descendant with exited leader = %s',
    async (exitedLeader) => {
      // Linux/macOS are the native fixture targets; never signal the test runner's group.
      expect(process.platform).not.toBe('win32');
      const descendant =
        'process.on("SIGTERM", () => {}); process.stdout.write("ready\\n"); setInterval(() => {}, 1000);';
      const source = `
        const { spawn } = require('node:child_process');
        const child = spawn(process.execPath, ['-e', ${JSON.stringify(descendant)}],
          { stdio: ['ignore', 'pipe', 'ignore'] });
        child.stdout.once('data', () => {
          process.stdout.write(String(child.pid) + '\\n');
          if (${exitedLeader}) process.exit(0);
        });
        setInterval(() => {}, 1000);
      `;
      const leader = spawn(process.execPath, ['-e', source], {
        detached: true,
        stdio: ['ignore', 'pipe', 'ignore'],
      });
      try {
        const pid = await new Promise<number>((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error('Descendant deadline')), 5000);
          leader.once('error', reject);
          leader.stdout!.once('data', (data: Buffer) => {
            clearTimeout(timer);
            resolve(Number(data.toString().trim()));
          });
        });
        expect(Number.isSafeInteger(pid) && pid > 0).toBe(true);
        expect(absent(pid)).toBe(false);
        if (exitedLeader) {
          await exit(leader);
          expect(leader.exitCode).toBe(0);
          expect(absent(pid)).toBe(false); // Positive surviving-descendant control.
        }
        await stopTestChild(leader);
        await exit(leader);
        expect(absent(pid)).toBe(true);
        expect(absent(-leader.pid!)).toBe(true);
      } finally {
        if (leader.pid !== undefined && !absent(-leader.pid)) process.kill(-leader.pid, 'SIGKILL');
        await exit(leader);
      }
    },
    15_000,
  );
  it('accepts a failed spawn with no owned process', async () => {
    await expect(stopTestChild({ pid: undefined } as ChildProcess)).resolves.toBeUndefined();
  });
  it.each([false, true])(
    'requires absence rather than treating permission refusal as cleanup, persistent = %s',
    async (persistent) => {
      vi.useFakeTimers();
      let probes = 0;
      const kill = vi.spyOn(process, 'kill').mockImplementation((_pid, signal) => {
        if (signal !== 0) throw Object.assign(new Error('Owned group signal'), { code: 'EPERM' });
        probes++;
        throw Object.assign(new Error('Owned group probe'), {
          code: persistent || probes === 1 ? 'EPERM' : 'ESRCH',
        });
      });
      try {
        const child = { pid: 123456, exitCode: 0, signalCode: null } as ChildProcess;
        const stopped = stopTestChild(child);
        const checked = persistent
          ? expect(stopped).rejects.toThrow('Owned fixture process group did not stop')
          : expect(stopped).resolves.toBeUndefined();
        await vi.advanceTimersByTimeAsync(persistent ? 5020 : 20);
        await checked;
        expect(probes).toBeGreaterThan(1);
        expect(kill).toHaveBeenCalledWith(-child.pid!, 'SIGTERM');
        if (persistent) expect(kill).toHaveBeenCalledWith(-child.pid!, 'SIGKILL');
      } finally {
        vi.restoreAllMocks();
        vi.useRealTimers();
      }
    },
  );
});
