// @vitest-environment node
import { describe, expect, it } from 'vitest';

import { canonicalLocale, canonicalRouteLocale, publicEntry, publicRedirect } from './public-entry';
import type { PublicSite } from './public-entry';

function site(enabledLocales = ['tr', 'en', 'zh-Hans-CN'], defaultLocale = 'tr'): PublicSite {
  return {
    displayName: 'Institution',
    enabledLocales,
    defaultLocale,
    theme: null,
    showPlatformAttribution: true,
  };
}

describe('public locale grammar and route-only identity', () => {
  it.each([
    ['TR', 'tr'],
    ['en-us', 'en-US'],
    ['ZH-hans-cn', 'zh-Hans-CN'],
    ['de-1996', 'de-1996'],
    ['qwerty', 'qwerty'],
    ['courses', 'courses'],
    ['studio', 'studio'],
    ['portal', 'portal'],
    ['en_US', null],
    ['e', null],
    [' en', null],
    ['en\n', null],
    ['en-1234', 'en-1234'],
    ['en-abc', null],
    ['a'.repeat(36), null],
    ['abcde-abcde-abcde-abcde-abcde-abcde', 'abcde-abcde-abcde-abcde-abcde-abcde'],
    ['abcdef-abcde-abcde-abcde-abcde-abcde', null],
    ['tr%2dTR', null],
    ['tr-TR-x', null],
  ])('canonicalizes grammar rather than a fixed language registry: %s', (input, canonical) => {
    expect(canonicalLocale(input!)).toBe(canonical);
  });

  it.each([
    ['/tr/courses?locale=en', 'tr'],
    ['/zh-Hans-CN/courses/foundation', 'zh-Hans-CN'],
    ['/courses/courses', 'courses'],
    ['/studio/courses/foundation/lessons/intro', 'studio'],
    ['/portal/courses', 'portal'],
    ['/TR/courses', null],
    ['/tr', null],
    ['/courses', null],
    ['/studio', null],
    ['/%74r/courses', null],
    ['/tr/unknown', null],
    ['/tr/courses/foundation/lessons', null],
    ['/tr/courses/foundation/lessons/intro/extra', null],
    ['/tr/courses/../foundation', null],
    ['/tr/courses/%2fadmin', null],
    ['/tr//courses', null],
  ])(
    'derives Education locale exclusively from a supported canonical route: %s',
    (target, locale) => {
      expect(canonicalRouteLocale(target!)).toBe(locale);
    },
  );
});

