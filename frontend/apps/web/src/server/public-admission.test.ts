// @vitest-environment node
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { describe, expect, it, vi } from 'vitest';

import { beginPublicAdmission, readPublicAdmission } from './public-admission';
import { installPublicAdmissionRuntime } from './public-admission-runtime';

const key = Symbol.for('learnstack.public-admission.v1');
const binding = {
  host: 'institution.example:3000',
  peer: '203.0.113.9',
  method: 'GET',
  target: '/tr/courses',
};
const site = {
  displayName: 'Institution',
  enabledLocales: ['tr', 'en'],
  defaultLocale: 'tr',
  showPlatformAttribution: true,
  theme: null,
};
const unavailable = 'Public admission context unavailable';

function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage,
    response: Object.assign(new EventEmitter(), {
      destroyed: false,
      writableEnded: false,
    }) as ServerResponse,
  };
}

describe('server-only admission facade retrieves the sole native holder', () => {
  // One real installation preserves the production nonconfigurable descriptor.
  it('lazily refuses absence, shares a native store across module instances and validates the holder', async () => {
    expect(Object.getOwnPropertyDescriptor(globalThis, key)).toBeUndefined();
    expect(() => beginPublicAdmission(binding)).toThrow(unavailable);
    expect(() => readPublicAdmission(binding)).toThrow(unavailable);
    expect(Object.getOwnPropertyDescriptor(globalThis, key)).toBeUndefined();
    const runtime = installPublicAdmissionRuntime();
    try {
      const descriptor = Object.getOwnPropertyDescriptor(globalThis, key)!;
      expect(descriptor).toEqual({
        value: runtime.holder,
        configurable: false,
        enumerable: false,
        writable: false,
      });
      expect(Object.isFrozen(runtime.holder)).toBe(true);
      expect(runtime.holder.version).toBe(1);
      expect(() => installPublicAdmissionRuntime()).toThrow(unavailable);
      expect(() => beginPublicAdmission(binding)).toThrow(unavailable);
      expect(() => readPublicAdmission(binding)).toThrow(unavailable);
      vi.resetModules();
      const secondFacade = await import('./public-admission');
      expect(secondFacade.readPublicAdmission).not.toBe(readPublicAdmission);
      const owner = lifetime();
      runtime.run(binding, owner.request, owner.response, () => {
        const writing = beginPublicAdmission(binding);
        writing.publish(site);
        const first = readPublicAdmission(binding);
        const second = secondFacade.readPublicAdmission({ ...binding });
        expect(second.site).toBe(first.site);
        expect(second.site).toEqual(site);
        expect(second.site).not.toBe(site);
        expect(second.signal).toBe(writing.signal);
        second.assertActive();
      });
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
      owner.response.emit('finish');
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expect(owner.request.listenerCount('aborted')).toBe(0);
      expect(owner.response.listenerCount('close')).toBe(0);
      expect(owner.response.listenerCount('finish')).toBe(0);
      const getDescriptor = Object.getOwnPropertyDescriptor;
      const dirtyDescriptors: PropertyDescriptor[] = [
        { ...descriptor, enumerable: true },
        { ...descriptor, configurable: true },
        { ...descriptor, writable: true },
        { ...descriptor, value: null },
        { ...descriptor, value: 1 },
        { ...descriptor, value: { ...runtime.holder } },
        { ...descriptor, value: Object.freeze({ ...runtime.holder, version: 2 }) },
        { ...descriptor, value: Object.freeze({ version: 1, read: runtime.holder.read }) },
        { ...descriptor, value: Object.freeze({ version: 1, begin: runtime.holder.begin }) },
        { ...descriptor, value: Object.freeze({ ...runtime.holder, begin: false }) },
        { ...descriptor, value: Object.freeze({ ...runtime.holder, read: false }) },
        { get: () => runtime.holder, configurable: false, enumerable: false },
      ];
      for (const dirty of dirtyDescriptors) {
        const owner = lifetime();
        runtime.run(binding, owner.request, owner.response, () => {
          const lookup = vi
            .spyOn(Object, 'getOwnPropertyDescriptor')
            .mockImplementation((object, name) =>
              object === globalThis && name === key ? dirty : getDescriptor(object, name),
            );
          try {
            expect(() => beginPublicAdmission(binding)).toThrow(unavailable);
            expect(() => readPublicAdmission(binding)).toThrow(unavailable);
            expect(runtime.counts()).toEqual({ active: 1, snapshots: 0 });
          } finally {
            lookup.mockRestore();
          }
          // The same scope succeeds after the dirty descriptor is removed.
          beginPublicAdmission(binding).publish(site);
          expect(readPublicAdmission(binding).site).toEqual(site);
        });
        owner.response.emit('finish');
        expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      }
      expect(Object.getOwnPropertyDescriptor(globalThis, key)).toEqual(descriptor);
    } finally {
      runtime.shutdown();
    }
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });
});
