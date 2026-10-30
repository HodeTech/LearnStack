// @vitest-environment node
import { createServer, request } from 'node:http';
import type { ServerResponse } from 'node:http';

import { describe, expect, it, vi } from 'vitest';

import {
  heldWork,
  snapshotBinding,
  snapshotRequest,
  SnapshotWire,
} from '../../test/page-response-fixture';
import { createPublicAdmissionRuntime } from '../public-admission-runtime';
import type { NativeEntrySnapshot, PageSite } from './page-contract';
import type { NativePageLease, NativeSnapshotTicket } from './page-snapshot-contract';
import { createBffRequestRuntime } from './request-runtime';

const site: PageSite = Object.freeze({
  displayName: 'School',
  defaultLocale: 'en',
  enabledLocales: ['en'],
  theme: null,
  showPlatformAttribution: true,
});
const entry: NativeEntrySnapshot = {
  site,
  locale: 'en',
  view: { kind: 'entry', entry: null, cursor: null },
};
function fixture() {
  const admission = createPublicAdmissionRuntime(true);
  const runtime = createBffRequestRuntime(admission.holder);
  const run = <T>(wire: SnapshotWire, action: () => T, binding = snapshotBinding) => {
    const request = snapshotRequest();
    return admission.run(binding, request, wire.native, () => {
      admission.holder.begin(binding).publish(site);
      return runtime.run(binding, request, wire.native, action);
    });
  };
  return { runtime, run };
}

