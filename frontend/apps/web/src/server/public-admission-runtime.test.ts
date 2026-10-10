// @vitest-environment node
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { describe, expect, it, vi } from 'vitest';

import type { IngressContext } from './ingress';
import { createPublicAdmissionRuntime } from './public-admission-runtime';
import type { PublicAdmissionRead, PublicAdmissionWrite } from './public-admission-runtime';
import { MAX_PUBLIC_RESPONSE_BYTES } from './public-api-limits';
import { publicThemeCss } from '../components/public/theme';

const binding: IngressContext = {
  host: 'institution.example:3000',
  peer: '203.0.113.9',
  method: 'GET',
  target: '/tr/courses?cursor=inert',
};
const unavailable = 'Public admission context unavailable';
const completed = 'Public admission request completed';

function site(displayName = 'Institution') {
  return {
    displayName,
    enabledLocales: ['tr', 'en'],
    defaultLocale: 'tr',
    showPlatformAttribution: true,
    theme: { primary: '#123456', background: '#ffffff', foreground: '#000000', muted: '#555555' },
  };
}

function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage,
    response: Object.assign(new EventEmitter(), {
      destroyed: false,
      writableEnded: false,
    }) as ServerResponse,
  };
}

function expectReleased(owner: ReturnType<typeof lifetime>) {
  expect(owner.request.listenerCount('aborted')).toBe(0);
  expect(owner.response.listenerCount('finish')).toBe(0);
  expect(owner.response.listenerCount('close')).toBe(0);
}

function deferred() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

