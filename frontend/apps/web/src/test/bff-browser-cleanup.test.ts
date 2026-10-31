// @vitest-environment node
import type { ChildProcess } from 'node:child_process';
import { EventEmitter } from 'node:events';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { createBffBrowserCleanup } from '../../scripts/bff-proof-support.mjs';
import { stopTestChild } from '../../scripts/stop-test-child.mjs';

vi.mock('../../scripts/stop-test-child.mjs', () => ({ stopTestChild: vi.fn() }));

afterEach(() => {
  vi.resetAllMocks();
  vi.useRealTimers();
});

function fixture() {
  const order: string[] = [];
  const browserProcess = Object.assign(new EventEmitter(), {
    exitCode: 0,
    signalCode: null,
  }) as ChildProcess;
  const nativeProcess = { exitCode: 0, signalCode: null } as ChildProcess;
  const send = vi.fn(async (_method: 'Browser.close') => {
    order.push('cdp');
  });
  const browser = {
    newBrowserCDPSession: vi.fn(async () => ({ send })),
    close: vi.fn(async () => {
      order.push('disconnect');
    }),
  };
  const nativeServer = {
    process: nativeProcess,
    completed: Promise.resolve({ code: 0 as number | null, signal: null as NodeJS.Signals | null }),
  };
  const owner = {
    dispose: vi.fn(async () => {
      order.push('owner');
    }),
  };
  vi.mocked(stopTestChild).mockImplementation(async (child) => {
    order.push(child === browserProcess ? 'browser-stop' : 'native-stop');
    return { forced: false };
  });
  const cleanup = createBffBrowserCleanup(() => ({ browser, browserProcess, nativeServer, owner }));
  return { cleanup, browser, browserProcess, nativeProcess, nativeServer, owner, send, order };
}

