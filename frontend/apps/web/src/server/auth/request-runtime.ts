import { AsyncLocalStorage } from 'node:async_hooks';
import type { EventEmitter } from 'node:events';
import type { IncomingHttpHeaders } from 'node:http';

import { canonicalAddress, normalizeHost, validTarget } from '../ingress.js';
import type { IngressContext } from '../ingress.js';
import { BFF_POST_PATHS, bffActivity } from './http-input.js';
import type { SessionProjection } from './session-contract.js';
import { isBffOpaque } from './session-store.js';

export type BffRequestRead = Readonly<{
  session: SessionProjection | null;
  assertActive: () => void;
}>;
export type BffRequestHolder = Readonly<{
  version: 1;
  activity: (binding: IngressContext) => 'read' | 'passive';
  flight: (binding: IngressContext) => boolean;
  publish: (binding: IngressContext, session: SessionProjection | null) => void;
  read: (binding: IngressContext) => BffRequestRead;
}>;
type Store = {
  binding: IngressContext;
  activity: 'read' | 'passive';
  flight: boolean;
  phase: 'pending' | 'ready' | 'closed';
  session: SessionProjection | null;
  close: () => void;
};
const unavailable = (): never => {
  throw new Error('BFF request context unavailable');
};
const same = (left: IngressContext, right: IngressContext) =>
  left.host === right.host &&
  left.peer === right.peer &&
  left.method === right.method &&
  left.target === right.target;
function snapshot(session: SessionProjection | null): SessionProjection | null {
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

/** Separate native ALS: the response owner publishes once; RSC only reads a safe snapshot. */
export function createBffRequestRuntime() {
  const storage = new AsyncLocalStorage<Store>();
  const active = new Set<Store>();
  let stopped = false;
  function current(binding: IngressContext, phase: 'pending' | 'ready') {
    const store = storage.getStore();
    if (!store || store.phase !== phase || !same(store.binding, binding)) return unavailable();
    return store;
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
      const store = current(binding, 'pending');
      try {
        store.session = snapshot(session);
        store.phase = 'ready';
      } catch {
        store.close();
        unavailable();
      }
    },
    read(binding) {
      const store = current(binding, 'ready');
      return Object.freeze({
        session: store.session,
        assertActive() {
          if (current(binding, 'ready') !== store) unavailable();
        },
      });
    },
  });
  return Object.freeze({
    holder,
    run<T>(
      binding: IngressContext,
      request: Pick<EventEmitter, 'once' | 'removeListener'> & {
        readonly aborted: boolean;
        readonly headers: IncomingHttpHeaders;
      },
      response: Pick<EventEmitter, 'once' | 'removeListener'> & {
        readonly destroyed: boolean;
        readonly writableEnded: boolean;
      },
      work: () => T,
    ): T {
      if (
        stopped ||
        normalizeHost(binding.host) !== binding.host ||
        canonicalAddress(binding.peer) !== binding.peer ||
        !validTarget(binding.target) ||
        !(
          ['GET', 'HEAD'].includes(binding.method) ||
          (binding.method === 'POST' && BFF_POST_PATHS.includes(binding.target))
        )
      )
        return unavailable();
      // Capture protocol intent before Next strips Flight headers. Retain only
      // the closed scalar, never the native request or its header collection.
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
      const store: Store = {
        activity: bffActivity(binding.method, headers),
        flight: request.headers.rsc === '1',
        binding: Object.freeze({ ...binding }),
        phase: 'pending',
        session: null,
        close: () => {},
      };
      const close = () => {
        if (store.phase === 'closed') return;
        store.phase = 'closed';
        store.session = null;
        active.delete(store);
        request.removeListener('aborted', close);
        response.removeListener('finish', close);
        response.removeListener('close', close);
        store.close = () => {};
      };
      store.close = close;
      active.add(store);
      request.once('aborted', close);
      response.once('finish', close);
      response.once('close', close);
      if (request.aborted || response.destroyed || response.writableEnded) close();
      return storage.run(store, () => {
        if (store.phase === 'closed') return unavailable();
        try {
          return work();
        } catch (error) {
          close();
          throw error;
        }
      });
    },
    stop() {
      stopped = true;
      for (const store of active) store.close();
    },
    counts() {
      return active.size;
    },
  });
}
export function installBffRequestRuntime() {
  const key = Symbol.for('learnstack.bff-request.v1');
  if (Object.getOwnPropertyDescriptor(globalThis, key)) return unavailable();
  const runtime = createBffRequestRuntime();
  Object.defineProperty(globalThis, key, {
    value: runtime.holder,
    configurable: false,
    enumerable: false,
    writable: false,
  });
  return runtime;
}
