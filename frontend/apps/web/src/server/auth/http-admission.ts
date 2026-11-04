import 'server-only';

import { NextResponse } from 'next/server';
import type { NextRequest } from 'next/server';

import { INGRESS_HEADER, verifyNextProvenance } from '../ingress';
import type { IngressContext } from '../ingress';
import { readPublicAdmission } from '../public-admission';
import type { BffPageRenderer } from './http-protocol';
import { bffRequestOwner } from './request-owner';
import { bffSessionProtocol } from './session-owner';
import { isNativeReturnPath } from './session-path';

/** Host admission must already be published by the real bootstrap. */
export async function admitBffHttp(
  request: NextRequest,
  context: IngressContext,
  signal: AbortSignal,
  render?: BffPageRenderer,
) {
  const observed = new URL(request.url);
  const verified = verifyNextProvenance(
    request.headers.get(INGRESS_HEADER),
    process.env.LEARNSTACK_PUBLIC_HOP_SECRET,
    { method: request.method, target: observed.pathname + observed.search },
  );
  if (
    !verified ||
    Object.keys(context).some(
      (key) => verified[key as keyof IngressContext] !== context[key as keyof IngressContext],
    )
  )
    throw new Error('BFF request context unavailable');
  const publicRead = readPublicAdmission(context);
  const active = () => {
    publicRead.assertActive();
    signal.throwIfAborted();
  };
  active();
  const protocol = bffSessionProtocol();
  const input = {
    host: context.host,
    source: context.peer,
    method: context.method,
    target: context.target,
    headers: request.headers,
    body: request.body,
    signal,
  };
  const response = await protocol.handle(input, render);
  active();
  if (response) {
    const headers = new Headers(response.headers);
    const location = headers.get('location');
    // Next's middleware adapter requires an absolute Location. Only the independently
    // verified host and the owner's closed return paths can form a local redirect.
    if (location?.startsWith('/')) {
      if (
        !isNativeReturnPath(location) &&
        !['/api/auth/entry', '/api/auth/signin'].includes(location)
      )
        throw new Error('BFF response destination unavailable');
      headers.set('location', 'https://' + context.host + location);
    }
    return Object.freeze({
      response: new NextResponse(response.body, { status: response.status, headers }),
      session: null,
      cookies: [],
      unavailable: false,
    });
  }
  const projected = await protocol.project(input, bffRequestOwner().activity(context));
  active();
  if (projected.refusalStatus !== undefined)
    return Object.freeze({
      ...projected,
      response: new NextResponse(null, {
        status: projected.refusalStatus,
        headers: { 'cache-control': 'no-store' },
      }),
    });
  bffRequestOwner().publish(context, projected.session);
  return Object.freeze({ response: null, ...projected });
}
