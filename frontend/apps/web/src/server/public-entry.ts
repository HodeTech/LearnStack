import type { ServerSdk } from '@learnstack/sdk/server';

import { normalizeHost, PUBLIC_HTTPS_PORT, validTarget } from './ingress';

type SiteResult = Awaited<ReturnType<ServerSdk['getSite']>>;
export type PublicSite = Extract<SiteResult, { kind: 'success' }>['data'];
export type PublicEntry =
  | { readonly kind: 'continue'; readonly locale: string | null }
  | { readonly kind: 'redirect'; readonly status: 307 | 308; readonly path: string }
  | { readonly kind: 'refuse'; readonly status: 404 | 503 };

/** Mirrors SharedKernel LocaleTag; membership, never a known-locale registry, admits it. */
export function canonicalLocale(value: string): string | null {
  if (
    value.length > 35 ||
    /[^A-Za-z0-9-]/.test(value) ||
    !/^[a-zA-Z]{2,8}(?:-[a-zA-Z]{4})?(?:-(?:[a-zA-Z]{2}|[0-9]{3}))?(?:-(?:[a-zA-Z0-9]{5,8}|[0-9][a-zA-Z0-9]{3}))*$/.test(
      value,
    )
  )
    return null;
  return value
    .split('-')
    .map((part, index) => {
      const lower = part.toLowerCase();
      if (index > 0 && lower.length === 4 && !/^[0-9]/.test(lower))
        return lower.charAt(0).toUpperCase() + lower.slice(1);
      return index > 0 && lower.length === 2 ? lower.toUpperCase() : lower;
    })
    .join('-');
}

function slug(value: string): boolean {
  try {
    const decoded = decodeURIComponent(value);
    return (
      decoded.length <= 160 &&
      /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(decoded) &&
      !/^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/.test(
        decoded,
      )
    );
  } catch {
    return false;
  }
}

function contentPath(parts: readonly string[]): boolean {
  return (
    parts[0] === 'courses' &&
    (parts.length === 1 ||
      (parts.length === 2 && slug(parts[1] ?? '')) ||
      (parts.length === 4 &&
        slug(parts[1] ?? '') &&
        parts[2] === 'lessons' &&
        slug(parts[3] ?? '')))
  );
}

/** Fixed error namespace; it grants no Education-route authority. */
export function isPublicStatusTarget(target: string): boolean {
  const parts = (target.split('?')[0] ?? '').slice(1).split('/');
  return parts.length === 3 && parts[1] === 'status' && parts[2] === 'not-found';
}

/** The configured caller uses only a canonical locale from a supported signed route. */
export function canonicalRouteLocale(target: string): string | null {
  if (!validTarget(target)) return null;
  const parts = (target.split('?')[0] ?? '').slice(1).split('/');
  const prefix = parts[0] ?? '';
  const locale = canonicalLocale(prefix);
  return locale === prefix && contentPath(parts.slice(1)) ? locale : null;
}

/** Bootstrap must be live before this membership-first entry policy is evaluated. */
export function publicEntry(target: string, site: PublicSite): PublicEntry {
  if (!validTarget(target)) return { kind: 'refuse', status: 404 };
  const enabled = site.enabledLocales;
  if (
    enabled.some((locale) => canonicalLocale(locale) !== locale) ||
    new Set(enabled).size !== enabled.length ||
    canonicalLocale(site.defaultLocale) !== site.defaultLocale ||
    (enabled.length > 0 && !enabled.includes(site.defaultLocale))
  )
    return { kind: 'refuse', status: 503 };
  if (enabled.length === 0) return { kind: 'refuse', status: 404 };
  const queryAt = target.indexOf('?');
  const path = queryAt < 0 ? target : target.slice(0, queryAt);
  const query = queryAt < 0 ? '' : target.slice(queryAt);
  if (path === '/')
    return { kind: 'redirect', status: 307, path: `/${site.defaultLocale}/courses${query}` };
  const parts = path.slice(1).split('/');
  const prefix = parts[0] ?? '';
  const canonical = canonicalLocale(prefix);
  if (canonical !== null && enabled.includes(canonical)) {
    const root = parts.length === 1 || (parts.length === 2 && parts[1] === '');
    if (!root && !contentPath(parts.slice(1)) && !isPublicStatusTarget(target))
      return { kind: 'refuse', status: 404 };
    if (canonical !== prefix)
      return {
        kind: 'redirect',
        status: 308,
        path: '/' + [canonical, ...parts.slice(1)].join('/') + query,
      };
    return root
      ? { kind: 'redirect', status: 307, path: `/${canonical}/courses${query}` }
      : { kind: 'continue', locale: canonical };
  }
  if (path === '/studio' || path === '/portal') return { kind: 'continue', locale: null };
  if (contentPath(parts))
    return { kind: 'redirect', status: 307, path: `/${site.defaultLocale}${path}${query}` };
  return { kind: 'refuse', status: 404 };
}

/** Fixed HTTPS ingress port; query data and request.url never supply an authority. */
export function publicRedirect(host: string, path: string): string {
  const normalized = normalizeHost(host);
  if (normalized !== host || !validTarget(path)) throw new Error('Invalid public redirect');
  const hostname = host.startsWith('[') ? host.slice(0, host.indexOf(']') + 1) : host.split(':')[0];
  return `https://${hostname}:${PUBLIC_HTTPS_PORT}${path}`;
}
