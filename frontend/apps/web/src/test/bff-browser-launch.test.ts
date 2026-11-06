// @vitest-environment node
import { spawn, type ChildProcess } from 'node:child_process';

import { describe, expect, it, vi } from 'vitest';

import { waitForBrowserDevTools } from '../../scripts/admission-browser.mjs';
import { bffDevToolsStalled, startBffBrowser } from '../../scripts/bff-proof-support.mjs';

type Launched = { readonly child: ChildProcess; readonly attempt: number };
type Observation = { kind: 'missing' | 'empty' | 'malformed' };

/** A readiness failure minted by the real helper, never a forged object. */
async function readinessFailure(
  options: Partial<{
    assertActive: () => void;
    isLive: () => boolean;
    readPort: () => Observation | never;
  }> = {},
): Promise<unknown> {
  vi.useFakeTimers();
  try {
    const waiting = waitForBrowserDevTools({
      assertActive() {},
      isLive: () => true,
      readPort: () => ({ kind: 'missing' as const }),
      timeout: 1,
      ...options,
    }).catch((error: unknown) => error);
    await vi.runAllTimersAsync();
    return await waiting;
  } finally {
    vi.useRealTimers();
  }
}

const fakeChild = (): ChildProcess => ({ signalCode: null, exitCode: null }) as ChildProcess;

describe('BFF browser stall classification', () => {
  it.each(['missing', 'empty', 'malformed'] as const)(
    'treats a live browser with a %s port file at the deadline as a stall',
    async (kind) => {
      expect(bffDevToolsStalled(await readinessFailure({ readPort: () => ({ kind }) }))).toBe(true);
    },
  );

  it('never treats browser death, owner closure, unreadable state or unbranded errors as a stall', async () => {
    expect(
      bffDevToolsStalled(
        await readinessFailure({
          assertActive() {
            throw new Error('PRIVATE-CANARY');
          },
        }),
      ),
    ).toBe(false);
    expect(bffDevToolsStalled(await readinessFailure({ isLive: () => false }))).toBe(false);
    expect(
      bffDevToolsStalled(
        await readinessFailure({
          readPort() {
            throw new Error('PRIVATE-CANARY');
          },
        }),
      ),
    ).toBe(false);
    expect(bffDevToolsStalled(new Error('PRIVATE-CANARY'))).toBe(false);
    expect(
      bffDevToolsStalled(
        Object.assign(new Error('forged'), { admissionStage: 'browser-devtools' }),
      ),
    ).toBe(false);
    expect(bffDevToolsStalled(null)).toBe(false);
  });
});

describe('BFF browser launch replacement', () => {
  it('replaces a stalled browser exactly once, after stopping it without force', async () => {
    const stalled = await readinessFailure();
    const launched: Launched[] = [];
    const stopped: ChildProcess[] = [];
    const result = await startBffBrowser({
      launch: (attempt: number) => {
        const entry = { child: fakeChild(), attempt };
        launched.push(entry);
        return entry;
      },
      wait: async ({ attempt }: Launched) => {
        if (attempt === 1) throw stalled;
        return 40123;
      },
      stop: async (child: ChildProcess) => {
        stopped.push(child);
        return { forced: false };
      },
    });
    expect(result.port).toBe(40123);
    expect(result.attempt).toBe(2);
    expect(launched.map((entry) => entry.attempt)).toEqual([1, 2]);
    expect(stopped).toEqual([launched[0]!.child]);
  });

  it('does not launch again when the first browser becomes ready', async () => {
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    const stop = vi.fn();
    const result = await startBffBrowser({ launch, wait: async () => 41000, stop });
    expect(result.port).toBe(41000);
    expect(launch).toHaveBeenCalledTimes(1);
    expect(stop).not.toHaveBeenCalled();
  });

  it('keeps the second closed failure when the replacement also stalls', async () => {
    const first = await readinessFailure({ readPort: () => ({ kind: 'missing' }) });
    const second = await readinessFailure({ readPort: () => ({ kind: 'empty' }) });
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    const stop = vi.fn(async () => ({ forced: false }));
    await expect(
      startBffBrowser({
        launch,
        wait: async ({ attempt }: Launched) => {
          throw attempt === 1 ? first : second;
        },
        stop,
      }),
    ).rejects.toBe(second);
    expect(launch).toHaveBeenCalledTimes(2);
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('never retries browser death, owner closure or unreadable state', async () => {
    const failures = [
      await readinessFailure({ isLive: () => false }),
      await readinessFailure({
        assertActive() {
          throw new Error('PRIVATE-CANARY');
        },
      }),
      await readinessFailure({
        readPort() {
          throw new Error('PRIVATE-CANARY');
        },
      }),
      new Error('PRIVATE-CANARY'),
    ];
    for (const failure of failures) {
      const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
      const stop = vi.fn();
      await expect(
        startBffBrowser({
          launch,
          wait: async () => {
            throw failure;
          },
          stop,
        }),
      ).rejects.toBe(failure);
      expect(launch).toHaveBeenCalledTimes(1);
      expect(stop).not.toHaveBeenCalled();
    }
  });

  it.each([
    ['forced', async () => ({ forced: true })],
    [
      'unconfirmed',
      async () => {
        throw new Error('Owned fixture process group did not stop');
      },
    ],
  ])(
    'surfaces the original stall and does not launch when the stalled browser stop is %s',
    async (_name, stop) => {
      const stalled = await readinessFailure();
      const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
      await expect(
        startBffBrowser({
          launch,
          wait: async () => {
            throw stalled;
          },
          stop,
        }),
      ).rejects.toBe(stalled);
      expect(launch).toHaveBeenCalledTimes(1);
    },
  );

  it('keeps the launch bound closed', async () => {
    await expect(
      startBffBrowser({
        launch: (attempt: number) => ({ child: fakeChild(), attempt }),
        wait: async () => 1,
        attempts: 3 as unknown as 2,
      }),
    ).rejects.toThrow('closed launch bound');
  });

  it('stops an actual stalled owned process group without force before replacing it', async () => {
    expect(process.platform).not.toBe('win32');
    const stalled = await readinessFailure();
    const children: ChildProcess[] = [];
    const result = await startBffBrowser({
      launch: (attempt: number) => {
        const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
          detached: true,
          stdio: 'ignore',
        });
        children.push(child);
        return { child, attempt };
      },
      wait: async ({ attempt }: Launched) => {
        if (attempt === 1) throw stalled;
        return 42000;
      },
    });
    try {
      expect(result.port).toBe(42000);
      expect(children).toHaveLength(2);
      const [first, second] = children as [ChildProcess, ChildProcess];
      expect(first.signalCode ?? first.exitCode).not.toBeNull();
      expect(second.exitCode).toBeNull();
      expect(second.signalCode).toBeNull();
      expect(() => process.kill(-first.pid!, 0)).toThrow(/ESRCH/);
    } finally {
      for (const child of children) {
        try {
          if (child.exitCode === null && child.signalCode === null)
            process.kill(-child.pid!, 'SIGKILL');
        } catch {
          /* already gone */
        }
      }
    }
  });
});
