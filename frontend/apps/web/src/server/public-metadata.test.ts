// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import { createPublicTranslator } from '@/i18n/catalogues';

import type { ConfiguredPublicClient } from './configured-public-client';
import { publicMetadata } from './public-metadata';
import type { PublicRequest } from './public-request';
import type { requirePublicResource } from './public-resource';
import { publicRoute } from './public-route';

type Resource = Awaited<ReturnType<typeof requirePublicResource>>;

function request(target = '/tr/courses', locale: string | null = 'tr'): PublicRequest {
  return {
    context: { host: 'school.example:9443', peer: '203.0.113.7', method: 'GET', target },
    client: {
      getSite: vi.fn<ConfiguredPublicClient['getSite']>(),
      getCourses: vi.fn<ConfiguredPublicClient['getCourses']>(),
      getCourse: vi.fn<ConfiguredPublicClient['getCourse']>(),
      getLesson: vi.fn<ConfiguredPublicClient['getLesson']>(),
    },
    site: {
      displayName: 'School',
      enabledLocales: ['tr', 'en', 'ar', 'zh-Hans-CN'],
      defaultLocale: 'tr',
      showPlatformAttribution: true,
      theme: null,
    },
    locale,
    route: publicRoute(target),
  };
}

function catalog(): Extract<Resource, { kind: 'catalog' }> {
  return {
    kind: 'catalog',
    request: request(),
    pagination: { limit: '20', isPaginated: false },
    data: {
      locale: 'tr',
      items: [],
      pageInfo: { hasNext: false, hasPrevious: false, nextCursor: null, previousCursor: null },
    },
  };
}

function course(): Extract<Resource, { kind: 'course' }> {
  return {
    kind: 'course',
    request: request('/tr/courses/temel'),
    pagination: { limit: '20', isPaginated: false },
    data: {
      locale: 'tr',
      course: {
        slug: 'temel',
        title: 'Temel kursu',
        summary: 'Derslere giriş.',
        contentAccess: 'public',
        level: null,
      },
      lessons: null,
      alternates: [
        { locale: 'tr', slug: 'temel' },
        { locale: 'en', slug: 'foundation' },
      ],
    },
  };
}

const ui = createPublicTranslator('tr');

describe('public metadata projection', () => {
  it('gives an empty first catalog an indexable localized title and every enabled locale', () => {
    expect(publicMetadata(catalog(), ui)).toEqual({
      title: ui.t('catalog.title'),
      robots: { index: true, follow: true },
      alternates: {
        canonical: 'https://school.example:3000/tr/courses',
        languages: {
          tr: 'https://school.example:3000/tr/courses',
          en: 'https://school.example:3000/en/courses',
          ar: 'https://school.example:3000/ar/courses',
          'zh-Hans-CN': 'https://school.example:3000/zh-Hans-CN/courses',
        },
      },
      openGraph: {
        type: 'website',
        title: ui.t('catalog.title'),
        siteName: 'School',
        url: 'https://school.example:3000/tr/courses',
        locale: 'tr',
        alternateLocale: ['en', 'ar', 'zh-Hans-CN'],
      },
    });
  });

  it('uses actual eligible translated slugs and does not invent enabled resource translations', () => {
    const metadata = publicMetadata(course(), ui);
    expect(metadata.title).toBe('Temel kursu');
    expect(metadata.description).toBe('Derslere giriş.');
    expect(metadata.alternates).toEqual({
      canonical: 'https://school.example:3000/tr/courses/temel',
      languages: {
        tr: 'https://school.example:3000/tr/courses/temel',
        en: 'https://school.example:3000/en/courses/foundation',
      },
    });
    expect(metadata.openGraph).toMatchObject({ locale: 'tr', alternateLocale: ['en'] });
    expect(JSON.stringify(metadata)).not.toContain('/en/courses/temel');
    expect(JSON.stringify(metadata)).not.toContain('/ar/');
  });

  it.each([catalog, course])(
    'uses the cursor-free first canonical and noindex for pagination',
    (create) => {
      const resource = create();
      const paginated = {
        ...resource,
        pagination: { cursor: 'private_cursor', limit: '7', isPaginated: true },
        request: {
          ...resource.request,
          context: {
            ...resource.request.context,
            target: `${resource.request.context.target}?cursor=private_cursor&lessonCursor=private_cursor&limit=7&locale=en&host=evil.example`,
          },
        },
      };
      const metadata = publicMetadata(paginated, ui);
      expect(metadata.robots).toEqual({ index: false, follow: true });
      expect(metadata.alternates?.canonical).toEqual(
        publicMetadata(resource, ui).alternates?.canonical,
      );
      expect(JSON.stringify(metadata)).not.toMatch(/private_cursor|evil|\?/);
    },
  );

  it('keeps restricted course marketing indexable without an outline', () => {
    const resource = course();
    resource.data.course.contentAccess = 'enrollment_required';
    expect(publicMetadata(resource, ui)).toMatchObject({
      title: 'Temel kursu',
      robots: { index: true },
    });
  });

  it.each([null, '', '   '])(
    'omits absent optional summary %s in both metadata surfaces',
    (summary) => {
      const resource = course();
      resource.data.course.summary = summary;
      const metadata = publicMetadata(resource, ui);
      expect(metadata).not.toHaveProperty('description');
      expect(metadata.openGraph).not.toHaveProperty('description');
    },
  );

  it('retains authored strings as data without treating their contents as URLs', () => {
    const resource = course();
    resource.data.course.title = '<script>alert(1)</script>';
    resource.data.course.summary = 'https://foreign.example/<b>summary</b>';
    const metadata = publicMetadata(resource, ui);
    expect(metadata.title).toBe(resource.data.course.title);
    expect(metadata.description).toBe(resource.data.course.summary);
    expect(metadata.alternates?.canonical).toBe('https://school.example:3000/tr/courses/temel');
  });

  it('uses admitted content locale, independently of UI fallback and payload locale', () => {
    const resource = catalog();
    const metadata = publicMetadata(
      { ...resource, request: request('/ar/courses?locale=en', 'ar') },
      createPublicTranslator('ar'),
    );
    expect(metadata.title).toBe('Courses');
    expect(metadata.openGraph).toMatchObject({ locale: 'ar' });
    expect(metadata.alternates?.canonical).toBe('https://school.example:3000/ar/courses');
  });

  it('emits no extra API reads, even on repeated metadata projections', () => {
    const resource = course();
    publicMetadata(resource, ui);
    publicMetadata(resource, ui);
    for (const operation of Object.values(resource.request.client))
      expect(operation).not.toHaveBeenCalled();
  });
});

