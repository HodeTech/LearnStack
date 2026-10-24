// @vitest-environment node
import { EventEmitter } from 'node:events';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { INGRESS_HEADER, mintProvenance } from '../ingress';
import { auth } from './auth';
import { createBffRequestRuntime } from './request-runtime';

const incoming = vi.hoisted(() => ({ value: new Headers() }));
vi.mock('next/headers', () => ({ headers: async () => incoming.value }));
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});
describe('read-only app-local auth()', () => {
  it('uses actual signed provenance and native ALS; repeated RSC reads cannot refresh, touch or write cookies', async () => {
    const secret = 'test-hop-'.padEnd(48, 'x');
    vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
    const binding = {
      host: 'institution.example.test:3000',
      peer: '203.0.113.1',
      method: 'GET',
      target: '/studio',
    };
    incoming.value = new Headers({ [INGRESS_HEADER]: mintProvenance(binding, secret) });
    const runtime = createBffRequestRuntime();
    const original = Object.getOwnPropertyDescriptor;
    vi.spyOn(Object, 'getOwnPropertyDescriptor').mockImplementation((object, key) =>
      object === globalThis && key === Symbol.for('learnstack.bff-request.v1')
        ? { value: runtime.holder, configurable: false, enumerable: false, writable: false }
        : original(object, key),
    );
    const request = Object.assign(new EventEmitter(), { aborted: false, headers: {} });
    const response = Object.assign(new EventEmitter(), { destroyed: false, writableEnded: false });
    const session = {
      state: 'authenticated' as const,
      displayName: 'School',
      expiresAt: Date.now() + 60_000,
      limitedByAbsolute: false,
      csrfToken: 'A'.repeat(43),
    };
    await runtime.run(binding, request, response, async () => {
      runtime.holder.publish(binding, session);
      const first = await auth();
      const second = await auth();
      expect(first).toBe(second);
      expect(Object.isFrozen(first)).toBe(true);
      expect(first).toEqual(session);
      response.emit('finish');
      await expect(auth()).rejects.toThrow('BFF request context unavailable');
    });
    runtime.stop();
  });
  it('a forged envelope cannot turn request headers into session authority', async () => {
    const secret = 'test-hop-'.padEnd(48, 'x');
    vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
    const binding = {
      host: 'institution.example.test:3000',
      peer: '203.0.113.1',
      method: 'GET',
      target: '/studio',
    };
    const runtime = createBffRequestRuntime();
    const read = vi.fn(runtime.holder.read);
    const holder = Object.freeze({ ...runtime.holder, read });
    const original = Object.getOwnPropertyDescriptor;
    vi.spyOn(Object, 'getOwnPropertyDescriptor').mockImplementation((object, key) =>
      object === globalThis && key === Symbol.for('learnstack.bff-request.v1')
        ? { value: holder, configurable: false, enumerable: false, writable: false }
        : original(object, key),
    );
    const request = Object.assign(new EventEmitter(), { aborted: false, headers: {} });
    const response = Object.assign(new EventEmitter(), { destroyed: false, writableEnded: false });
    const session = {
      state: 'authenticated' as const,
      displayName: 'School',
      expiresAt: Date.now() + 60_000,
      limitedByAbsolute: false,
      csrfToken: 'A'.repeat(43),
    };
    try {
      await runtime.run(binding, request, response, async () => {
        runtime.holder.publish(binding, session);
        incoming.value = new Headers({ [INGRESS_HEADER]: mintProvenance(binding, secret) });
        expect(await auth()).toEqual(session);
        expect(read).toHaveBeenCalledOnce();
        read.mockClear();
        incoming.value = new Headers({ [INGRESS_HEADER]: 'forged' });
        await expect(auth()).rejects.toThrow('BFF request context unavailable');
        expect(read).not.toHaveBeenCalled();
        response.emit('finish');
      });
    } finally {
      runtime.stop();
    }
  });
});