describe('BFF owned browser cleanup', () => {
  it('requests Chromium shutdown before disconnect and retains both owned stop and native exit checks', async () => {
    const state = fixture();
    const first = state.cleanup.run();
    expect(state.cleanup.run()).toBe(first);
    await expect(first).resolves.toBeUndefined();
    expect(state.send).toHaveBeenCalledExactlyOnceWith('Browser.close');
    expect(state.order).toEqual(['cdp', 'disconnect', 'browser-stop', 'native-stop', 'owner']);
    expect(state.cleanup.failureStage).toBeNull();
    await state.cleanup.run();
    expect(stopTestChild).toHaveBeenCalledTimes(2);
  });

  it('waits for owned browser exit before disconnect or the group-stop fallback', async () => {
    const state = fixture();
    Object.assign(state.browserProcess, { exitCode: null });
    const pending = state.cleanup.run();
    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(state.browserProcess.listenerCount('exit')).toBe(1);
    expect(state.browser.close).not.toHaveBeenCalled();
    expect(stopTestChild).not.toHaveBeenCalled();
    Object.assign(state.browserProcess, { exitCode: 0 });
    state.browserProcess.emit('exit', 0, null);
    await pending;
    expect(state.cleanup.failureStage).toBeNull();
    expect(state.browserProcess.listenerCount('exit')).toBe(0);
  });

  it('accepts a lost CDP acknowledgement only with actual clean owned exit and disposal witnesses', async () => {
    const state = fixture();
    state.send.mockRejectedValue(new Error('PRIVATE-CANARY'));
    await expect(state.cleanup.run()).resolves.toBeUndefined();
    expect(state.browser.close).toHaveBeenCalledOnce();
    expect(stopTestChild).toHaveBeenCalledTimes(2);
    expect(state.owner.dispose).toHaveBeenCalledOnce();
    expect(state.cleanup.failureStage).toBeNull();
  });

  it.each(['live', 'nonzero', 'signal', 'forced', 'unconfirmed', 'owner'] as const)(
    'never clears an unknown CDP failure with an incomplete %s witness',
    async (missing) => {
      const state = fixture();
      state.send.mockRejectedValue(new Error('PRIVATE-CANARY'));
      if (missing === 'live') {
        vi.useFakeTimers();
        Object.assign(state.browserProcess, { exitCode: null });
      }
      if (missing === 'nonzero') Object.assign(state.browserProcess, { exitCode: 7 });
      if (missing === 'signal') Object.assign(state.browserProcess, { signalCode: 'SIGTERM' });
      if (missing === 'forced') vi.mocked(stopTestChild).mockResolvedValueOnce({ forced: true });
      if (missing === 'unconfirmed')
        vi.mocked(stopTestChild).mockRejectedValueOnce(new Error('PRIVATE-CANARY'));
      if (missing === 'owner') state.owner.dispose.mockRejectedValue(new Error('PRIVATE-CANARY'));
      const pending = state.cleanup.run().catch((failure: unknown) => failure);
      if (missing === 'live') await vi.advanceTimersByTimeAsync(5001);
      const error = await pending;
      expect(String(error)).toBe('Error: BFF browser cleanup failed');
      expect(state.cleanup.failureStage).toBe('cleanup-cdp');
      expect(stopTestChild).toHaveBeenCalledTimes(2);
      expect(state.owner.dispose).toHaveBeenCalledOnce();
    },
  );

  it.each([
    ['disconnect', 'cleanup-disconnect'],
    ['browser-stop', 'cleanup-browser-stop'],
    ['browser-forced', 'cleanup-browser-forced'],
    ['browser-exit', 'cleanup-browser-exit'],
    ['browser-signal', 'cleanup-browser-signal'],
    ['native-stop', 'cleanup-native-stop'],
    ['native-forced', 'cleanup-native-forced'],
    ['owner', 'cleanup-owner'],
    ['native-exit', 'cleanup-native-exit'],
    ['native-signal', 'cleanup-native-signal'],
  ] as const)(
    'retains the first %s failure across cleanup retries without provider text',
    async (kind, stage) => {
      const state = fixture();
      const refusal = new Error('PRIVATE-CANARY');
      if (kind === 'disconnect') state.browser.close.mockRejectedValue(refusal);
      if (kind === 'browser-stop') vi.mocked(stopTestChild).mockRejectedValueOnce(refusal);
      if (kind === 'browser-forced')
        vi.mocked(stopTestChild).mockResolvedValueOnce({ forced: true });
      if (kind === 'browser-exit') Object.assign(state.browserProcess, { exitCode: 7 });
      if (kind === 'browser-signal') Object.assign(state.browserProcess, { signalCode: 'SIGTERM' });
      if (kind === 'native-stop' || kind === 'native-forced') {
        vi.mocked(stopTestChild).mockResolvedValueOnce({ forced: false });
        if (kind === 'native-stop') vi.mocked(stopTestChild).mockRejectedValueOnce(refusal);
        else vi.mocked(stopTestChild).mockResolvedValueOnce({ forced: true });
      }
      if (kind === 'owner') state.owner.dispose.mockRejectedValue(refusal);
      if (kind === 'native-exit')
        state.nativeServer.completed = Promise.resolve({ code: 7, signal: null });
      if (kind === 'native-signal')
        state.nativeServer.completed = Promise.resolve({ code: null, signal: 'SIGTERM' });
      const first = state.cleanup.run();
      await expect(first).rejects.toThrow('BFF browser cleanup failed');
      expect(state.cleanup.failureStage).toBe(stage);
      expect(state.cleanup.run()).toBe(first);
      await expect(state.cleanup.run()).rejects.toThrow('BFF browser cleanup failed');
      expect(state.cleanup.failureStage).toBe(stage);
      expect(stopTestChild).toHaveBeenCalledTimes(2);
      expect(state.owner.dispose).toHaveBeenCalledOnce();
    },
  );

  it('keeps an expired graceful-exit wait red after the fallback eventually stops the browser', async () => {
    vi.useFakeTimers();
    const state = fixture();
    Object.assign(state.browserProcess, { exitCode: null });
    vi.mocked(stopTestChild).mockImplementation(async (child) => {
      if (child === state.browserProcess) Object.assign(child, { exitCode: 0 });
      return { forced: false };
    });
    const pending = state.cleanup.run().catch((error: unknown) => error);
    await vi.advanceTimersByTimeAsync(5001);
    expect(String(await pending)).toBe('Error: BFF browser cleanup failed');
    expect(state.cleanup.failureStage).toBe('cleanup-browser-wait');
    expect(state.owner.dispose).toHaveBeenCalledOnce();
    expect(state.browserProcess.listenerCount('exit')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('bounds hung CDP and disconnect together and handles late rejection while still attempting every stop', async () => {
    vi.useFakeTimers();
    const state = fixture();
    Object.assign(state.browserProcess, { exitCode: null });
    let reject!: (reason: Error) => void;
    state.send.mockImplementation(
      () =>
        new Promise<void>((_resolve, fail) => {
          reject = fail;
        }),
    );
    state.browser.close.mockImplementation(() => new Promise<void>(() => {}));
    const failure = state.cleanup.run().catch((error: unknown) => error);
    await vi.advanceTimersByTimeAsync(5005);
    expect(String(await failure)).toBe('Error: BFF browser cleanup failed');
    expect(state.cleanup.failureStage).toBe('cleanup-cdp');
    expect(state.browser.close).toHaveBeenCalledOnce();
    expect(stopTestChild).toHaveBeenCalledTimes(2);
    expect(state.owner.dispose).toHaveBeenCalledOnce();
    reject(new Error('PRIVATE-CANARY late protocol rejection'));
    await Promise.resolve();
    expect(vi.getTimerCount()).toBe(0);
  });
});