describe('metadata rejects unsafe URL inputs', () => {
  it.each([
    'https://evil.example',
    '//evil.example',
    'school.example@evil.example',
    'school.example/path',
    'school.example?host=evil',
    'school.example\n',
    'School.example',
    'school.example.',
  ])('omits every URL for unsafe or noncanonical verified-host value %s', (host) => {
    const resource = course();
    const metadata = publicMetadata(
      {
        ...resource,
        request: { ...resource.request, context: { ...resource.request.context, host } },
      },
      ui,
    );
    expect(metadata.robots).toEqual({ index: false, follow: false });
    expect(metadata).not.toHaveProperty('alternates');
    expect(metadata).not.toHaveProperty('openGraph');
  });

  it.each(['school.example', 'school.example:443', '[::1]:9443'])(
    'uses the fixed HTTPS listener on canonical host %s',
    (host) => {
      const resource = catalog();
      const metadata = publicMetadata(
        {
          ...resource,
          request: { ...resource.request, context: { ...resource.request.context, host } },
        },
        ui,
      );
      expect(metadata.alternates?.canonical).toBe(
        `https://${host.startsWith('[') ? '[::1]' : 'school.example'}:3000/tr/courses`,
      );
    },
  );

  it.each([
    '../private',
    '//evil.example',
    'https://evil.example',
    '%74emel',
    'temel?secret=1',
    'temel#secret',
    '1234567890abcdef1234567890abcdef',
  ])('fails closed when current API slug %s is not admitted route data', (slug) => {
    const resource = course();
    resource.data.course.slug = slug;
    const metadata = publicMetadata(resource, ui);
    expect(metadata).not.toHaveProperty('alternates');
    expect(metadata).not.toHaveProperty('openGraph');
    expect(metadata.robots).toEqual({ index: false, follow: false });
  });

  it.each([null, 'TR', '../tr', 'fr'])(
    'fails closed for noncanonical or disabled content locale %s',
    (locale) => {
      const resource = catalog();
      const metadata = publicMetadata(
        { ...resource, request: { ...resource.request, locale } },
        ui,
      );
      expect(metadata).not.toHaveProperty('alternates');
      expect(metadata.robots).toEqual({ index: false, follow: false });
    },
  );

  it('omits unsafe, noncanonical, disabled and unroutable alternate entries', () => {
    const resource = course();
    resource.data.alternates.push(
      { locale: 'ar', slug: '../private' },
      { locale: 'EN', slug: 'foundation' },
      { locale: 'fr', slug: 'fondation' },
      { locale: '//evil.example', slug: 'course' },
      { locale: 'zh-Hans-CN', slug: '%66oundation' },
    );
    expect(publicMetadata(resource, ui).alternates).toEqual(
      publicMetadata(course(), ui).alternates,
    );
    const initial = catalog();
    const corrupted = {
      ...initial,
      request: {
        ...initial.request,
        site: { ...initial.request.site, enabledLocales: ['tr', 'EN', '../en', 'en?x=1'] },
      },
    };
    expect(publicMetadata(corrupted, ui).alternates?.languages).toEqual({
      tr: 'https://school.example:3000/tr/courses',
    });
  });
});

describe('controlled noindex metadata states', () => {
  it.each(['invalid_cursor', 'rate_limited', 'unavailable'] as const)(
    'localizes %s without original route details or resource alternates',
    (state) => {
      const resource: Resource = {
        kind: 'failure',
        request: request('/tr/courses/private-slug?cursor=private_token'),
        state,
      };
      const metadata = publicMetadata(resource, ui);
      expect(metadata).toEqual({
        title: ui.t(`page.${state}.title`),
        description: ui.t(`page.${state}.description`),
        robots: { index: false, follow: false },
      });
      expect(JSON.stringify(metadata)).not.toMatch(/private|lockey_|https:/);
    },
  );

  it('localizes the fixed missing state without resource alternates', () => {
    expect(
      publicMetadata({ kind: 'status', request: request('/tr/status/not-found') }, ui),
    ).toEqual({
      title: ui.t('page.missing.title'),
      description: ui.t('page.missing.description'),
      robots: { index: false, follow: false },
    });
  });

  it('keeps scaffold and pending lesson implementation noindex', () => {
    const resources: Resource[] = [
      { kind: 'scaffold', request: request('/studio', null) },
      {
        kind: 'lesson',
        request: request('/tr/courses/temel/lessons/giris'),
        data: {
          locale: 'tr',
          course: { slug: 'temel', title: 'Temel' },
          lesson: { slug: 'giris', title: 'Giriş' },
          content: { state: 'unavailable' },
          alternates: [],
        },
      },
    ];
    for (const resource of resources) {
      expect(publicMetadata(resource, ui)).toEqual({
        title: 'School',
        robots: { index: false, follow: false },
      });
    }
  });
});
