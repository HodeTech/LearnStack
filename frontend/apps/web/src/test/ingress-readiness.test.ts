// @vitest-environment node
import type { ChildProcess } from 'node:child_process';
import { EventEmitter } from 'node:events';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  boundNextVersionFetch,
  createIngressReadiness,
  isReadinessNotification,
} from '../../scripts/ingress-readiness.mjs';

function child(pid = 600001) {
  return Object.assign(new EventEmitter(), {
    pid,
    exitCode: null as number | null,
    signalCode: null as NodeJS.Signals | null,
  });
}

function owner(process: ReturnType<typeof child>) {
  return createIngressReadiness(process as unknown as ChildProcess);
}

function bind(process: ReturnType<typeof child>, address = '127.0.0.1', pid = process.pid) {
  process.emit('message', { kind: 'bound', pid, port: 3000, address });
}

const healthy = async () => ({ status: 200, body: '{"status":"healthy"}' });
const active = () => {};

async function failure(pending: Promise<unknown>) {
  const captured = pending.then(
    () => {
      throw new Error('Planted refusal unexpectedly passed');
    },
    (error: Error) => error,
  );
  await vi.advanceTimersByTimeAsync(22000);
  const error = await captured;
  return {
    message: error.message,
    value: JSON.parse(error.message.slice(error.message.indexOf(': ') + 2)),
  };
}

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('native fixture readiness observations', () => {
  it('bounds only the exact one-argument version lookup and preserves unrelated arguments/results', async () => {
    const response = new Response('{}');
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response);
    const notify = vi.fn();
    const observed = boundNextVersionFetch(fetch, notify);
    const options = { headers: { 'private-fixture-header': 'PRIVATE' } };
    const receiver = {};
    await expect(
      observed.call(receiver, 'https://registry.npmjs.org/-/package/next/dist-tags'),
    ).resolves.toBe(response);
    expect(fetch).toHaveBeenCalledOnce();
    const signal = fetch.mock.calls[0]![1]!.signal!;
    expect(signal).toBeInstanceOf(AbortSignal);
    expect(signal.aborted).toBe(false);
    expect(fetch.mock.contexts[0]).toBe(receiver);
    expect(notify.mock.calls.map(([value]) => value.state)).toEqual(['pending', 'settled']);
    expect(JSON.stringify(notify.mock.calls)).not.toMatch(/PRIVATE|https:|headers/);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(3000);
    expect(signal.aborted).toBe(false);
    notify.mockClear();
    const promise = Promise.resolve(response);
    fetch.mockReturnValueOnce(promise);
    expect(observed('https://unrelated.invalid/PRIVATE')).toBe(promise);
    fetch.mockReturnValueOnce(promise);
    const url = new URL('https://registry.npmjs.org/-/package/next/dist-tags');
    expect(observed(url, options)).toBe(promise);
    expect(fetch.mock.calls.at(-1)).toEqual([url, options]);
    fetch.mockReturnValueOnce(promise);
    expect(observed('https://registry.npmjs.org/-/package/next/dist-tags', options)).toBe(promise);
    expect(fetch.mock.calls.at(-1)![1]).toBe(options);
    fetch.mockReturnValueOnce(promise);
    expect(observed('https://registry.npmjs.org/-/package/next/dist-tags', undefined)).toBe(
      promise,
    );
    expect(notify).not.toHaveBeenCalled();
  });

  it('retains original fetch rejection identity and contains observer failures and arbitrary error values', async () => {
    const error = Object.assign(new Error('PRIVATE-FETCH'), { code: 'PRIVATE-CODE' });
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(error);
    const notify = vi.fn();
    await expect(
      boundNextVersionFetch(fetch, notify)('https://registry.npmjs.org/-/package/next/dist-tags'),
    ).rejects.toBe(error);
    expect(notify.mock.calls.map(([value]) => [value.state, value.errorKind])).toEqual([
      ['pending', 'none'],
      ['failed', 'other'],
    ]);
    expect(JSON.stringify(notify.mock.calls)).not.toContain('PRIVATE');
    fetch.mockImplementation(() => {
      throw error;
    });
    let synchronous: unknown;
    try {
      boundNextVersionFetch(fetch, notify)('https://registry.npmjs.org/-/package/next/dist-tags');
    } catch (value) {
      synchronous = value;
    }
    expect(synchronous).toBe(error);
    expect(vi.getTimerCount()).toBe(0);
    const response = new Response('{}');
    fetch.mockResolvedValue(response);
    await expect(
      boundNextVersionFetch(fetch, () => {
        throw new Error('PRIVATE-OBSERVER');
      })('https://registry.npmjs.org/-/package/next/dist-tags'),
    ).resolves.toBe(response);
    fetch.mockRejectedValue(error);
    await expect(
      boundNextVersionFetch(fetch, () => {
        throw new Error('PRIVATE-OBSERVER');
      })('https://registry.npmjs.org/-/package/next/dist-tags'),
    ).rejects.toBe(error);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('aborts the exact lookup at two seconds and retains the real delegate rejection', async () => {
    let signal!: AbortSignal;
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_input, options) => {
      signal = options!.signal!;
      return new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(signal.reason), { once: true });
      });
    });
    const notify = vi.fn();
    const pending = boundNextVersionFetch(
      fetch,
      notify,
    )('https://registry.npmjs.org/-/package/next/dist-tags');
    const rejected = pending.catch((error: unknown) => error);
    await vi.advanceTimersByTimeAsync(1999);
    expect(signal.aborted).toBe(false);
    expect(notify).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    expect(signal.aborted).toBe(true);
    expect(await rejected).toBe(signal.reason);
    expect(notify).toHaveBeenCalledTimes(2);
    expect(notify.mock.calls[1]![0]).toEqual({
      state: 'failed',
      errorKind: 'timeout',
      elapsedMilliseconds: 2000,
    });
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not abandon or substitute a delegate that has not settled after abort', async () => {
    let signal!: AbortSignal;
    let complete!: (response: Response) => void;
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_input, options) => {
      signal = options!.signal!;
      return new Promise((resolve) => {
        complete = resolve;
      });
    });
    const notify = vi.fn();
    const pending = boundNextVersionFetch(
      fetch,
      notify,
    )('https://registry.npmjs.org/-/package/next/dist-tags');
    let settled = false;
    const joined = pending.then((response) => {
      settled = true;
      return response;
    });
    await vi.advanceTimersByTimeAsync(2000);
    expect(signal.aborted).toBe(true);
    expect(settled).toBe(false);
    expect(notify).toHaveBeenCalledOnce();
    const response = new Response('{}');
    complete(response);
    await expect(joined).resolves.toBe(response);
    expect(notify.mock.calls.map(([value]) => value.state)).toEqual(['pending', 'settled']);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('records only current-child closed fetch observations, including a pending wait', async () => {
    const process = child();
    const readiness = owner(process);
    const upgrades: unknown[] = [];
    process.on('message', (message) => {
      if (isReadinessNotification(message)) return;
      upgrades.push(message);
    });
    const observation = {
      kind: 'version-fetch',
      state: 'pending',
      errorKind: 'none',
      elapsedMilliseconds: 0,
    };
    process.emit('message', { ...observation, pid: process.pid + 1 });
    process.emit('message', { ...observation, state: 'PRIVATE', pid: process.pid });
    process.emit('message', { ...observation, errorKind: 'PRIVATE', pid: process.pid });
    process.emit('message', { ...observation, elapsedMilliseconds: Infinity, pid: process.pid });
    let resolve!: (response: Response) => void;
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const pending = boundNextVersionFetch(fetch, (value) =>
      process.emit('message', {
        kind: 'version-fetch',
        pid: process.pid,
        ...value,
      }),
    )('https://registry.npmjs.org/-/package/next/dist-tags');
    const { message, value } = await failure(
      readiness.wait({ port: 3000, probe: healthy, assertActive: active }),
    );
    expect(value).toMatchObject({
      versionFetch: 'pending',
      versionFetchError: 'none',
      versionFetchCount: 1,
      versionFetchMilliseconds: 20000,
    });
    expect(message).not.toContain('PRIVATE');
    const response = new Response('{}');
    resolve(response);
    await expect(pending).resolves.toBe(response);
    const upgrade = { surface: 'sink', sanitized: true };
    process.emit('message', upgrade);
    expect(upgrades).toEqual([upgrade]);
  });

  it.each([
    ['ECONNREFUSED', 'connection_refused'],
    ['INGRESS_PROBE_TIMEOUT', 'timeout'],
    ['ETIMEDOUT', 'timeout'],
    ['ECONNRESET', 'connection_reset'],
    ['ERR_TLS_CERT_ALTNAME_INVALID', 'tls'],
    ['PRIVATE-CODE', 'other'],
  ])('classifies planted %s without exporting arbitrary error values', async (code, kind) => {
    const process = child();
    const readiness = owner(process);
    process.emit('message', { kind: 'readiness', pid: process.pid, phase: 'preparing' });
    const probe = vi.fn(async () => {
      throw Object.assign(new Error('PRIVATE-MESSAGE https://PRIVATE-HOST/?token=PRIVATE-TOKEN'), {
        code,
        cause: 'PRIVATE-CAUSE',
        detail: 'PRIVATE-DETAIL',
      });
    });
    const { message, value } = await failure(
      readiness.wait({ port: 3000, probe, assertActive: active }),
    );
    expect(message).not.toMatch(/PRIVATE|https:/);
    expect(value).toMatchObject({
      phase: 'preparing',
      bind: 'unobserved',
      exit: 'running',
      probeKind: kind,
      probeCount: 200,
      responseCount: 0,
      lastStatus: null,
      waitMilliseconds: 20000,
    });
    expect(probe).toHaveBeenCalledTimes(200);
  });

  it('records a two-second pending probe separately from the unchanged whole wait', async () => {
    const process = child();
    const readiness = owner(process);
    const probe = async () =>
      new Promise<never>((_resolve, reject) => {
        setTimeout(
          () => reject(Object.assign(new Error('PRIVATE'), { code: 'INGRESS_PROBE_TIMEOUT' })),
          2000,
        );
      });
    const { value } = await failure(readiness.wait({ port: 3000, probe, assertActive: active }));
    expect(value).toMatchObject({
      probeKind: 'timeout',
      probeCount: 10,
      responseCount: 0,
      lastProbeMilliseconds: 2000,
      waitMilliseconds: 21000,
    });
  });

  it('contains an error whose code accessor itself throws private data', async () => {
    const readiness = owner(child());
    const { message, value } = await failure(
      readiness.wait({
        port: 3000,
        probe: async () => {
          throw {
            get code() {
              throw new Error('PRIVATE-ACCESSOR');
            },
          };
        },
        assertActive: active,
      }),
    );
    expect(value.probeKind).toBe('other');
    expect(message).not.toContain('PRIVATE');
  });

  it.each(['missing', 'different_child', 'other_address'])(
    'refuses healthy responses with %s bind evidence',
    async (kind) => {
      const process = child();
      const readiness = owner(process);
      if (kind === 'different_child') bind(process, '127.0.0.1', process.pid + 1);
      if (kind === 'other_address') bind(process, 'PRIVATE-ADDRESS');
      const { message, value } = await failure(
        readiness.wait({ port: 3000, probe: healthy, assertActive: active }),
      );
      expect(message).not.toContain('PRIVATE');
      expect(value).toMatchObject({
        bind: kind === 'other_address' ? 'other_observed' : 'unobserved',
        probeKind: 'healthy_unattributed',
        responseCount: 200,
        lastStatus: 200,
      });
    },
  );

  it('accepts only current-child loopback bind plus actual healthy response', async () => {
    const process = child();
    const readiness = owner(process);
    bind(process);
    const probe = vi.fn(healthy);
    const observation = await readiness.wait({ port: 3000, probe, assertActive: active });
    expect(observation).toMatchObject({
      bind: 'owned_loopback',
      probeKind: 'healthy',
      probeCount: 1,
    });
    expect(probe).toHaveBeenCalledOnce();
  });

  it.each(['spawn_failed', 'exited_zero', 'exited_nonzero', 'signalled'])(
    'records %s before probing',
    async (exit) => {
      const process = child();
      const readiness = owner(process);
      if (exit === 'spawn_failed') process.emit('error', new Error('PRIVATE-SPAWN-ERROR'));
      if (exit === 'exited_zero') process.exitCode = 0;
      if (exit === 'exited_nonzero') process.exitCode = 73;
      if (exit === 'signalled') process.signalCode = 'SIGTERM';
      const probe = vi.fn(healthy);
      const { message, value } = await failure(
        readiness.wait({ port: 3000, probe, assertActive: active }),
      );
      expect(message).toMatch(/^Fixture startup failed:/);
      expect(message).not.toContain('PRIVATE');
      expect(value).toMatchObject({ exit, probeCount: 0, probeKind: 'none', waitMilliseconds: 0 });
      expect(probe).not.toHaveBeenCalled();
    },
  );

  it('keeps phase and diagnostic evidence tied to its own child and ignores unknown phases', async () => {
    const first = child();
    const second = child(600002);
    const firstReadiness = owner(first);
    const secondReadiness = owner(second);
    firstReadiness.output('Failed to ');
    firstReadiness.output('compile PRIVATE-TOKEN');
    second.emit('message', { kind: 'readiness', pid: first.pid, phase: 'prepared' });
    second.emit('message', { kind: 'readiness', pid: second.pid, phase: 'PRIVATE-PHASE' });
    second.emit('message', { kind: 'readiness', pid: second.pid, phase: 'preparing' });
    second.emit('message', { kind: 'readiness', pid: second.pid, phase: 'prepared' });
    second.emit('message', { kind: 'readiness', pid: second.pid, phase: 'preparing' });
    const { message, value } = await failure(
      secondReadiness.wait({
        port: 3000,
        probe: async () => ({ status: 503, body: 'PRIVATE-BODY' }),
        assertActive: active,
      }),
    );
    expect(value).toMatchObject({
      phase: 'prepared',
      compilation: false,
      probeKind: 'http_unhealthy',
      lastStatus: 503,
    });
    expect(message).not.toContain('PRIVATE');
    const firstResult = await failure(
      firstReadiness.wait({ port: 3000, probe: healthy, assertActive: active }),
    );
    expect(firstResult.value.compilation).toBe(true);
  });

  it('distinguishes malformed health JSON from a subsequent rejected transport', async () => {
    const process = child();
    const readiness = owner(process);
    bind(process);
    const probe = vi
      .fn()
      .mockResolvedValueOnce({ status: 200, body: 'PRIVATE-BODY' })
      .mockRejectedValue(Object.assign(new Error('PRIVATE'), { code: 'ECONNREFUSED' }));
    const { message, value } = await failure(
      readiness.wait({ port: 3000, probe, assertActive: active }),
    );
    expect(value).toMatchObject({
      probeKind: 'connection_refused',
      responseCount: 1,
      lastStatus: 200,
    });
    expect(message).not.toContain('PRIVATE');
    const malformed = await failure(
      owner(child()).wait({
        port: 3000,
        probe: async () => ({ status: 200, body: 'PRIVATE-BODY' }),
        assertActive: active,
      }),
    );
    expect(malformed.value.probeKind).toBe('invalid_json');
    expect(malformed.message).not.toContain('PRIVATE');
  });
});
