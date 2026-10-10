import { canonicalRouteLocale } from './public-entry';
import type { PublicPagination } from './public-route';

/** API segments are literal identifiers, never pre-encoded paths or URL values. */
function contentPath(locale: string, segments: readonly string[]): string | null {
  // Reject percent escapes instead of turning an API alias into a valid slug.
  if (segments.some((segment) => /[%/?#\\]/.test(segment))) return null;
  const path = `/${locale}/${segments.join('/')}`;
  return canonicalRouteLocale(path) === locale ? path : null;
}

export function catalogPath(locale: string): string | null {
  return contentPath(locale, ['courses']);
}

export function coursePath(locale: string, slug: string): string | null {
  return contentPath(locale, ['courses', slug]);
}

export function lessonPath(locale: string, courseSlug: string, lessonSlug: string): string | null {
  return contentPath(locale, ['courses', courseSlug, 'lessons', lessonSlug]);
}

/** Keep only this surface's normalized limit and opaque next token; null restarts. */
export function paginationPath(
  basePath: string,
  pagination: PublicPagination,
  nextCursor: string | null,
  outline: boolean,
): string | null {
  if (
    basePath.includes('?') ||
    canonicalRouteLocale(basePath) === null ||
    basePath.split('/').length !== (outline ? 4 : 3) ||
    !/^(?:[1-9][0-9]?|100)$/.test(pagination.limit) ||
    (nextCursor !== null && (nextCursor.length > 1024 || !/^[A-Za-z0-9_-]+$/.test(nextCursor)))
  )
    return null;
  const query = new URLSearchParams();
  query.set(outline ? 'lessonLimit' : 'limit', pagination.limit);
  if (nextCursor !== null) query.set(outline ? 'lessonCursor' : 'cursor', nextCursor);
  return `${basePath}?${query.toString()}`;
}
