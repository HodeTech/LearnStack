import 'server-only';

import { headers } from 'next/headers';

import { INGRESS_HEADER, verifyProvenance } from '../ingress';
import { bffRequestOwner } from './request-owner';
import type { SessionProjection } from './session-contract';

/** Read-only RSC facade. No cookies, refresh, provider call or idle mutation. */
export async function auth(): Promise<SessionProjection | null> {
  const incoming = await headers();
  const binding = verifyProvenance(
    incoming.get(INGRESS_HEADER),
    process.env.LEARNSTACK_PUBLIC_HOP_SECRET,
  );
  if (!binding) throw new Error('BFF request context unavailable');
  const read = bffRequestOwner().read(binding);
  read.assertActive();
  return read.session;
}
