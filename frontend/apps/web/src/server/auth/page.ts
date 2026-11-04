import 'server-only';

import { headers } from 'next/headers';

import { INGRESS_HEADER, verifyProvenance } from '../ingress';
import type { NativePageSnapshot } from './page-contract';
import { bffRequestOwner } from './request-owner';
import { bffSessionEnabled } from './session-owner';
import { isNativeReturnPath } from './session-path';

export type PreparedPageRead = Readonly<{
  snapshot: NativePageSnapshot;
  assertActive: () => void;
}>;

/** Reads one already-prepared request value. It never loads data or credentials. */
export async function optionalPreparedPage(): Promise<PreparedPageRead | null> {
  if (!bffSessionEnabled()) return null;
  const incoming = await headers();
  const binding = verifyProvenance(
    incoming.get(INGRESS_HEADER),
    process.env.LEARNSTACK_PUBLIC_HOP_SECRET,
  );
  if (!binding) throw new Error('Protected page context unavailable');
  const path = binding.target.split('?', 1)[0]!;
  if (path === '/' || !isNativeReturnPath(path)) return null;
  const read = bffRequestOwner().read(binding);
  read.assertActive();
  const snapshot = read.page;
  if (snapshot === null) return null;
  if (snapshot.target !== path) throw new Error('Protected page context unavailable');
  return Object.freeze({
    snapshot,
    assertActive() {
      read.assertActive();
      if (bffRequestOwner().read(binding).page !== snapshot)
        throw new Error('Protected page context unavailable');
    },
  });
}

export async function preparedPage(): Promise<PreparedPageRead> {
  const read = await optionalPreparedPage();
  if (!read) throw new Error('Protected page context unavailable');
  read.assertActive();
  return read;
}
