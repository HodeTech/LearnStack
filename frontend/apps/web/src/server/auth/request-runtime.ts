import { AsyncLocalStorage } from 'node:async_hooks';
import type { EventEmitter } from 'node:events';
import type { IncomingHttpHeaders, ServerResponse } from 'node:http';

import { canonicalAddress, normalizeHost, validTarget } from '../ingress.js';
import type { IngressContext } from '../ingress.js';
import type { PublicAdmissionHolder, PublicAdmissionRead } from '../public-admission-runtime.js';
import { BFF_POST_PATHS, bffActivity } from './http-input.js';
import { bffActionDocumentTarget } from './http-paths.js';
import type { NativePageSnapshot, PageSite } from './page-contract.js';
import { protectPageResponse } from './page-response.js';
import type { NativePagePreparation, NativeSnapshotTicket } from './page-snapshot-contract.js';
import { createPageSnapshotPool, SnapshotAdmissionError } from './page-snapshot-pool.js';
import type { ResponsePageLease } from './page-snapshot-pool.js';
import type { SessionProjection } from './session-contract.js';
import { isBffOpaque } from './session-store.js';

export type BffRequestRead = Readonly<{
  session: SessionProjection | null;
  page: NativePageSnapshot | null;
  flight: boolean;
  assertActive: () => void;
}>;
export type BffRequestHolder = Readonly<{
  version: 1;
  activity: (binding: IngressContext) => 'read' | 'passive';
  flight: (binding: IngressContext) => boolean;
  publish: (binding: IngressContext, session: SessionProjection | null) => void;
  read: (binding: IngressContext) => BffRequestRead;
}>;
type RequestLifetime = Pick<EventEmitter, 'once' | 'removeListener'> & {
  readonly aborted: boolean;
  readonly headers: IncomingHttpHeaders;
};
type ResponseLifetime = Pick<EventEmitter, 'once' | 'removeListener'> & {
  readonly destroyed: boolean;
  readonly writableEnded: boolean;
} & Partial<
    Pick<ServerResponse, 'writeHead' | 'flushHeaders' | 'write' | 'end' | 'destroy' | 'headersSent'>
  >;
type Frame = {
  binding: IngressContext;
  activity: 'read' | 'passive';
  flight: boolean;
  prefetch: boolean;
  phase: 'pending' | 'ready' | 'closed';
  session: SessionProjection | null;
  page: NativePageSnapshot | null;
  controller: AbortController;
  lease: ResponsePageLease | undefined;
  outputStarted: boolean;
  guarded: boolean;
  cleaned: Promise<void>;
  invalidate: () => void;
  assertCurrent: () => void;
  waitUntil: (promise: Promise<unknown>) => void;
  next: Readonly<{ waitUntil: (promise: Promise<unknown>) => void }>;
};
const unavailable = (): never => {
  throw new Error('BFF request context unavailable');
};
const same = (left: IngressContext, right: IngressContext) =>
  left.host === right.host &&
  left.peer === right.peer &&
  left.method === right.method &&
  left.target === right.target;
function sessionSnapshot(session: SessionProjection | null): SessionProjection | null {
  if (session === null) return null;
  if (
    session.state !== 'authenticated' ||
    typeof session.displayName !== 'string' ||
    session.displayName.length > 1024 ||
    !Number.isFinite(session.expiresAt) ||
    typeof session.limitedByAbsolute !== 'boolean' ||
    !isBffOpaque(session.csrfToken)
  )
    return unavailable();
  return Object.freeze({
    state: 'authenticated',
    displayName: session.displayName,
    expiresAt: session.expiresAt,
    limitedByAbsolute: session.limitedByAbsolute,
    csrfToken: session.csrfToken,
  });
}

