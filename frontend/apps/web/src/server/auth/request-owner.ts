import 'server-only';

import type { BffRequestHolder } from './request-runtime';

/** A passive lookup, shared across native/middleware/RSC bundles. */
export function bffRequestOwner(): BffRequestHolder {
  const descriptor = Object.getOwnPropertyDescriptor(
    globalThis,
    Symbol.for('learnstack.bff-request.v1'),
  );
  const value: unknown = descriptor?.value;
  if (
    !descriptor ||
    descriptor.writable ||
    descriptor.configurable ||
    descriptor.enumerable ||
    !value ||
    typeof value !== 'object' ||
    !Object.isFrozen(value) ||
    !('version' in value) ||
    value.version !== 1 ||
    !('activity' in value) ||
    typeof value.activity !== 'function' ||
    !('flight' in value) ||
    typeof value.flight !== 'function' ||
    !('publish' in value) ||
    typeof value.publish !== 'function' ||
    !('read' in value) ||
    typeof value.read !== 'function'
  )
    throw new Error('BFF request context unavailable');
  return value as BffRequestHolder;
}
