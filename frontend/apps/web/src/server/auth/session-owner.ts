import 'server-only';

import type { BffSessionHolder } from './session-runtime.js';

/** Passive cross-bundle lookup; a build or request never creates a second owner. */
function holder(): BffSessionHolder {
  const descriptor = Object.getOwnPropertyDescriptor(
    globalThis,
    Symbol.for('learnstack.bff-sessions.v1'),
  );
  const holder: unknown = descriptor?.value;
  if (
    !descriptor ||
    descriptor.configurable ||
    descriptor.enumerable ||
    descriptor.writable ||
    !holder ||
    typeof holder !== 'object' ||
    !Object.isFrozen(holder) ||
    !('version' in holder) ||
    holder.version !== 1 ||
    !('store' in holder)
  )
    throw new Error('BFF session owner unavailable');
  const store = holder.store;
  if (
    !store ||
    typeof store !== 'object' ||
    !Object.isFrozen(store) ||
    !['start', 'complete', 'peek', 'admit', 'signout', 'consumeLogout', 'stop'].every(
      (key) => key in store && typeof (store as Record<string, unknown>)[key] === 'function',
    )
  )
    throw new Error('BFF session owner unavailable');
  const http = (holder as BffSessionHolder).http;
  if (
    !http ||
    !Object.isFrozen(http) ||
    typeof http.handle !== 'function' ||
    typeof http.project !== 'function'
  )
    throw new Error('BFF session owner unavailable');
  return holder as BffSessionHolder;
}

export function bffSessionProtocol(): BffSessionHolder['http'] {
  return holder().http;
}

/** Native installation is the mode authority; no request/header or bundled fallback enables it. */
export function bffSessionEnabled(): boolean {
  if (!Object.getOwnPropertyDescriptor(globalThis, Symbol.for('learnstack.bff-sessions.v1')))
    return false;
  holder();
  return true;
}
