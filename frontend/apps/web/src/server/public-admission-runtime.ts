import { AsyncLocalStorage } from 'node:async_hooks';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { canonicalAddress, normalizeHost, validTarget } from './ingress.js';
import type { IngressContext } from './ingress.js';
import { MAX_PUBLIC_RESPONSE_BYTES } from './public-api-limits.js';

/** The native owner is compiled separately; bundled consumers only import its types. */
export interface PublicAdmissionRead {
  readonly site: unknown;
  readonly signal: AbortSignal;
  readonly assertActive: () => void;
}

export interface PublicAdmissionWrite {
  readonly signal: AbortSignal;
  readonly assertActive: () => void;
  readonly publish: (site: unknown) => void;
  readonly refuse: () => void;
}

export interface PublicAdmissionHolder {
  readonly version: 1;
  readonly begin: (binding: IngressContext) => PublicAdmissionWrite;
  readonly read: (binding: IngressContext) => PublicAdmissionRead;
}

type Phase = 'pending' | 'loading' | 'ready' | 'refused' | 'closed';
type Store = {
  readonly binding: IngressContext;
  readonly controller: AbortController;
  phase: Phase;
  site: unknown;
  close: () => void;
};
type RequestLifetime = Pick<IncomingMessage, 'aborted' | 'once' | 'removeListener'>;
type ResponseLifetime = Pick<
  ServerResponse,
  'destroyed' | 'writableEnded' | 'once' | 'removeListener'
>;

const release = () => {};
const completionReason = 'Public admission request completed';

function invariant(): never {
  // Only fixed diagnostics: never include a binding, DTO, envelope or provider value.
  throw new Error('Public admission context unavailable');
}

function completed(): never {
  throw new Error('Public admission request completed');
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Copy the closed public DTO, discarding additive wire members and all source ownership. */
function snapshot(value: unknown): unknown {
  if (
    !record(value) ||
    typeof value.displayName !== 'string' ||
    !Array.isArray(value.enabledLocales) ||
    !value.enabledLocales.every((locale): locale is string => typeof locale === 'string') ||
    typeof value.defaultLocale !== 'string' ||
    typeof value.showPlatformAttribution !== 'boolean'
  )
    return invariant();
  let theme: Readonly<Record<string, string>> | null = null;
  const sourceTheme = value.theme;
  if (sourceTheme !== null) {
    if (
      !record(sourceTheme) ||
      !['primary', 'background', 'foreground', 'muted'].every(
        (key) => typeof sourceTheme[key] === 'string',
      )
    )
      return invariant();
    // CSS grammar stays with the atomic renderer; a malformed color retains its fallback.
    theme = Object.freeze({
      primary: sourceTheme.primary as string,
      background: sourceTheme.background as string,
      foreground: sourceTheme.foreground as string,
      muted: sourceTheme.muted as string,
    });
  }
  const projected = {
    displayName: value.displayName,
    enabledLocales: Object.freeze([...value.enabledLocales]),
    defaultLocale: value.defaultLocale,
    showPlatformAttribution: value.showPlatformAttribution,
    theme,
  };
  if (Buffer.byteLength(JSON.stringify(projected)) > MAX_PUBLIC_RESPONSE_BYTES) return invariant();
  return Object.freeze(projected);
}

/** Native-only construction. The active set owns shutdown, never request lookup. */
export function createPublicAdmissionRuntime() {
  const storage = new AsyncLocalStorage<Store>();
  const active = new Set<Store>();
  let stopped = false;

  function assertStore(store: Store): void {
    if (store.phase === 'closed') completed();
    if (storage.getStore() !== store) {
      store.close();
      invariant();
    }
  }

  function current(binding: IngressContext): Store {
    const store = storage.getStore();
    if (!store) return invariant();
    assertStore(store);
    if (
      store.binding.host !== binding.host ||
      store.binding.peer !== binding.peer ||
      store.binding.method !== binding.method ||
      store.binding.target !== binding.target
    ) {
      store.close();
      return invariant();
    }
    return store;
  }

  function requirePhase(store: Store, phase: Phase): void {
    assertStore(store);
    if (store.phase !== phase) {
      store.close();
      invariant();
    }
  }

  const holder: PublicAdmissionHolder = Object.freeze({
    version: 1,
    begin(binding: IngressContext): PublicAdmissionWrite {
      const store = current(binding);
      requirePhase(store, 'pending');
      store.phase = 'loading';
      return Object.freeze({
        signal: store.controller.signal,
        assertActive: () => requirePhase(store, 'loading'),
        publish(value: unknown) {
          requirePhase(store, 'loading');
          try {
            store.site = snapshot(value);
            store.phase = 'ready';
          } catch {
            store.close();
            invariant();
          }
        },
        refuse() {
          requirePhase(store, 'loading');
          store.phase = 'refused';
          store.controller.abort(completionReason);
        },
      });
    },
    read(binding: IngressContext): PublicAdmissionRead {
      const store = current(binding);
      requirePhase(store, 'ready');
      return Object.freeze({
        site: store.site,
        signal: store.controller.signal,
        assertActive: () => requirePhase(store, 'ready'),
      });
    },
  });

  return Object.freeze({
    holder,
    run<T>(
      binding: IngressContext,
      request: RequestLifetime,
      response: ResponseLifetime,
      work: () => T,
    ): T {
      if (
        stopped ||
        normalizeHost(binding.host) !== binding.host ||
        canonicalAddress(binding.peer) !== binding.peer ||
        (binding.method !== 'GET' && binding.method !== 'HEAD') ||
        !validTarget(binding.target)
      )
        return invariant();
      const store: Store = {
        binding: Object.freeze({ ...binding }),
        controller: new AbortController(),
        phase: 'pending',
        site: undefined,
        close: release,
      };
      const close = () => {
        if (store.phase === 'closed') return;
        store.phase = 'closed';
        store.site = undefined;
        active.delete(store);
        request.removeListener('aborted', close);
        response.removeListener('finish', close);
        response.removeListener('close', close);
        // Escaped handles and late ALS work must not retain the native objects.
        store.close = release;
        // A fresh default DOMException can retain this stack and its request objects.
        store.controller.abort(completionReason);
      };
      store.close = close;
      active.add(store);
      request.once('aborted', close);
      response.once('finish', close);
      response.once('close', close);
      if (request.aborted || response.destroyed || response.writableEnded) close();
      return storage.run(store, () => {
        assertStore(store);
        try {
          return work();
        } catch (error) {
          close();
          throw error;
        }
      });
    },
    shutdown() {
      stopped = true;
      for (const store of active) store.close();
    },
    counts() {
      return Object.freeze({
        active: active.size,
        snapshots: [...active].filter((store) => store.site !== undefined).length,
      });
    },
  });
}

/** Install once before Next loads its separately bundled middleware and RSC facades. */
export function installPublicAdmissionRuntime() {
  const key = Symbol.for('learnstack.public-admission.v1');
  if (Object.getOwnPropertyDescriptor(globalThis, key)) return invariant();
  const runtime = createPublicAdmissionRuntime();
  Object.defineProperty(globalThis, key, {
    value: runtime.holder,
    enumerable: false,
    configurable: false,
    writable: false,
  });
  return runtime;
}
