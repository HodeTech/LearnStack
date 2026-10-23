import 'server-only';

import type { BffSessionHolder } from './session-runtime.js';

/** Passive cross-bundle lookup; a build or request never creates a second owner. */
export function bffSessionOwner(): BffSessionHolder['store'] {
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
  return (holder as BffSessionHolder).store;
}
