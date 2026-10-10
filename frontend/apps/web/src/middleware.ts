import { NextResponse, type NextRequest } from 'next/server';

import { createConfiguredPublicClient, publicTraceparent } from '@/server/configured-public-client';
import { INGRESS_HEADER, verifyNextProvenance } from '@/server/ingress';
import { beginPublicAdmission } from '@/server/public-admission';
import { isPublicStatusTarget, publicEntry, publicRedirect } from '@/server/public-entry';

function refusal(method: string, status: 404 | 429 | 503, retryAfter?: number): NextResponse {
  return new NextResponse(
    method === 'HEAD'
      ? null
      : status === 404
        ? 'Not found'
        : status === 429
          ? 'Too many requests'
          : 'Service unavailable',
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

function boundedRetryAfter(status: number, code: string, value: unknown): number | undefined {
  return ((status === 429 && code === 'rate_limited') ||
    (status === 503 && (code === 'dependency_unavailable' || code === 'audit_unavailable'))) &&
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 60
    ? value
    : undefined;
}

/** Bootstrap is request-local; only the API resolves the captured visitor host. */
export async function middleware(request: NextRequest) {
  const url = new URL(request.url);
  const envelope = request.headers.get(INGRESS_HEADER);
  const context = verifyNextProvenance(envelope, process.env.LEARNSTACK_PUBLIC_HOP_SECRET, {
    method: request.method,
    target: `${url.pathname}${url.search}`,
  });
  if (!context) return refusal(request.method, 404);
  let admission: ReturnType<typeof beginPublicAdmission> | undefined;
  try {
    admission = beginPublicAdmission(context);
    const activeAdmission = admission;
    const finish = (response: NextResponse) => {
      activeAdmission.refuse();
      return response;
    };
    const traceparent = publicTraceparent(request.headers.get('traceparent'));
    const signal = AbortSignal.any([admission.signal, request.signal]);
    const client = createConfiguredPublicClient(envelope, {
      traceparent,
      signal,
    });
    if (!client) return finish(refusal(request.method, 404));
    const bootstrap = await client.getSite();
    admission.assertActive();
    signal.throwIfAborted();
    if (bootstrap.kind !== 'success') {
      if (bootstrap.kind === 'api-error') {
        if (bootstrap.status === 404) return finish(refusal(request.method, 404));
        if (bootstrap.status === 429)
          return finish(
            refusal(
              request.method,
              429,
              boundedRetryAfter(
                bootstrap.status,
                bootstrap.error.code,
                'retryAfter' in bootstrap.error ? bootstrap.error.retryAfter : undefined,
              ),
            ),
          );
        return finish(
          refusal(
            request.method,
            503,
            boundedRetryAfter(
              bootstrap.status,
              bootstrap.error.code,
              'retryAfter' in bootstrap.error ? bootstrap.error.retryAfter : undefined,
            ),
          ),
        );
      }
      return finish(refusal(request.method, 503));
    }
    const entry = publicEntry(context.target, bootstrap.data);
    if (entry.kind === 'refuse') return finish(refusal(request.method, entry.status));
    if (entry.kind === 'redirect') {
      const response = NextResponse.redirect(
        publicRedirect(context.host, entry.path),
        entry.status,
      );
      response.headers.set('cache-control', 'no-store');
      return finish(response);
    }
    // Next's pinned adapter hides Flight headers here and restores them after
    // middleware. Rebuild ordinary headers; protocol restoration is proved by
    // the real production fixture, not by forwarding authority or client cookies.
    const downstream = new Headers();
    const accept = request.headers.get('accept');
    if (accept !== null) downstream.set('accept', accept);
    downstream.set(INGRESS_HEADER, envelope!); // Verified above; the caller verifies again.
    downstream.set('traceparent', traceparent);
    const response = NextResponse.next({
      status: isPublicStatusTarget(context.target) ? 404 : 200,
      request: { headers: downstream },
    });
    response.headers.set('cache-control', 'no-store');
    admission.publish(bootstrap.data);
    return response;
  } catch {
    try {
      admission?.refuse();
    } catch {
      /* Completed or poisoned native context. */
    }
    // No provider/request/configuration details become HTML, headers or logs.
    return refusal(request.method, 503);
  }
}

export const config = {
  runtime: 'nodejs',
  matcher: ['/((?!_next/|api/healthz$|favicon\\.ico$).*)'],
};
