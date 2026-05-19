import { NextResponse, type NextRequest } from 'next/server';

/**
 * Tenant + locale resolution at the edge — the only place a host maps to a
 * tenant. The real `IHostToTenantResolver` lookup is wired in Phase 02a;
 * this scaffold propagates the request headers downstream Server Components +
 * route handlers expect (`x-tenant-id`, `x-organization-id`, `x-locale`),
 * matching the shape documented in docs/architecture/14-frontend-architecture.md
 * § Tenant + Organization Resolution.
 *
 * Headers MUST be written to the request (via `NextResponse.next({ request })`)
 * — writing only to the response makes them visible to the browser but not to
 * downstream `headers()` calls in RSC. Real Phase 02a resolution will plug in
 * here; until then, the values are placeholders so layouts can be authored
 * against the final shape today.
 */
export function middleware(request: NextRequest) {
  const url = new URL(request.url);
  const host = request.headers.get('host') ?? 'localhost';

  const requestHeaders = new Headers(request.headers);
  // TODO(2026-05-19, @platform): replace placeholders once `IHostToTenantResolver`
  // is wired and the `/v1/tenants/resolve-host` endpoint exists (Phase 02a).
  requestHeaders.set('x-tenant-id', host);
  requestHeaders.set('x-locale', extractLocaleOrDefault(url.pathname));

  return NextResponse.next({ request: { headers: requestHeaders } });
}

// TODO(2026-05-19, @platform): drive the supported-locale set from
// `Tenant.SupportedLocales` once tenant resolution lands. Right now the
// regex matches any ISO-like locale and falls back to `en`.
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