describe('native public admission state and closed snapshot', () => {
  it('Public_Admission_Uses_Only_The_Active_Native_Context', () => {
    const runtime = createPublicAdmissionRuntime();
    expect(() => runtime.holder.begin(binding)).toThrow(unavailable);
    expect(() => runtime.holder.read(binding)).toThrow(unavailable);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('Public_Admission_Reuses_One_Immutable_Snapshot_Per_Request', () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    const input = {
      ...site(),
      tenantId: 'private identifier',
      providerDiagnostic: 'private detail',
    };
    Object.assign(input.theme, { privateColor: 'private extra color' });
    runtime.run(binding, owner.request, owner.response, () => {
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 0 });
      const writing = runtime.holder.begin({ ...binding });
      expect(Object.isFrozen(writing)).toBe(true);
      expect(writing.signal.aborted).toBe(false);
      writing.assertActive();
      writing.publish(input);
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
      const first = runtime.holder.read(binding);
      const second = runtime.holder.read({ ...binding });
      expect(Object.isFrozen(first)).toBe(true);
      expect(first.signal).toBe(writing.signal);
      expect(second.site).toBe(first.site);
      expect(first.site).not.toBe(input);
      const snapshot = first.site as ReturnType<typeof site>;
      expect(snapshot).toEqual(site());
      expect(Object.isFrozen(snapshot)).toBe(true);
      expect(Object.isFrozen(snapshot.enabledLocales)).toBe(true);
      expect(Object.isFrozen(snapshot.theme)).toBe(true);
      expect(Reflect.set(snapshot, 'displayName', 'Mutated')).toBe(false);
      expect(Reflect.set(snapshot.enabledLocales, '0', 'ar')).toBe(false);
      expect(Reflect.set(snapshot.theme, 'primary', '#ffffff')).toBe(false);
      input.displayName = 'Changed source';
      input.enabledLocales.push('ar');
      input.theme.primary = '#abcdef';
      expect(snapshot).toEqual(site());
      expect(JSON.stringify(snapshot)).not.toContain('private');
      first.assertActive();
    });
    expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
    owner.response.emit('finish');
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it.each(['begin', 'publish', 'refuse'] as const)(
    'closes a conflicting second %s operation',
    (operation) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      runtime.run(binding, owner.request, owner.response, () => {
        const writing = runtime.holder.begin(binding);
        if (operation === 'begin') expect(() => runtime.holder.begin(binding)).toThrow(unavailable);
        else {
          writing.publish(site());
          if (operation === 'publish') expect(() => writing.publish(site())).toThrow(unavailable);
          else expect(() => writing.refuse()).toThrow(unavailable);
        }
        expect(writing.signal.aborted).toBe(true);
        expect(() => writing.assertActive()).toThrow(completed);
      });
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expectReleased(owner);
    },
  );

  it.each(['pending', 'loading', 'refused'] as const)(
    'refuses reading in the %s phase',
    (phase) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      runtime.run(binding, owner.request, owner.response, () => {
        if (phase !== 'pending') {
          const writing = runtime.holder.begin(binding);
          if (phase === 'refused') writing.refuse();
        }
        expect(() => runtime.holder.read(binding)).toThrow(unavailable);
      });
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expectReleased(owner);
    },
  );

  it('makes refusal terminal without publishing and aborts transport once', () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    runtime.run(binding, owner.request, owner.response, () => {
      const writing = runtime.holder.begin(binding);
      const aborted = vi.fn();
      writing.signal.addEventListener('abort', aborted);
      writing.refuse();
      expect(writing.signal.aborted).toBe(true);
      expect(aborted).toHaveBeenCalledOnce();
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 0 });
      owner.response.emit('finish');
      expect(aborted).toHaveBeenCalledOnce();
      expect(() => writing.publish(site())).toThrow(completed);
    });
    expectReleased(owner);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it.each([
    { displayName: null },
    { enabledLocales: null },
    { enabledLocales: ['tr', 42] },
    { defaultLocale: null },
    { showPlatformAttribution: 'true' },
    { theme: undefined },
    { theme: [] },
    { theme: { primary: '#123456' } },
    { theme: { ...site().theme, primary: 42 } },
  ])('refuses malformed snapshot shape %j without retaining it', (change) => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    runtime.run(binding, owner.request, owner.response, () => {
      const writing = runtime.holder.begin(binding);
      expect(() => writing.publish({ ...site(), ...change })).toThrow(unavailable);
      expect(writing.signal.aborted).toBe(true);
      expect(() => runtime.holder.read(binding)).toThrow(completed);
    });
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it('masks throwing source accessors and releases the failed snapshot', () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    runtime.run(binding, owner.request, owner.response, () => {
      const writing = runtime.holder.begin(binding);
      const input = {
        ...site(),
        get displayName(): string {
          throw new Error('private value');
        },
      };
      expect(() => writing.publish(input)).toThrow(unavailable);
      expect(writing.signal.aborted).toBe(true);
    });
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it('bounds the retained closed DTO in UTF-8 bytes and discards large unknown fields', () => {
    const runtime = createPublicAdmissionRuntime();
    const overhead = Buffer.byteLength(JSON.stringify(site('')));
    const exactName = 'x'.repeat(MAX_PUBLIC_RESPONSE_BYTES - overhead);
    const permitted = lifetime();
    runtime.run(binding, permitted.request, permitted.response, () => {
      runtime.holder.begin(binding).publish(site(exactName));
      const snapshot = runtime.holder.read(binding).site;
      expect(Buffer.byteLength(JSON.stringify(snapshot))).toBe(MAX_PUBLIC_RESPONSE_BYTES);
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
    });
    permitted.response.emit('finish');
    expectReleased(permitted);

    for (const name of [exactName + 'x', exactName.slice(0, -1) + 'é']) {
      const refused = lifetime();
      runtime.run(binding, refused.request, refused.response, () => {
        const writing = runtime.holder.begin(binding);
        expect(() => writing.publish(site(name))).toThrow(unavailable);
        expect(writing.signal.aborted).toBe(true);
      });
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expectReleased(refused);
    }

    const additive = lifetime();
    runtime.run(binding, additive.request, additive.response, () => {
      runtime.holder.begin(binding).publish({ ...site(), unknownPrivateValue: exactName });
      expect(runtime.holder.read(binding).site).toEqual(site());
    });
    additive.response.emit('finish');
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(additive);
  });

  it.each([null, { ...site().theme, primary: 'malformed color' }])(
    'retains renderer theme fallback semantics: %j',
    (theme) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      runtime.run(binding, owner.request, owner.response, () => {
        runtime.holder.begin(binding).publish({ ...site(), theme });
        const snapshot = runtime.holder.read(binding).site as ReturnType<typeof site>;
        expect(snapshot.theme).toEqual(theme);
        expect(publicThemeCss(snapshot.theme)).toBeNull();
      });
      owner.response.emit('finish');
      expectReleased(owner);
    },
  );

  it.each([
    { host: 'other.example:3000' },
    { peer: '203.0.113.10' },
    { method: 'HEAD' },
    { target: '/en/courses?cursor=inert' },
  ])('closes a ready context when binding differs: %j', (change) => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    runtime.run(binding, owner.request, owner.response, () => {
      const writing = runtime.holder.begin(binding);
      writing.publish(site());
      expect(() => runtime.holder.read({ ...binding, ...change })).toThrow(unavailable);
      expect(writing.signal.aborted).toBe(true);
    });
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it.each([
    { host: 'INSTITUTION.example:3000' },
    { peer: '::ffff:203.0.113.9' },
    { method: 'POST' },
    { target: '//other.example/' },
  ])('refuses invalid native input before listeners or delegation: %j', (change) => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    const work = vi.fn();
    expect(() =>
      runtime.run({ ...binding, ...change }, owner.request, owner.response, work),
    ).toThrow(unavailable);
    expect(work).not.toHaveBeenCalled();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });
});

