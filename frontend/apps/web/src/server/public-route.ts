import { canonicalRouteLocale, isPublicStatusTarget } from './public-entry';

export type PublicRoute =
  | { readonly kind: 'catalog'; readonly path: string }
  | { readonly kind: 'course'; readonly path: string; readonly slug: string }
  | {
      readonly kind: 'lesson';
      readonly path: string;
      readonly slug: string;
      readonly lessonSlug: string;
    }
  | { readonly kind: 'status'; readonly path: string }
  | { readonly kind: 'scaffold'; readonly path: string };

/** Call only after live publicEntry admission; observed params are not authority. */
export function publicRoute(target: string): PublicRoute {
  const path = target.split('?')[0] ?? '';
  if (isPublicStatusTarget(target)) return { kind: 'status', path };
  if (canonicalRouteLocale(target) === null) return { kind: 'scaffold', path };
  const parts = path.split('/');
  if (parts.length === 3) return { kind: 'catalog', path };
  const slug = decodeURIComponent(parts[3] ?? '');
  return parts.length === 4
    ? { kind: 'course', path, slug }
    : { kind: 'lesson', path, slug, lessonSlug: decodeURIComponent(parts[5] ?? '') };
}

export type PublicPagination = {
  readonly cursor?: string;
  readonly limit: string;
  readonly isPaginated: boolean;
};

function boundedLimit(raw: string | undefined): number | null {
  if (raw === undefined) return 20;
  if (!/^[0-9]+$/.test(raw)) return null;
  let limit = 0;
  for (const digit of raw) limit = Math.min(100, limit * 10 + Number(digit));
  return limit === 0 ? null : limit;
}

/** Cursor contents remain opaque; the API owns decoding and scope validation. */
export function publicPagination(target: string, outline: boolean): PublicPagination | null {
  const parameters = new URLSearchParams(
    target.includes('?') ? target.slice(target.indexOf('?') + 1) : '',
  );
  const cursorName = outline ? 'lessonCursor' : 'cursor';
  const limitName = outline ? 'lessonLimit' : 'limit';
  const cursors = parameters.getAll(cursorName);
  const limits = parameters.getAll(limitName);
  if (cursors.length > 1 || limits.length > 1) return null;
  const cursor = cursors[0];
  const rawLimit = limits[0];
  if (cursor !== undefined && (cursor.length > 1024 || !/^[A-Za-z0-9_-]+$/.test(cursor)))
    return null;
  const limit = boundedLimit(rawLimit);
  if (limit === null) return null;
  return {
    cursor,
    limit: String(limit),
    isPaginated: cursor !== undefined,
  };
}
