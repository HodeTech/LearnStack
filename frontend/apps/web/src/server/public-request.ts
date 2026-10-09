import 'server-only';

import { createConfiguredPublicClient, publicTraceparent } from './configured-public-client';
import type { ConfiguredPublicClient } from './configured-public-client';
import { INGRESS_HEADER, verifyProvenance } from './ingress';
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
export const getPublicRequest = requestMemo(async (incoming): Promise<PublicRequest | null> => {
  const envelope = incoming.get(INGRESS_HEADER);
  const context = verifyProvenance(envelope, process.env.LEARNSTACK_PUBLIC_HOP_SECRET);
  if (!context) return null;
  // A missing static asset can invoke Next's root not-found renderer. These
  // transport paths bypass public middleware and must not bootstrap tenant UI.
  const path = context.target.split('?')[0];
  if (path === '/favicon.ico' || path === '/api/healthz' || path?.startsWith('/_next/'))
    return null;
  try {
    const client = createConfiguredPublicClient(envelope, {
      traceparent: publicTraceparent(incoming.get('traceparent')),
    });
    if (!client) return null;
    const result = await client.getSite();
    if (result.kind !== 'success') return null;
    const entry = publicEntry(context.target, result.data);
    if (entry.kind !== 'continue') return null;
    return {
      context,
      client,
      site: result.data,
      locale: entry.locale,
      route: publicRoute(context.target),
    };
  } catch {
    // Fail before selecting a tenant document; private configuration is never logged.
    return null;
  }
});
