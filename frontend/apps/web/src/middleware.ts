import { NextResponse, type NextRequest } from 'next/server';

/**
 * Tenant + locale resolution at the edge — the only place a host maps to a
 * tenant. The real `IHostToTenantResolver` lookup is wired in Phase 02a;
 * this scaffold writes the request headers downstream code expects so that
 * the route-group layouts can be authored against the real shape today.
 */
export function middleware(request: NextRequest) {
  const url = new URL(request.url);
  const host = request.headers.get('host') ?? 'localhost';

  const response = NextResponse.next();
  response.headers.set('x-ls-host', host);
  response.headers.set('x-ls-locale', extractLocaleOrDefault(url.pathname));

  return response;
}

function extractLocaleOrDefault(pathname: string): string {
  const segments = pathname.split('/').filter(Boolean);
  const first = segments[0];
  if (first && /^[a-z]{2}(-[A-Z]{2})?$/.test(first)) {
    return first;
  }
  return 'en';
}

export const config = {
  matcher: ['/((?!_next/static|_next/image|favicon.ico).*)'],
};
