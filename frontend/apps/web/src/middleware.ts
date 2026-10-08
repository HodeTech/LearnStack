import { NextResponse, type NextRequest } from 'next/server';

import { createConfiguredPublicClient, publicTraceparent } from '@/server/configured-public-client';
import { INGRESS_HEADER, verifyNextProvenance } from '@/server/ingress';
import { publicEntry, publicRedirect } from '@/server/public-entry';

function refusal(status: 404 | 429 | 503, retryAfter?: number): NextResponse {
  return new NextResponse(
    status === 404 ? 'Not found' : status === 429 ? 'Too many requests' : 'Service unavailable',
    {
      status,
      headers: {
        'cache-control': 'no-store',
        'content-type': 'text/plain; charset=utf-8',
        ...(retryAfter === undefined ? {} : { 'retry-after': String(retryAfter) }),
      },
    },
  );
}

/** Bootstrap is request-local; only the API resolves the captured visitor host. */
export async function middleware(request: NextRequest) {
  const url = new URL(request.url);
  const envelope = request.headers.get(INGRESS_HEADER);
  const context = verifyNextProvenance(envelope, process.env.LEARNSTACK_PUBLIC_HOP_SECRET, {
    method: request.method,
    target: `${url.pathname}${url.search}`,
  });
  if (!context) return refusal(404);
  try {
    const traceparent = publicTraceparent(request.headers.get('traceparent'));
    const client = createConfiguredPublicClient(envelope, {
      traceparent,
      signal: request.signal,
    });
    if (!client) return refusal(404);
    const bootstrap = await client.getSite();
    if (bootstrap.kind !== 'success') {
      if (bootstrap.kind === 'api-error') {
        if (bootstrap.status === 404) return refusal(404);
        if (bootstrap.status === 429)
          return refusal(
            429,
            bootstrap.error.code === 'rate_limited' ? bootstrap.error.retryAfter : undefined,
          );
      }
      return refusal(503);
    }
    const entry = publicEntry(context.target, bootstrap.data);
    if (entry.kind === 'refuse') return refusal(entry.status);
    if (entry.kind === 'redirect') {
      const response = NextResponse.redirect(
        publicRedirect(context.host, entry.path),
        entry.status,
      );
      response.headers.set('cache-control', 'no-store');
      return response;
    }
    // Framework RSC/navigation inputs are protocol data, not host/peer authority.
    // Rebuild request headers; never spread client cookies or internal overrides.
    const downstream = new Headers();
    for (const name of [
      'accept',
      'rsc',
      'next-router-state-tree',
      'next-router-prefetch',
      'next-router-segment-prefetch',
      'next-url',
    ]) {
      const value = request.headers.get(name);
      if (value !== null) downstream.set(name, value);
    }
    downstream.set(INGRESS_HEADER, envelope!); // Verified above; the caller verifies again.
    downstream.set('traceparent', traceparent);
    const response = NextResponse.next({ request: { headers: downstream } });
    response.headers.set('cache-control', 'no-store');
    return response;
  } catch {
    // No provider/request/configuration details become HTML, headers or logs.
    return refusal(503);
  }
}

export const config = {
  runtime: 'nodejs',
  matcher: ['/((?!_next/|api/healthz$|favicon.ico$).*)'],
};