describe('membership-first public entry', () => {
  it.each([
    ['/', 307, '/tr/courses'],
    ['/courses', 307, '/tr/courses'],
    ['/courses/foundation', 307, '/tr/courses/foundation'],
    ['/courses/foundation/lessons/intro', 307, '/tr/courses/foundation/lessons/intro'],
    ['/en', 307, '/en/courses'],
    ['/en/', 307, '/en/courses'],
    ['/TR/courses', 308, '/tr/courses'],
    ['/zh-hans-cn/courses/foundation', 308, '/zh-Hans-CN/courses/foundation'],
    ['/ZH-Hans-cn/', 308, '/zh-Hans-CN/'],
  ])('redirects %s with the configured non-English default', (target, status, path) => {
    expect(publicEntry(String(target), site())).toEqual({ kind: 'redirect', status, path });
  });

  it.each([
    '/tr/courses',
    '/en/courses/foundation',
    '/tr/courses/foundation/lessons/intro',
    '/tr/courses/%66oundation',
  ])('continues only supported content: %s', (target) => {
    expect(publicEntry(target, site())).toEqual({
      kind: 'continue',
      locale: target.startsWith('/en') ? 'en' : 'tr',
    });
  });

  it.each(['/studio', '/portal'])('continues the exact live-host scaffold only: %s', (target) => {
    expect(publicEntry(target, site())).toEqual({ kind: 'continue', locale: null });
  });

  it.each([
    '/fr/courses',
    '/en_US/courses',
    '/en-US/courses',
    '/%74r/courses',
    '/en%2dUS/courses',
    '/studio/admin',
    '/portal/account',
    '/studio/',
    '/portal/',
    '/api/arbitrary',
    '/courses//foundation',
    '/tr/courses/',
    '/tr/unknown',
    '/tr/courses/foundation/other',
    '/tr/courses/foundation/lessons/intro/extra',
    '/tr/courses/UPPER',
    '/tr/courses/550e8400-e29b-41d4-a716-446655440000',
    '/tr/courses/%2e%2e',
    '/tr/../courses',
    '//evil.example/tr/courses',
    '/tr/courses/%5cadmin',
    '/' + 'a'.repeat(36) + '/courses',
  ])('refuses disabled, malformed, ambiguous or unknown paths: %s', (target) => {
    expect(publicEntry(target, site())).toEqual({ kind: 'refuse', status: 404 });
  });

  it.each(['courses', 'studio', 'portal'])(
    'gives enabled %s membership priority when default and nondefault',
    (locale) => {
      for (const defaultLocale of [locale, 'tr']) {
        const configuration = site(['tr', locale], defaultLocale);
        expect(publicEntry(`/${locale}`, configuration)).toEqual({
          kind: 'redirect',
          status: 307,
          path: `/${locale}/courses`,
        });
        expect(publicEntry(`/${locale}/`, configuration)).toEqual({
          kind: 'redirect',
          status: 307,
          path: `/${locale}/courses`,
        });
        expect(publicEntry(`/${locale}/courses`, configuration)).toEqual({
          kind: 'continue',
          locale,
        });
        expect(publicEntry(`/${locale}/courses/foundation`, configuration)).toEqual({
          kind: 'continue',
          locale,
        });
        expect(publicEntry(`/${locale}/courses/foundation/lessons/intro`, configuration)).toEqual({
          kind: 'continue',
          locale,
        });
        expect(publicEntry(`/${locale.toUpperCase()}/courses`, configuration)).toEqual({
          kind: 'redirect',
          status: 308,
          path: `/${locale}/courses`,
        });
        expect(publicEntry('/', configuration)).toEqual({
          kind: 'redirect',
          status: 307,
          path: `/${defaultLocale}/courses`,
        });
        // A locale called courses intentionally removes the unprefixed course shorthand.
        if (locale === 'courses')
          expect(publicEntry('/courses/foundation', configuration)).toEqual({
            kind: 'refuse',
            status: 404,
          });
      }
    },
  );

  it.each([
    '?',
    '?x=%20',
    '?x=~&x=%2f',
    '?x',
    '?x=%41',
    '?_rsc=raw',
    '?locale=en&next=https://evil.example',
  ])('preserves authenticated raw query bytes as inert data: %s', (query) => {
    expect(publicEntry('/courses' + query, site())).toEqual({
      kind: 'redirect',
      status: 307,
      path: '/tr/courses' + query,
    });
    expect(publicRedirect('tenant.example:9876', '/tr/courses' + query)).toBe(
      'https://tenant.example:3000/tr/courses' + query,
    );
  });

  it('refuses valid empty locale configuration separately from malformed stored configuration', () => {
    expect(publicEntry('/', site([], 'tr'))).toEqual({ kind: 'refuse', status: 404 });
    for (const configuration of [
      site(['en'], 'tr'),
      site(['tr', 'tr']),
      site(['TR'], 'TR'),
      site(['tr\n'], 'tr\n'),
    ])
      expect(publicEntry('/', configuration)).toEqual({ kind: 'refuse', status: 503 });
  });

  it('uses only normalized captured authority and fixed HTTPS port', () => {
    expect(publicRedirect('[2001:db8::1]:8080', '/tr/courses')).toBe(
      'https://[2001:db8::1]:3000/tr/courses',
    );
    expect(() => publicRedirect('Tenant.Example', '/tr/courses')).toThrow();
    expect(() => publicRedirect('tenant.example', '//evil.example/tr/courses')).toThrow();
  });
});
