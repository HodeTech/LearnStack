// @vitest-environment node
import { describe, expect, it } from 'vitest';

import { catalogPath, coursePath, lessonPath, paginationPath } from './public-paths';
import { publicPagination } from './public-route';

describe('safe public local paths', () => {
  it.each(['en', 'tr-TR', 'zh-Hans-CN', 'ar', 'courses'])(
    'preserves admitted canonical locale %s without a UI language registry',
    (locale) => {
      expect(catalogPath(locale)).toBe(`/${locale}/courses`);
      expect(coursePath(locale, 'foundation-2')).toBe(`/${locale}/courses/foundation-2`);
      expect(lessonPath(locale, 'foundation-2', 'intro-1')).toBe(
        `/${locale}/courses/foundation-2/lessons/intro-1`,
      );
    },
  );

  it.each(['', 'EN', 'tr-tr', 'en_US', '../en', '//evil.example', 'en?x=1', 'en#x', '%65n'])(
    'refuses unsafe or noncanonical locale %s',
    (locale) => {
      expect(catalogPath(locale)).toBeNull();
      expect(coursePath(locale, 'foundation')).toBeNull();
      expect(lessonPath(locale, 'foundation', 'intro')).toBeNull();
    },
  );

  it.each([
    '',
    '.',
    '..',
    '../other',
    'one/two',
    'one\\two',
    '//evil.example',
    'https://evil.example',
    'javascript:alert(1)',
    'data:text/html,evil',
    'item?cursor=private',
    'item#private',
    'Foundation',
    'temel-kursu ',
    'temel--kursu',
    '-foundation',
    'foundation-',
    'öğrenme',
    '%66oundation',
    '%2e%2e',
    '%252f',
    'line\nbreak',
    'a'.repeat(161),
    '1234567890abcdef1234567890abcdef',
    '12345678-90ab-cdef-1234-567890abcdef',
  ])('refuses API slug %s at every link position', (slug) => {
    expect(coursePath('en', slug)).toBeNull();
    expect(lessonPath('en', slug, 'intro')).toBeNull();
    expect(lessonPath('en', 'foundation', slug)).toBeNull();
  });

  it('accepts the full storage width for a non-UUID slug', () => {
    const slug = 'z'.repeat(160);
    expect(coursePath('en', slug)).toBe(`/en/courses/${slug}`);
  });
});

describe.each([
  { base: '/tr/courses', outline: false, cursor: 'cursor', limit: 'limit' },
  {
    base: '/tr/courses/temel',
    outline: true,
    cursor: 'lessonCursor',
    limit: 'lessonLimit',
  },
])('pagination links for $base', ({ base, outline, cursor, limit }) => {
  it('preserves normalized limit and replaces the current cursor without carrying foreign data', () => {
    const pagination = publicPagination(
      `${base}?${cursor}=previous_private&${limit}=0007&locale=en&next=https://evil.example&other=1`,
      outline,
    );
    expect(pagination).not.toBeNull();
    const next = paginationPath(base, pagination!, 'opaque_NEXT-1', outline);
    expect(next).toBe(`${base}?${limit}=7&${cursor}=opaque_NEXT-1`);
    expect(next).not.toMatch(/previous|private|locale|evil|other/);
    expect(paginationPath(base, pagination!, null, outline)).toBe(`${base}?${limit}=7`);
  });

  it('is independent of input percent encoding and never decodes cursor contents', () => {
    const encoded = publicPagination(`${base}?${cursor}=%41%5F%2D&${limit}=%30%30%32`, outline);
    const plain = publicPagination(`${base}?${cursor}=A_-&${limit}=2`, outline);
    expect(encoded).toEqual(plain);
    // A is not a serialized API cursor, but the frontend treats it as opaque.
    expect(paginationPath(base, encoded!, 'A', outline)).toBe(`${base}?${limit}=2&${cursor}=A`);
    expect(paginationPath(base, plain!, 'A', outline)).toBe(`${base}?${limit}=2&${cursor}=A`);
  });

  it.each(['', '%41', 'a/b', 'a+b', 'a=', 'a.b', 'a?b=1', 'a#b', 'a\nb', 'a'.repeat(1025)])(
    'refuses an invalid API next cursor %s',
    (token) => {
      expect(paginationPath(base, { limit: '20', isPaginated: false }, token, outline)).toBeNull();
    },
  );

  it.each(['0', '01', '101', '1.5', '20&next=https://evil.example', ' 20'])(
    'refuses an unnormalized limit %s',
    (value) => {
      expect(paginationPath(base, { limit: value, isPaginated: false }, null, outline)).toBeNull();
    },
  );

  it.each([
    'https://evil.example',
    '//evil.example',
    '/en/courses/../other',
    '/en/status/not-found',
    '/en/courses?foreign=1',
    '/en/courses#fragment',
    '/EN/courses',
  ])('refuses an unsafe or non-content base %s', (path) => {
    expect(paginationPath(path, { limit: '20', isPaginated: true }, null, outline)).toBeNull();
  });

  it('refuses the other pagination surface and lesson paths', () => {
    expect(paginationPath(base, { limit: '20', isPaginated: false }, null, !outline)).toBeNull();
    expect(
      paginationPath(
        '/en/courses/foundation/lessons/intro',
        { limit: '20', isPaginated: false },
        null,
        outline,
      ),
    ).toBeNull();
  });
});
