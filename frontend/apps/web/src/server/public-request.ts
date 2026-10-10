import 'server-only';

import { createConfiguredPublicClient, publicTraceparent } from './configured-public-client';
import type { ConfiguredPublicClient } from './configured-public-client';
import { INGRESS_HEADER, verifyProvenance } from './ingress';
import { readPublicAdmission } from './public-admission';
import { publicEntry } from './public-entry';
import type { PublicSite } from './public-entry';
import { publicRoute } from './public-route';
import type { PublicRoute } from './public-route';
import { requestMemo } from './request-memo';

export type PublicRequest = {
  readonly context: NonNullable<ReturnType<typeof verifyProvenance>>;
  readonly client: ConfiguredPublicClient;
  readonly site: PublicSite;
  readonly locale: string | null;
  readonly route: PublicRoute;
};

/** One incoming RSC request only; this module deliberately knows no UI catalogue. */
const loadPublicRequest = requestMemo(async (incoming): Promise<PublicRequest | null> => {
  const envelope = incoming.get(INGRESS_HEADER);
  const context = verifyProvenance(envelope, process.env.LEARNSTACK_PUBLIC_HOP_SECRET);
  if (!context) return null;
  // A missing static asset can invoke Next's root not-found renderer. These
  // transport paths bypass public middleware and must not bootstrap tenant UI.
  const path = context.target.split('?')[0];
  if (path === '/favicon.ico' || path === '/api/healthz' || path?.startsWith('/_next/'))
    return null;
  const admission = readPublicAdmission(context);
  try {
    const client = createConfiguredPublicClient(envelope, {
      traceparent: publicTraceparent(incoming.get('traceparent')),
      signal: admission.signal,
    });
    if (!client) throw new Error('Public admission context unavailable');
    const entry = publicEntry(context.target, admission.site);
    if (entry.kind !== 'continue') throw new Error('Public admission context unavailable');
    admission.assertActive();
    return {
      context,
      client,
      site: admission.site,
      locale: entry.locale,
      route: publicRoute(context.target),
    };
  } catch {
    // Distinguish completed work from an active invariant without private details.
    admission.assertActive();
    throw new Error('Public admission context unavailable');
  }
});

export function assertPublicRequestActive(request: PublicRequest): void {
  const admission = readPublicAdmission(request.context);
  // Identical host/path bindings can overlap; a value still belongs to one store.
  if (admission.site !== request.site) throw new Error('Public admission context unavailable');
  admission.assertActive();
}

/** A memo hit still crosses an await and must recheck the native lifetime. */
export async function getPublicRequest(): Promise<PublicRequest | null> {
  const request = await loadPublicRequest();
  if (request) assertPublicRequestActive(request);
  return request;
}
