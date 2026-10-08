import { NextResponse, type NextRequest } from 'next/server';

import { INGRESS_HEADER, verifyNextProvenance } from '@/server/ingress';

/** Step 1 proves ingress admission; bootstrap/entry is wired by P02d-5 Step 3. */
export function middleware(request: NextRequest) {
  const url = new URL(request.url);
  const context = verifyNextProvenance(
    request.headers.get(INGRESS_HEADER),
    process.env.LEARNSTACK_PUBLIC_HOP_SECRET,
    { method: request.method, target: `${url.pathname}${url.search}` },
  );
  return new NextResponse(context ? 'Service unavailable' : 'Not found', {
    status: context ? 503 : 404,
    headers: { 'cache-control': 'no-store' },
  });
}

export const config = {
  runtime: 'nodejs',
  matcher: ['/((?!_next/|api/healthz$|favicon.ico$).*)'],
};
