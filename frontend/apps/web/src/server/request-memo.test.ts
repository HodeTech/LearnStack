// @vitest-environment node
import { AsyncLocalStorage } from 'node:async_hooks';

import type { headers } from 'next/headers';
import { describe, expect, it, vi } from 'vitest';

import { requestMemo } from './request-memo';

const mocks = vi.hoisted(() => ({ headers: vi.fn<() => Promise<Headers>>() }));
vi.mock('next/headers', () => ({ headers: mocks.headers }));

describe('request identity memoization', () => {
  it('shares pending and completed work across consumers using the exact request object', async () => {
    const incoming = new Headers({ host: 'first.example' });
    mocks.headers.mockResolvedValue(incoming);
    let release!: (value: object) => void;
    const load = vi.fn(
      () =>
        new Promise<object>((resolve) => {
          release = resolve;
        }),
    );
    const get = requestMemo(load);
    const first = get();
    const second = get();
    await vi.waitFor(() => expect(load).toHaveBeenCalledOnce());
    expect(load).toHaveBeenCalledWith(incoming);
    const value = {};
    release(value);
    expect(await first).toBe(value);
    expect(await second).toBe(value);
    expect(await get()).toBe(value);
    expect(load).toHaveBeenCalledOnce();
  });

  it('keeps equal headers in different requests separate, including overlapping execution', async () => {
    const context = new AsyncLocalStorage<Headers>();
    mocks.headers.mockImplementation(async () => {
      const incoming = context.getStore();
      if (!incoming) throw new Error('No request');
      return incoming;
    });
    const first = new Headers({ host: 'same.example' });
    const second = new Headers(first);
    let release!: () => void;
    const paused = new Promise<void>((resolve) => {
      release = resolve;
    });
    const load = vi.fn(async (incoming: Awaited<ReturnType<typeof headers>>) => {
      if (incoming === first) await paused;
      return { incoming };
    });
    const get = requestMemo(load);
    const firstResult = context.run(first, get);
    const secondResult = await context.run(second, get);
    expect(secondResult.incoming).toBe(second);
    release();
    expect((await firstResult).incoming).toBe(first);
    expect(load).toHaveBeenCalledTimes(2);
    const next = new Headers(first);
    expect((await context.run(next, get)).incoming).toBe(next);
    expect(load).toHaveBeenCalledTimes(3);
  });

  it('does not collide independently memoized loaders within one request', async () => {
    mocks.headers.mockResolvedValue(new Headers());
    const admission = requestMemo(async () => 'admission');
    const resource = requestMemo(async () => 42);
    expect(await admission()).toBe('admission');
    expect(await resource()).toBe(42);
    expect(await admission()).toBe('admission');
  });

  it.each(['rejected', 'thrown'] as const)(
    'retains %s work without retrying the request',
    async (kind) => {
      mocks.headers.mockResolvedValue(new Headers());
      const failure = new Error('controlled failure');
      const load = vi.fn(() => {
        if (kind === 'thrown') throw failure;
        return Promise.reject(failure);
      });
      const get = requestMemo(load);
      await expect(get()).rejects.toBe(failure);
      await expect(get()).rejects.toBe(failure);
      expect(load).toHaveBeenCalledOnce();
      mocks.headers.mockResolvedValue(new Headers());
      await expect(get()).rejects.toBe(failure);
      expect(load).toHaveBeenCalledTimes(2);
    },
  );

  it('retains a refused admission value for the request', async () => {
    mocks.headers.mockResolvedValue(new Headers());
    const load = vi.fn(async () => null);
    const get = requestMemo(load);
    expect(await get()).toBeNull();
    expect(await get()).toBeNull();
    expect(load).toHaveBeenCalledOnce();
  });
});
