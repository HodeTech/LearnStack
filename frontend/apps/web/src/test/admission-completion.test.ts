// @vitest-environment node
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { describe, expect, it } from 'vitest';

import { createNativeCompletionObserver } from '../../scripts/admission-completion.mjs';
import { createPublicAdmissionRuntime } from '../server/public-admission-runtime';

function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage,
    response: Object.assign(new EventEmitter(), {
      destroyed: false,
      writableEnded: false,
    }) as ServerResponse,
  };
}
const binding = {
  host: 'institution.example',
  peer: '203.0.113.8',
  method: 'GET' as const,
  target: '/en/courses',
};
const site = {
  displayName: 'Institution',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  showPlatformAttribution: true,
  theme: null,
};

function start() {
  const runtime = createPublicAdmissionRuntime();
  const owner = lifetime();
  runtime.run(binding, owner.request, owner.response, () =>
    runtime.holder.begin(binding).publish(site),
  );
  return { runtime, owner };
}

describe('native completion proof barrier', () => {
  it.each(['finish', 'close'] as const)(
    'waits for native %s after apparent client completion',
    async (event) => {
      const { runtime, owner } = start();
      const observer = createNativeCompletionObserver();
      observer.track(owner.response); // Production disposal listeners were registered first.
      let settled = false;
      const completion = observer.settled().then(() => {
        settled = true;
      });
      await Promise.resolve(); // A completed client/delegation promise cannot release this barrier.
      expect(settled).toBe(false);
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
      owner.response.emit(event);
      await completion;
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expect(owner.response.listenerCount('finish')).toBe(0);
      expect(owner.response.listenerCount('close')).toBe(0);
      await observer.settled();
    },
  );

  it.each(['finish', 'close'] as const)('does not conceal missing cleanup on %s', async (event) => {
    const { runtime, owner } = start();
    owner.response.removeAllListeners('finish');
    owner.response.removeAllListeners('close'); // Planted missing production cleanup.
    const observer = createNativeCompletionObserver();
    observer.track(owner.response);
    owner.response.emit(event);
    await observer.settled(); // Independent native event completes even though the runtime leaks.
    expect(() => assert.deepEqual(runtime.counts(), { active: 0, snapshots: 0 })).toThrow();
    runtime.shutdown();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('awaits every overlapping native response and removes both owned listeners', async () => {
    const observer = createNativeCompletionObserver();
    const first = lifetime().response;
    const second = lifetime().response;
    observer.track(first);
    observer.track(second);
    let settled = false;
    const completion = observer.settled().then(() => {
      settled = true;
    });
    first.emit('finish');
    await Promise.resolve();
    expect(settled).toBe(false);
    second.emit('close');
    await completion;
    expect(settled).toBe(true);
    for (const response of [first, second]) {
      expect(response.listenerCount('finish')).toBe(0);
      expect(response.listenerCount('close')).toBe(0);
    }
  });

  it('rejects duplicate registration without adding another pair of listeners', async () => {
    const observer = createNativeCompletionObserver();
    const response = lifetime().response;
    observer.track(response);
    expect(() => observer.track(response)).toThrow('Native response already observed');
    expect(response.listenerCount('finish')).toBe(1);
    expect(response.listenerCount('close')).toBe(1);
    response.emit('finish');
    await observer.settled();
  });
});