describe('native public admission lifetime and concurrent ownership', () => {
  it.each(['finish', 'close', 'aborted'] as const)(
    '%s closes once and removes only owned listeners',
    (event) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      const otherAborted = vi.fn();
      const otherFinished = vi.fn();
      const otherClosed = vi.fn();
      owner.request.on('aborted', otherAborted);
      owner.response.on('finish', otherFinished);
      owner.response.on('close', otherClosed);
      let reading!: PublicAdmissionRead;
      runtime.run(binding, owner.request, owner.response, () => {
        runtime.holder.begin(binding).publish(site());
        reading = runtime.holder.read(binding);
      });
      const aborted = vi.fn();
      reading.signal.addEventListener('abort', aborted);
      const emitter = event === 'aborted' ? owner.request : owner.response;
      emitter.emit(event);
      emitter.emit(event);
      expect(aborted).toHaveBeenCalledOnce();
      expect(reading.signal.aborted).toBe(true);
      expect(() => reading.assertActive()).toThrow(completed);
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expect(owner.request.listeners('aborted')).toEqual([otherAborted]);
      expect(owner.response.listeners('finish')).toEqual([otherFinished]);
      expect(owner.response.listeners('close')).toEqual([otherClosed]);
      expect(
        { finish: otherFinished, close: otherClosed, aborted: otherAborted }[event],
      ).toHaveBeenCalledTimes(2);
    },
  );

  it.each(['aborted', 'destroyed', 'writableEnded'] as const)(
    'refuses a native lifetime already %s',
    (state) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      if (state === 'aborted') owner.request.aborted = true;
      else Object.assign(owner.response, { [state]: true });
      const work = vi.fn();
      expect(() => runtime.run(binding, owner.request, owner.response, work)).toThrow(completed);
      expect(work).not.toHaveBeenCalled();
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expectReleased(owner);
    },
  );

  it('keeps async delegation active after its promise resolves until native completion', async () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    const gate = deferred();
    const pending = runtime.run(binding, owner.request, owner.response, async () => {
      runtime.holder.begin(binding).publish(site());
      await gate.promise;
      const reading = runtime.holder.read(binding);
      reading.assertActive();
      return reading.site;
    });
    expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
    gate.release();
    expect(await pending).toEqual(site());
    expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
    owner.response.emit('finish');
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it.each(['finish', 'close', 'aborted', 'shutdown'] as const)(
    'refuses late publication after %s',
    async (event) => {
      const runtime = createPublicAdmissionRuntime();
      const owner = lifetime();
      const gate = deferred();
      let writing!: PublicAdmissionWrite;
      const pending = runtime.run(binding, owner.request, owner.response, async () => {
        writing = runtime.holder.begin(binding);
        await gate.promise;
        expect(() => writing.publish(site())).toThrow(completed);
        expect(() => writing.assertActive()).toThrow(completed);
        expect(() => runtime.holder.read(binding)).toThrow(completed);
      });
      if (event === 'shutdown') runtime.shutdown();
      else (event === 'aborted' ? owner.request : owner.response).emit(event);
      expect(writing.signal.aborted).toBe(true);
      gate.release();
      await pending;
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
      expectReleased(owner);
    },
  );

  it('Public_Admission_Ends_With_The_Native_Request', async () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    const head = { ...binding, method: 'HEAD' };
    const gate = deferred();
    const pending = runtime.run(head, owner.request, owner.response, async () => {
      runtime.holder.begin(head).publish(site());
      const reading = runtime.holder.read(head);
      await gate.promise;
      expect(() => reading.assertActive()).toThrow(completed);
      expect(() => runtime.holder.read(head)).toThrow(completed);
      expect(reading.signal.aborted).toBe(true);
    });
    owner.response.emit('finish');
    gate.release();
    await pending;
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it('isolates eight overlapped identical bindings with distinct native stores', async () => {
    const runtime = createPublicAdmissionRuntime();
    const gate = deferred();
    const owners = Array.from({ length: 8 }, lifetime);
    const snapshots = new Set<unknown>();
    const signals = new Set<AbortSignal>();
    const pending = owners.map((owner, index) =>
      runtime.run(binding, owner.request, owner.response, async () => {
        const writing = runtime.holder.begin(binding);
        signals.add(writing.signal);
        writing.publish(site(`Institution ${index}`));
        const reading = runtime.holder.read(binding);
        snapshots.add(reading.site);
        await gate.promise;
        reading.assertActive();
        expect(runtime.holder.read(binding).site).toBe(reading.site);
        expect(reading.site).toEqual(site(`Institution ${index}`));
        owner.response.emit('finish');
      }),
    );
    expect(runtime.counts()).toEqual({ active: 8, snapshots: 8 });
    expect(snapshots.size).toBe(8);
    expect(signals.size).toBe(8);
    gate.release();
    await Promise.all(pending);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    owners.forEach(expectReleased);
  });

  it.each(['write', 'read'] as const)(
    'rejects escaped %s handles inside an identical neighbor',
    (kind) => {
      const runtime = createPublicAdmissionRuntime();
      const first = lifetime();
      const second = lifetime();
      let escaped!: PublicAdmissionWrite | PublicAdmissionRead;
      runtime.run(binding, first.request, first.response, () => {
        const writing = runtime.holder.begin(binding);
        if (kind === 'read') {
          writing.publish(site('First'));
          escaped = runtime.holder.read(binding);
        } else escaped = writing;
      });
      runtime.run(binding, second.request, second.response, () => {
        const writing = runtime.holder.begin(binding);
        expect(() => escaped.assertActive()).toThrow(unavailable);
        expect(escaped.signal.aborted).toBe(true);
        expect(writing.signal.aborted).toBe(false);
        writing.publish(site('Second'));
        expect(runtime.holder.read(binding).site).toEqual(site('Second'));
        expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
      });
      expectReleased(first);
      second.response.emit('finish');
      expectReleased(second);
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    },
  );

  it('closes synchronous delegation faults without replacing the original error', () => {
    const runtime = createPublicAdmissionRuntime();
    const owner = lifetime();
    const fault = new Error('owned test failure');
    expect(() =>
      runtime.run(binding, owner.request, owner.response, () => {
        runtime.holder.begin(binding).publish(site());
        throw fault;
      }),
    ).toThrow(fault);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(owner);
  });

  it('shuts down all stores once and refuses new native requests', () => {
    const runtime = createPublicAdmissionRuntime();
    const ready = lifetime();
    const loading = lifetime();
    let first!: PublicAdmissionRead;
    let second!: PublicAdmissionWrite;
    runtime.run(binding, ready.request, ready.response, () => {
      runtime.holder.begin(binding).publish(site());
      first = runtime.holder.read(binding);
    });
    runtime.run(binding, loading.request, loading.response, () => {
      second = runtime.holder.begin(binding);
    });
    expect(runtime.counts()).toEqual({ active: 2, snapshots: 1 });
    runtime.shutdown();
    runtime.shutdown();
    expect(first.signal.aborted).toBe(true);
    expect(second.signal.aborted).toBe(true);
    expect(() => first.assertActive()).toThrow(completed);
    expect(() => second.publish(site())).toThrow(completed);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    expectReleased(ready);
    expectReleased(loading);
    const later = lifetime();
    const work = vi.fn();
    expect(() => runtime.run(binding, later.request, later.response, work)).toThrow(unavailable);
    expect(work).not.toHaveBeenCalled();
    expectReleased(later);
  });
});