/** Actual native ALS and response owner. The global facade exposes no preparation authority. */
export function createBffRequestRuntime(admission?: Pick<PublicAdmissionHolder, 'read'>) {
  const storage = new AsyncLocalStorage<Frame>();
  const active = new Set<Frame>();
  const tickets = new WeakMap<
    NativeSnapshotTicket,
    Readonly<{ frame: Frame; admission: PublicAdmissionRead }>
  >();
  const pool = createPageSnapshotPool();
  let stopped = false;
  function current(binding: IngressContext, phase: 'pending' | 'ready') {
    const frame = storage.getStore();
    if (!frame || frame.phase !== phase || !same(frame.binding, binding)) return unavailable();
    frame.assertCurrent();
    return frame;
  }
  const holder: BffRequestHolder = Object.freeze({
    version: 1,
    activity(binding) {
      return current(binding, 'pending').activity;
    },
    flight(binding) {
      return current(binding, 'pending').flight;
    },
    publish(binding, session) {
      const frame = current(binding, 'pending');
      try {
        const value = sessionSnapshot(session);
        if (
          frame.page &&
          (!value ||
            frame.page.session.state !== value.state ||
            frame.page.session.displayName !== value.displayName ||
            frame.page.session.expiresAt !== value.expiresAt ||
            frame.page.session.limitedByAbsolute !== value.limitedByAbsolute ||
            frame.page.session.csrfToken !== value.csrfToken)
        )
          return unavailable();
        if (frame.lease) frame.lease.assertOutput();
        frame.session = value;
        frame.phase = 'ready';
      } catch {
        frame.invalidate();
        unavailable();
      }
    },
    read(binding) {
      const frame = current(binding, 'ready');
      if (frame.lease) frame.lease.assertOutput();
      return Object.freeze({
        session: frame.session,
        page: frame.page,
        flight: frame.flight,
        assertActive() {
          if (current(binding, 'ready') !== frame) unavailable();
          if (frame.lease) frame.lease.assertOutput();
        },
      });
    },
  });
  const preparation: NativePagePreparation = Object.freeze({
    current(binding) {
      const frame = current(binding, 'pending');
      const pageMethod =
        frame.binding.method === 'GET' ||
        (frame.binding.method === 'POST' &&
          (frame.binding.target === '/api/auth/entry/create' ||
            bffActionDocumentTarget(frame.binding.target)));
      if (!admission || !pageMethod || frame.outputStarted || frame.lease) return unavailable();
      const publicRead = admission.read(binding);
      publicRead.assertActive();
      const ticket: NativeSnapshotTicket = Object.freeze({
        binding: frame.binding,
        site: publicRead.site as PageSite,
        signal: AbortSignal.any([publicRead.signal, frame.controller.signal]),
        activity: frame.activity,
        flight: frame.flight,
        prefetch: frame.prefetch,
      });
      tickets.set(ticket, { frame, admission: publicRead });
      return ticket;
    },
    reserve(ticket, actualOwner) {
      const owned = tickets.get(ticket);
      if (
        !owned ||
        storage.getStore() !== owned.frame ||
        owned.frame.phase !== 'pending' ||
        owned.frame.lease ||
        owned.frame.outputStarted ||
        !owned.frame.guarded
      )
        throw new SnapshotAdmissionError('unavailable');
      owned.frame.assertCurrent();
      owned.admission.assertActive();
      const frame = owned.frame;
      const result = pool.reserve(actualOwner, {
        signal: ticket.signal,
        cleaned: frame.cleaned,
        assertCurrent: frame.assertCurrent,
        invalidate: frame.invalidate,
        publishPage(value) {
          frame.assertCurrent();
          frame.page = value;
        },
      });
      frame.lease = result;
      tickets.delete(ticket);
      return result.lease;
    },
  });
  const framework = Object.freeze({
    get() {
      return storage.getStore()?.next;
    },
  });
  return Object.freeze({
    holder,
    preparation,
    framework,
    run<T>(
      binding: IngressContext,
      request: RequestLifetime,
      response: ResponseLifetime,
      work: () => T,
    ): T {
      if (
        stopped ||
        request.aborted ||
        response.destroyed ||
        response.writableEnded ||
        normalizeHost(binding.host) !== binding.host ||
        canonicalAddress(binding.peer) !== binding.peer ||
        !validTarget(binding.target) ||
        !(
          ['GET', 'HEAD'].includes(binding.method) ||
          (binding.method === 'POST' && BFF_POST_PATHS.includes(binding.target))
        )
      )
        return unavailable();
      const headers = new Headers();
      for (const name of [
        'rsc',
        'next-router-prefetch',
        'purpose',
        'sec-purpose',
        'sec-fetch-dest',
        'accept',
      ]) {
        const value = request.headers[name];
        if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(',') : value);
      }
      let handlerPending = true;
      let terminal: boolean = response.destroyed || response.writableEnded;
      let released = false;
      let writes = 0;
      let generation = 0;
      let scheduled = false;
      const waiting = new Set<Promise<unknown>>();
      let resolveCleaned!: () => void;
      const cleaned = new Promise<void>((resolve) => {
        resolveCleaned = resolve;
      });
      let fence: ReturnType<typeof protectPageResponse> | undefined;
      const frame: Frame = {
        binding: Object.freeze({ ...binding }),
        activity: bffActivity(binding.method, headers),
        flight: request.headers.rsc === '1',
        prefetch:
          headers.has('next-router-prefetch') ||
          headers.get('purpose') === 'prefetch' ||
          (headers
            .get('sec-purpose')
            ?.toLowerCase()
            .split(/[;\s]+/)
            .includes('prefetch') ??
            false),
        phase: 'pending',
        session: null,
        page: null,
        controller: new AbortController(),
        lease: undefined,
        outputStarted: response.headersSent === true,
        guarded: false,
        cleaned,
        invalidate: () => {},
        assertCurrent: () => {},
        waitUntil: () => {},
        next: { waitUntil: () => {} },
      };
      const release = () => {
        if (released || scheduled || handlerPending || !terminal || writes || waiting.size) return;
        scheduled = true;
        const version = generation;
        queueMicrotask(() =>
          queueMicrotask(() => {
            scheduled = false;
            if (released || handlerPending || !terminal || writes || waiting.size) return;
            if (version !== generation) {
              release();
              return;
            }
            released = true;
            active.delete(frame);
            request.removeListener('aborted', aborted);
            response.removeListener('finish', ended);
            response.removeListener('close', ended);
            resolveCleaned();
          }),
        );
      };
      frame.invalidate = () => {
        if (frame.phase !== 'closed') {
          frame.phase = 'closed';
          frame.session = null;
          frame.page = null;
          frame.controller.abort('BFF request context closed');
        }
        if (frame.lease) fence?.refuse();
        else if (!terminal) response.destroy?.();
        release();
      };
      frame.assertCurrent = () => {
        if (storage.getStore() !== frame || frame.phase === 'closed' || released)
          return unavailable();
      };
      frame.waitUntil = (promise) => {
        // Existing cleanup may register more actual work after cancellation;
        // released frames can never regain a cleanup capability.
        if (
          storage.getStore() !== frame ||
          released ||
          !promise ||
          typeof promise.then !== 'function'
        )
          return unavailable();
        const actual = Promise.resolve(promise);
        waiting.add(actual);
        ++generation;
        const done = () => {
          waiting.delete(actual);
          ++generation;
          release();
        };
        void actual.then(done, done);
      };
      frame.next = Object.freeze({ waitUntil: frame.waitUntil });
      const ended = () => {
        terminal = true;
        frame.invalidate();
        release();
      };
      const aborted = () => {
        frame.invalidate();
        response.destroy?.();
        release();
      };
      if (
        typeof response.writeHead === 'function' &&
        typeof response.flushHeaders === 'function' &&
        typeof response.write === 'function' &&
        typeof response.end === 'function' &&
        typeof response.destroy === 'function'
      ) {
        frame.guarded = true;
        fence = protectPageResponse(
          response as ServerResponse,
          () => {
            if (frame.lease) frame.lease.assertOutput();
            else if (frame.phase === 'closed') unavailable();
            if (frame.page && frame.phase !== 'ready') unavailable();
          },
          () => {
            frame.outputStarted = true;
          },
          () => {
            ++writes;
            return () => {
              --writes;
              release();
            };
          },
        );
      }
      active.add(frame);
      request.once('aborted', aborted);
      response.once('finish', ended);
      response.once('close', ended);
      if (request.aborted || terminal) frame.invalidate();
      return storage.run(frame, () => {
        const settled = () => {
          handlerPending = false;
          release();
        };
        try {
          frame.assertCurrent();
          const result = work();
          if (
            result !== null &&
            (typeof result === 'object' || typeof result === 'function') &&
            'then' in result &&
            typeof result.then === 'function'
          )
            void Promise.resolve(result).then(settled, () => {
              frame.invalidate();
              settled();
            });
          else settled();
          return result;
        } catch (error) {
          frame.invalidate();
          settled();
          throw error;
        }
      });
    },
    async stop() {
      stopped = true;
      for (const frame of active) frame.invalidate();
      const pooled = pool.stop();
      while (active.size) await Promise.all([...active].map((frame) => frame.cleaned));
      await pooled;
    },
    counts() {
      return active.size;
    },
    snapshotCounts: pool.counts,
  });
}

export function installBffRequestRuntime(admission?: Pick<PublicAdmissionHolder, 'read'>) {
  const key = Symbol.for('learnstack.bff-request.v1');
  const frameworkKey = Symbol.for('@next/request-context');
  if (
    Object.getOwnPropertyDescriptor(globalThis, key) ||
    Object.getOwnPropertyDescriptor(globalThis, frameworkKey)
  )
    return unavailable();
  const runtime = createBffRequestRuntime(admission);
  for (const [symbol, value] of [
    [key, runtime.holder],
    [frameworkKey, runtime.framework],
  ] as const)
    Object.defineProperty(globalThis, symbol, {
      value,
      configurable: false,
      enumerable: false,
      writable: false,
    });
  return runtime;
}
