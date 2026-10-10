import 'server-only';

import type { IngressContext } from './ingress';
import type {
  PublicAdmissionHolder,
  PublicAdmissionRead,
  PublicAdmissionWrite,
} from './public-admission-runtime';
import type { PublicSite } from './public-entry';

/** Lazy retrieval permits builds; a request never creates an alternate ALS holder. */
function holder(): PublicAdmissionHolder {
  const descriptor = Object.getOwnPropertyDescriptor(
    globalThis,
    Symbol.for('learnstack.public-admission.v1'),
  );
  const value: unknown = descriptor?.value;
  if (
    !descriptor ||
    descriptor.enumerable ||
    descriptor.configurable ||
    descriptor.writable ||
    typeof value !== 'object' ||
    value === null ||
    !Object.isFrozen(value) ||
    !('version' in value) ||
    value.version !== 1 ||
    !('begin' in value) ||
    typeof value.begin !== 'function' ||
    !('read' in value) ||
    typeof value.read !== 'function'
  )
    throw new Error('Public admission context unavailable');
  return value as PublicAdmissionHolder;
}

export function beginPublicAdmission(binding: IngressContext): PublicAdmissionWrite {
  return holder().begin(binding);
}

export function readPublicAdmission(
  binding: IngressContext,
): Omit<PublicAdmissionRead, 'site'> & { readonly site: PublicSite } {
  // The sole native holder clones and freezes the SDK-validated closed Site shape.
  return holder().read(binding) as Omit<PublicAdmissionRead, 'site'> & {
    readonly site: PublicSite;
  };
}
