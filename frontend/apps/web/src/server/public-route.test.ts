// @vitest-environment node
import { describe, expect, it } from 'vitest';

import { publicPagination, publicRoute } from './public-route';

describe('admitted public route dispatch', () => {
  it.each([
    ['/tr/courses?locale=en', { kind: 'catalog', path: '/tr/courses' }],
    [
      '/zh-Hans-CN/courses/%66oundation?slug=other',
      { kind: 'course', path: '/zh-Hans-CN/courses/%66oundation', slug: 'foundation' },
    ],
    [
      '/ar/courses/foundation/lessons/%69ntro?lessonSlug=other',
      {
        kind: 'lesson',
        path: '/ar/courses/foundation/lessons/%69ntro',
        slug: 'foundation',
        lessonSlug: 'intro',
      },
    ],
    ['/tr/status/not-found?cursor=private', { kind: 'status', path: '/tr/status/not-found' }],
    ['/studio', { kind: 'scaffold', path: '/studio' }],
    ['/portal', { kind: 'scaffold', path: '/portal' }],
  ])('dispatches the signed admitted target %s', (target, route) => {
    expect(publicRoute(target)).toEqual(route);
  });
});

describe.each([
  { outline: false, cursor: 'cursor', limit: 'limit' },
  { outline: true, cursor: 'lessonCursor', limit: 'lessonLimit' },
])('owned pagination: $cursor / $limit', ({ outline, cursor, limit }) => {
  const target = '/tr/courses/foundation';

  it('defaults to 20 and does not mark a limit-only request as a continuation', () => {
    expect(publicPagination(target, outline)).toEqual({ limit: '20', isPaginated: false });
    expect(publicPagination(`${target}?${limit}=7`, outline)).toEqual({
      limit: '7',
      isPaginated: false,
    });
  });

  it.each([
    ['1', '1'],
    ['0002', '2'],
    ['100', '100'],
    ['101', '100'],
    ['9999999999', '100'],
  ])('normalizes positive decimal %s to the API bound %s', (value, expected) => {
    expect(publicPagination(`${target}?${limit}=${value}`, outline)).toEqual({
      limit: expected,
      isPaginated: false,
    });
  });

  it.each([
    '',
    '0',
    '000',
    '-1',
    '+1',
    '%2B1',
    '%201',
    '1%20',
    '1.5',
    '1e2',
    '１２',
    '10000000000',
    '%0A1',
  ])('refuses malformed owned limit %s', (value) => {
    expect(publicPagination(`${target}?${limit}=${value}`, outline)).toBeNull();
  });

  it.each(['', 'a+b', 'a%2Bb', 'a%2Fb', 'a=', 'a.b', '%20', '%0A', 'é', '%FF'])(
    'refuses malformed owned cursor %s',
    (value) => {
      expect(publicPagination(`${target}?${cursor}=${value}`, outline)).toBeNull();
    },
  );

  it('rejects duplicate owned names even when encoded or equal', () => {
    for (const name of [cursor, limit]) {
      const encoded = `%${name.charCodeAt(0).toString(16)}${name.slice(1)}`;
      expect(publicPagination(`${target}?${name}=1&${encoded}=1`, outline)).toBeNull();
      expect(publicPagination(`${target}?${name}=1&${name}=2`, outline)).toBeNull();
    }
  });

  it('accepts opaque base64url characters without decoding or interpreting the cursor', () => {
    // These tokens are intentionally not valid API cursor envelopes.
    for (const token of ['A', 'not_an_api_cursor-0', 'a'.repeat(1024)]) {
      expect(publicPagination(`${target}?${cursor}=${token}`, outline)).toEqual({
        cursor: token,
        limit: '20',
        isPaginated: true,
      });
    }
    expect(publicPagination(`${target}?${cursor}=${'a'.repeat(1025)}`, outline)).toBeNull();
  });

  it('ignores foreign query data including the other surface pagination names', () => {
    const foreignCursor = outline ? 'cursor' : 'lessonCursor';
    const foreignLimit = outline ? 'limit' : 'lessonLimit';
    const query = `${foreignCursor}=&${foreignCursor}=bad%2Ftoken&${foreignLimit}=0&${foreignLimit}=huge&locale=en&next=https://evil.example&unknown=raw`;
    expect(publicPagination(`${target}?${query}`, outline)).toEqual({
      limit: '20',
      isPaginated: false,
    });
    expect(publicPagination(`${target}?${query}&${cursor}=opaque_1&${limit}=3`, outline)).toEqual({
      cursor: 'opaque_1',
      limit: '3',
      isPaginated: true,
    });
  });
});