describe('actual native snapshot request lifetime', () => {
  it.each([false, true])(
    'refuses destroyed-socket writes before close without abandoning an earlier callback (held=%s)',
    async (holdIssuedCallback) => {
      const admission = createPublicAdmissionRuntime(true);
      const runtime = createBffRequestRuntime(admission.holder);
      const rejectedWrite = vi.fn();
      const rejectedEnd = vi.fn();
      const issued = vi.fn();
      let releaseIssued: (() => void) | undefined;
      let response!: ServerResponse;
      let closed = false;
      let served!: () => void;
      let failed!: (error: unknown) => void;
      const handled = new Promise<void>((resolve, reject) => {
        served = resolve;
        failed = reject;
      });
      const server = createServer((incoming, outgoing) => {
        response = outgoing;
        outgoing.once('close', () => {
          closed = true;
        });
        if (holdIssuedCallback) {
          const write = outgoing.write;
          outgoing.write = function (this: ServerResponse, ...args: unknown[]) {
            const callback = args.pop();
            if (typeof callback !== 'function') throw new Error('Expected tracked callback');
            args.push((...values: unknown[]) => {
              releaseIssued = () => {
                releaseIssued = undefined;
                Reflect.apply(callback, this, values);
              };
            });
            return Reflect.apply(write, this, args) as boolean;
          } as ServerResponse['write'];
        }
        try {
          admission.run(snapshotBinding, incoming, outgoing, () => {
            admission.holder.begin(snapshotBinding).publish(site);
            runtime.run(snapshotBinding, incoming, outgoing, () => {
              const lease = runtime.preparation.reserve(
                runtime.preparation.current(snapshotBinding),
                {},
              );
              lease.pin(() => true, 30_000);
              lease.publishEntry(entry);
              if (holdIssuedCallback) outgoing.write('admitted', issued);
              outgoing.socket!.destroy();
              expect(closed).toBe(false);
              expect(outgoing.write('withheld', rejectedWrite)).toBe(false);
              expect(outgoing.end('withheld', rejectedEnd)).toBe(outgoing);
            });
          });
          served();
        } catch (error) {
          failed(error);
        }
      });
      try {
        await new Promise<void>((resolve, reject) => {
          server.once('error', reject);
          server.listen(0, '127.0.0.1', resolve);
        });
        const address = server.address();
        if (!address || typeof address === 'string') throw new Error('Expected local address');
        const completed = new Promise<void>((resolve) => {
          const call = request(
            { host: '127.0.0.1', port: address.port, path: '/studio', agent: false },
            (received) => {
              received.resume();
              received.once('end', resolve);
              received.once('aborted', resolve);
              received.once('error', () => resolve());
            },
          );
          call.once('error', () => resolve());
          call.end();
        });
        await handled;
        await completed;
        await vi.waitFor(() => {
          expect(closed).toBe(true);
          expect(response.destroyed).toBe(true);
          expect(rejectedWrite).toHaveBeenCalledExactlyOnceWith(expect.any(Error));
          expect(rejectedEnd).toHaveBeenCalledExactlyOnceWith(expect.any(Error));
        });
        if (holdIssuedCallback) {
          await vi.waitFor(() => expect(releaseIssued).toBeTypeOf('function'));
          expect(issued).not.toHaveBeenCalled();
          expect(runtime.counts()).toBe(1);
          expect(runtime.snapshotCounts().active).toBe(1);
          releaseIssued!();
          expect(issued).toHaveBeenCalledOnce();
        }
        await vi.waitFor(() => {
          expect(runtime.counts()).toBe(0);
          expect(runtime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
        });
        await runtime.stop();
      } finally {
        releaseIssued?.();
        response?.destroy();
        admission.shutdown();
        // A dirty implementation may strand a callback forever; do not let that
        // expected assertion failure turn into an unbounded test cleanup wait.
        void runtime.stop();
        await new Promise<void>((resolve) => server.close(() => resolve()));
      }
    },
  );

  it('rejects copied tickets and an authentic ticket borrowed by an equal-binding neighbor', async () => {
    const { runtime, run } = fixture();
    const first = new SnapshotWire();
    const second = new SnapshotWire();
    let ticket!: NativeSnapshotTicket;
    run(first, () => {
      ticket = runtime.preparation.current(snapshotBinding);
      expect(Object.isFrozen(ticket.binding)).toBe(true);
      expect(() => runtime.preparation.reserve({ ...ticket }, {})).toThrow();
      run(second, () => {
        expect(() => runtime.preparation.reserve(ticket, {})).toThrow();
        second.native.end();
      });
      const lease = runtime.preparation.reserve(ticket, {});
      lease.pin(() => true, 30000);
      lease.publishEntry(entry);
      expect(() => runtime.preparation.reserve(ticket, {})).toThrow();
      first.native.end();
    });
    first.drainCallbacks();
    second.drainCallbacks();
    await runtime.stop();
    expect(runtime.snapshotCounts().active).toBe(0);
  });

  it('holds credit after finish through actual handler, nested waitUntil, API and native write cleanup', async () => {
    const { runtime, run } = fixture();
    const wire = new SnapshotWire();
    const handler = heldWork();
    const framework = heldWork();
    const nested = heldWork();
    const api = heldWork();
    let lease!: NativePageLease;
    const running = run(wire, async () => {
      lease = runtime.preparation.reserve(runtime.preparation.current(snapshotBinding), {});
      lease.track(api.promise);
      lease.pin(() => true, 30000);
      lease.publishEntry(entry);
      const context = runtime.framework.get()!;
      context.waitUntil(
        framework.promise.then(() => {
          context.waitUntil(nested.promise);
        }),
      );
      wire.native.write('prepared');
      wire.native.end();
      await handler.promise;
    });
    let cleaned = false;
    void lease.cleaned.then(() => {
      cleaned = true;
    });
    const stopping = runtime.stop();
    handler.finish();
    await running;
    api.finish();
    framework.finish();
    await Promise.resolve();
    await Promise.resolve();
    expect(cleaned).toBe(false);
    expect(runtime.snapshotCounts().active).toBe(1);
    nested.finish();
    await Promise.resolve();
    await Promise.resolve();
    expect(cleaned).toBe(false);
    wire.drainCallbacks();
    await stopping;
    expect(cleaned).toBe(true);
    expect(runtime.counts()).toBe(0);
    expect(runtime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
  });

  it('does not release a non-page response merely because its handler or stop settles first', async () => {
    const { runtime, run } = fixture();
    const wire = new SnapshotWire();
    const handler = heldWork();
    const running = run(wire, async () => {
      runtime.holder.publish(snapshotBinding, null);
      wire.native.write('ordinary');
      await handler.promise;
    });
    let stopped = false;
    const stopping = runtime.stop().then(() => {
      stopped = true;
    });
    handler.finish();
    await running;
    await Promise.resolve();
    expect(stopped).toBe(false);
    wire.drainCallbacks();
    await stopping;
    expect(stopped).toBe(true);
  });
});
