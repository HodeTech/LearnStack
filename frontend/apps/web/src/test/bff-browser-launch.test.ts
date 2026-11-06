// @vitest-environment node
import { spawn, type ChildProcess } from 'node:child_process';

import { describe, expect, it, vi } from 'vitest';

import { waitForBrowserDevTools } from '../../scripts/admission-browser.mjs';
import { bffDevToolsStalled, startBffBrowser } from '../../scripts/bff-proof-support.mjs';
import { createFixtureOwner } from '../../scripts/fixture-support.mjs';

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
  it('replaces a stalled browser once, after retiring it', async () => {
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
      retire: async (child: ChildProcess) => {
        stopped.push(child);
      },
    });
    expect(result.port).toBe(40123);
    expect(result.attempt).toBe(2);
    expect(launched.map((entry) => entry.attempt)).toEqual([1, 2]);
    expect(stopped).toEqual([launched[0]!.child]);
  });

  it('does not launch again when the first browser becomes ready', async () => {
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    const retire = vi.fn();
    const result = await startBffBrowser({ launch, wait: async () => 41000, retire });
    expect(result.port).toBe(41000);
    expect(launch).toHaveBeenCalledTimes(1);
    expect(retire).not.toHaveBeenCalled();
  });

  it('takes a third launch when the second also stalls, retiring each stalled browser once', async () => {
    const stalls = [
      await readinessFailure({ readPort: () => ({ kind: 'missing' }) }),
      await readinessFailure({ readPort: () => ({ kind: 'empty' }) }),
    ];
    const launched: Launched[] = [];
    const retired: ChildProcess[] = [];
    const result = await startBffBrowser({
      launch: (attempt: number) => {
        const entry = { child: fakeChild(), attempt };
        launched.push(entry);
        return entry;
      },
      wait: async ({ attempt }: Launched) => {
        if (attempt <= 2) throw stalls[attempt - 1];
        return 44001;
      },
      retire: async (child: ChildProcess) => {
        retired.push(child);
      },
    });
    expect(result.port).toBe(44001);
    expect(result.attempt).toBe(3);
    expect(retired).toEqual([launched[0]!.child, launched[1]!.child]);
  });

  it('keeps the last closed failure after three stalled launches and never starts a fourth', async () => {
    const stalls = [
      await readinessFailure({ readPort: () => ({ kind: 'missing' }) }),
      await readinessFailure({ readPort: () => ({ kind: 'empty' }) }),
      await readinessFailure({ readPort: () => ({ kind: 'malformed' }) }),
    ];
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    const retire = vi.fn(async () => undefined);
    await expect(
      startBffBrowser({
        launch,
        wait: async ({ attempt }: Launched) => {
          throw stalls[attempt - 1];
        },
        retire,
      }),
    ).rejects.toBe(stalls[2]);
    expect(launch).toHaveBeenCalledTimes(3);
    expect(retire).toHaveBeenCalledTimes(2);
  });

  it.each([1, 2] as const)('honours a caller bound of %i launch(es)', async (attempts) => {
    const stalled = await readinessFailure();
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    const retire = vi.fn(async () => undefined);
    await expect(
      startBffBrowser({
        launch,
        wait: async () => {
          throw stalled;
        },
        retire,
        attempts,
      }),
    ).rejects.toBe(stalled);
    expect(launch).toHaveBeenCalledTimes(attempts);
    expect(retire).toHaveBeenCalledTimes(attempts - 1);
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
      const retire = vi.fn();
      await expect(
        startBffBrowser({
          launch,
          wait: async () => {
            throw failure;
          },
          retire,
        }),
      ).rejects.toBe(failure);
      expect(launch).toHaveBeenCalledTimes(1);
      expect(retire).not.toHaveBeenCalled();
    }
  });

  it('surfaces the original stall and does not launch when the stalled browser cannot be retired', async () => {
    const stalled = await readinessFailure();
    const launch = vi.fn((attempt: number) => ({ child: fakeChild(), attempt }));
    await expect(
      startBffBrowser({
        launch,
        wait: async () => {
          throw stalled;
        },
        retire: async () => {
          throw new Error('Owned fixture process group did not stop');
        },
      }),
    ).rejects.toBe(stalled);
    expect(launch).toHaveBeenCalledTimes(1);
  });

  it('keeps the launch bound closed', async () => {
    await expect(
      startBffBrowser({
        launch: (attempt: number) => ({ child: fakeChild(), attempt }),
        wait: async () => 1,
        attempts: 4 as unknown as 3,
      }),
    ).rejects.toThrow('closed launch bound');
  });

  it('stops an actual stalled owned process group before replacing it', async () => {
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

describe('BFF browser launch replacement of a start-up that ignores SIGTERM', () => {
  // A browser still inside its start-up never runs the loop that handles SIGTERM, so
  // the owner's grace expires and the group is killed. That is the stall hosted run
  // 42238072858 produced: the replacement used to be refused and cleanup then failed.
  const ignoresTerm =
    "process.on('SIGTERM', () => {}); process.stdout.write('ready\\n'); setInterval(() => {}, 1000)";
  // Without this the signal can arrive before the child has installed its handler.
  const ready = (child: ChildProcess) =>
    new Promise<void>((resolve) => child.stdout!.once('data', () => resolve()));

  it('retires it through the owner, starts the replacement and finishes with a clean disposal', async () => {
    expect(process.platform).not.toBe('win32');
    const stalled = await readinessFailure();
    const owner = createFixtureOwner();
    const children: ChildProcess[] = [];
    let result: Awaited<ReturnType<typeof startBffBrowser<Launched>>> | undefined;
    try {
      result = await startBffBrowser<Launched>({
        launch: (attempt: number) => {
          const child = owner.ownChild(
            spawn(
              process.execPath,
              ['-e', attempt === 1 ? ignoresTerm : 'setInterval(() => {}, 1000)'],
              {
                detached: true,
                stdio: ['ignore', 'pipe', 'pipe'],
              },
            ),
          );
          children.push(child);
          return { child, attempt };
        },
        wait: async ({ attempt, child }: Launched) => {
          if (attempt === 1) {
            await ready(child);
            throw stalled;
          }
          return 43000;
        },
        retire: (child: ChildProcess) => owner.retireChild(child),
      });
      expect(result.port).toBe(43000);
      const [first, second] = children as [ChildProcess, ChildProcess];
      expect(first.signalCode).toBe('SIGKILL');
      expect(() => process.kill(-first.pid!, 0)).toThrow(/ESRCH/);
      expect(second.exitCode).toBeNull();
      expect(second.signalCode).toBeNull();
    } finally {
      // The retired browser is not counted again; only the live replacement is stopped.
      await owner.dispose();
    }
    const [first, second] = children as [ChildProcess, ChildProcess];
    expect(second.signalCode).toBe('SIGTERM');
    expect(() => process.kill(-second.pid!, 0)).toThrow(/ESRCH/);
    expect(first.signalCode).toBe('SIGKILL');
  }, 30_000);

  it('still fails disposal when a browser that is not retired needed SIGKILL', async () => {
    expect(process.platform).not.toBe('win32');
    const owner = createFixtureOwner();
    const child = owner.ownChild(
      spawn(process.execPath, ['-e', ignoresTerm], {
        detached: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    );
    try {
      await ready(child);
      await expect(owner.dispose()).rejects.toThrow('Fixture cleanup failed');
      expect(child.signalCode).toBe('SIGKILL');
      expect(() => process.kill(-child.pid!, 0)).toThrow(/ESRCH/);
    } finally {
      try {
        process.kill(-child.pid!, 'SIGKILL');
      } catch {
        /* already gone */
      }
    }
  }, 30_000);
});
