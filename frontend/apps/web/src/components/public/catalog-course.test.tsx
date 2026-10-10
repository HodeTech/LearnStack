import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { createPublicTranslator } from '@/i18n/catalogues';
import type { ConfiguredPublicClient } from '@/server/configured-public-client';
import type { PublicRequest } from '@/server/public-request';
import type { PublicResource } from '@/server/public-resource';
import { publicPagination, publicRoute } from '@/server/public-route';

import { PublicCatalog } from './catalog';
import { PublicCourse } from './course';

type CatalogResource = Extract<PublicResource, { kind: 'catalog' }>;
type CourseResource = Extract<PublicResource, { kind: 'course' }>;

const firstPage = {
  hasNext: false,
  hasPrevious: false,
  nextCursor: null,
  previousCursor: null,
};
const courseSummary: CourseResource['data']['course'] = {
  slug: 'foundation',
  title: 'Authored course title',
  summary: 'Authored course summary',
  level: null,
  contentAccess: 'public',
};
const client = {
  getSite: vi.fn<ConfiguredPublicClient['getSite']>(),
  getCourses: vi.fn<ConfiguredPublicClient['getCourses']>(),
  getCourse: vi.fn<ConfiguredPublicClient['getCourse']>(),
  getLesson: vi.fn<ConfiguredPublicClient['getLesson']>(),
};

function request(locale: string, target: string): PublicRequest {
  return {
    context: { host: 'institution.example:3000', peer: '203.0.113.7', method: 'GET', target },
    client,
    site: {
      displayName: 'Institution',
      defaultLocale: locale,
      enabledLocales: [locale],
      showPlatformAttribution: false,
      theme: null,
    },
    locale,
    route: publicRoute(target),
  };
}

function catalog(
  overrides: Partial<CatalogResource['data']> = {},
  target?: string,
): CatalogResource {
  const data = { locale: 'tr-TR', items: [courseSummary], pageInfo: firstPage, ...overrides };
  const signedTarget = target ?? `/${data.locale}/courses`;
  const pagination = publicPagination(signedTarget, false);
  if (!pagination) throw new Error('Invalid test pagination');
  return { kind: 'catalog', data, request: request(data.locale, signedTarget), pagination };
}

function course(overrides: Partial<CourseResource['data']> = {}, target?: string): CourseResource {
  const data: CourseResource['data'] = {
    locale: 'tr-TR',
    course: courseSummary,
    alternates: [],
    lessons: {
      items: [
        { slug: 'first', title: 'First authored lesson', sort: 7 },
        { slug: 'second', title: 'Second authored lesson', sort: 12 },
      ],
      pageInfo: firstPage,
    },
    ...overrides,
  };
  const signedTarget = target ?? `/${data.locale}/courses/foundation`;
  const pagination = publicPagination(signedTarget, true);
  if (!pagination) throw new Error('Invalid test pagination');
  return { kind: 'course', data, request: request(data.locale, signedTarget), pagination };
}

describe('public catalog and course semantics', () => {
  it('renders a semantic catalog with one h1, ordered h2 cards and ordinary course anchors', () => {
    const resource = catalog({
      items: [courseSummary, { ...courseSummary, slug: 'another', title: 'Another course' }],
    });
    render(<PublicCatalog resource={resource} ui={createPublicTranslator('tr-TR')} />);
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Kurslar');
    expect(screen.getByText('Bu sayfada 2 kurs')).toHaveAttribute('lang', 'tr');
    const list = screen.getByRole('list');
    expect(list.tagName).toBe('UL');
    expect(
      within(list)
        .getAllByRole('heading', { level: 2 })
        .map((node) => node.textContent),
    ).toEqual(['Authored course title', 'Another course']);
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByRole('link', { name: 'Authored course title' })).toHaveAttribute(
      'href',
      '/tr-TR/courses/foundation',
    );
    expect(screen.getByRole('link', { name: 'Another course' })).toHaveAttribute(
      'href',
      '/tr-TR/courses/another',
    );
  });

  it('renders an ordered outline in API order without using sort values as URLs or numbering', () => {
    const resource = course({
      lessons: {
        items: [
          { slug: 'first', title: 'First authored lesson', sort: 90 },
          { slug: 'second', title: 'Second authored lesson', sort: 3 },
        ],
        pageInfo: firstPage,
      },
    });
    render(<PublicCourse resource={resource} ui={createPublicTranslator('tr-TR')} />);
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(courseSummary.title);
    expect(screen.getAllByRole('heading', { level: 2 })).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('Kurs içeriği');
    expect(screen.getByText('Bu sayfada 2 ders')).toHaveAttribute('lang', 'tr');
    const list = screen.getByRole('list');
    expect(list.tagName).toBe('OL');
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((node) => node.textContent),
    ).toEqual(['First authored lesson', 'Second authored lesson']);
    expect(screen.getAllByRole('link').map((node) => node.getAttribute('href'))).toEqual([
      '/tr-TR/courses/foundation/lessons/first',
      '/tr-TR/courses/foundation/lessons/second',
    ]);
  });

  it.each(['catalog', 'course'] as const)(
    'keeps %s authored strings escaped and inert, with no summary linkification',
    (view) => {
      const title = '<script>alert("title")</script>';
      const summary =
        '<img src=x onerror=alert(1)> javascript:alert(2) data:text/html,secret https://example.invalid';
      const authored = { ...courseSummary, title, summary };
      const ui = createPublicTranslator('tr-TR');
      const { container } = render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ items: [authored] })} ui={ui} />
        ) : (
          <PublicCourse resource={course({ course: authored, lessons: null })} ui={ui} />
        ),
      );
      expect(screen.getByText(title)).toBeInTheDocument();
      const summaryNode = screen.getByText(summary);
      expect(summaryNode.tagName).toBe('P');
      expect(summaryNode.querySelector('a')).toBeNull();
      expect(container.querySelector('script, img, iframe, [onerror], [onclick]')).toBeNull();
      expect(
        container.querySelector('a[href^="javascript:"], a[href^="data:"], a[href^="https:"]'),
      ).toBeNull();
    },
  );

  it.each([
    ['catalog', null],
    ['catalog', ''],
    ['catalog', ' \n\t '],
    ['course', null],
    ['course', ''],
    ['course', ' \n\t '],
  ] as const)('omits empty optional summary and null level in the %s view: %j', (view, summary) => {
    const authored = { ...courseSummary, summary, level: null };
    const ui = createPublicTranslator('en');
    const { container } = render(
      view === 'catalog' ? (
        <PublicCatalog resource={catalog({ items: [authored] })} ui={ui} />
      ) : (
        <PublicCourse resource={course({ course: authored })} ui={ui} />
      ),
    );
    expect(container.querySelector('.public-summary')).toBeNull();
    expect(screen.queryByText('Level unavailable')).toBeNull();
    expect(container.querySelectorAll('p')).toHaveLength(1);
  });

  it('distinguishes the empty catalog from an empty public outline and restricted outline', () => {
    const ui = createPublicTranslator('en');
    const { rerender } = render(<PublicCatalog resource={catalog({ items: [] })} ui={ui} />);
    expect(screen.getByText('No courses are available in this language yet.')).toBeInTheDocument();
    expect(screen.queryByRole('list')).toBeNull();
    rerender(
      <PublicCourse resource={course({ lessons: { items: [], pageInfo: firstPage } })} ui={ui} />,
    );
    expect(
      screen.getByText('No public lessons are available in this course yet.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('No courses are available in this language yet.')).toBeNull();
    expect(screen.queryByText('The lessons in this course are not publicly available.')).toBeNull();
    expect(screen.queryByRole('list')).toBeNull();
    expect(screen.queryByText('0 lessons on this page')).toBeNull();
  });

  it('renders restricted marketing and a translated notice without inspecting a supplied outline', () => {
    const resource = course(
      { course: { ...courseSummary, contentAccess: 'enrollment_required' } },
      '/tr-TR/courses/foundation?lessonCursor=opaque&lessonLimit=20',
    );
    const readLessons = vi.fn(() => {
      throw new Error('Restricted outline was inspected');
    });
    Object.defineProperty(resource.data, 'lessons', { get: readLessons });
    const { container } = render(
      <PublicCourse resource={resource} ui={createPublicTranslator('tr-TR')} />,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(courseSummary.title);
    expect(screen.getByText('Authored course summary')).toBeInTheDocument();
    expect(screen.getByText('Bu kursun dersleri herkese açık değildir.')).toHaveAttribute(
      'lang',
      'tr',
    );
    expect(readLessons).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading', { level: 2 })).toBeNull();
    expect(screen.queryByRole('list')).toBeNull();
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
    expect(container.textContent).not.toMatch(
      /First authored|Second authored|2 ders|body|purchase|login|enroll/i,
    );
  });
});

describe('public UI, authored content and resolved label languages', () => {
  it.each(['catalog', 'course'] as const)(
    'uses the admitted locale for %s navigation even when response data names another locale',
    (view) => {
      const ui = createPublicTranslator('tr-TR');
      const pageInfo = { ...firstPage, hasNext: true, nextCursor: 'opaque_next' };
      const responseResource =
        view === 'catalog'
          ? catalog({ locale: 'ar', pageInfo })
          : course({
              locale: 'ar',
              lessons: { items: [{ slug: 'first', title: 'Lesson', sort: 0 }], pageInfo },
            });
      const path = view === 'catalog' ? '/tr-TR/courses' : '/tr-TR/courses/foundation';
      const resource = { ...responseResource, request: request('tr-TR', path) };
      const { container } = render(
        resource.kind === 'catalog' ? (
          <PublicCatalog resource={resource} ui={ui} />
        ) : (
          <PublicCourse resource={resource} ui={ui} />
        ),
      );
      expect(container.firstElementChild).toHaveAttribute('lang', 'ar');
      for (const link of screen.getAllByRole('link')) {
        expect(link.getAttribute('href')).toMatch(/^\/tr-TR\/courses/);
      }
    },
  );

  it.each(['catalog', 'course'] as const)(
    'keeps RTL %s content separate from English UI fallback and the API label locale',
    (view) => {
      const authored = {
        ...courseSummary,
        title: 'عنوان الدورة',
        summary: 'وصف الدورة',
        level: { state: 'ready' as const, label: { locale: 'fa', value: 'سطح مقدماتی' } },
      };
      const ui = createPublicTranslator('ar');
      const { container } = render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ locale: 'ar', items: [authored] })} ui={ui} />
        ) : (
          <PublicCourse resource={course({ locale: 'ar', course: authored })} ui={ui} />
        ),
      );
      const root = container.firstElementChild;
      expect(root).toHaveAttribute('lang', 'ar');
      expect(root).toHaveAttribute('dir', 'rtl');
      expect(screen.getByText(authored.summary).closest('[lang]')).toBe(root);
      const uiHeading = screen.getByRole('heading', { level: view === 'catalog' ? 1 : 2 });
      expect(uiHeading).toHaveAttribute('lang', 'en');
      expect(uiHeading).toHaveAttribute('dir', 'ltr');
      expect(screen.getByText('سطح مقدماتی')).toHaveAttribute('lang', 'fa');
      expect(screen.getByText('سطح مقدماتی')).toHaveAttribute('dir', 'rtl');
      expect(screen.getAllByRole('link')[0]?.getAttribute('href')).toMatch(/^\/ar\/courses\//);
    },
  );

  it.each(['catalog', 'course'] as const)(
    'uses translated unavailable-level UI copy for %s without exposing a stale label',
    (view) => {
      const authored = { ...courseSummary, level: { state: 'unavailable' as const, label: null } };
      const ui = createPublicTranslator('tr-TR');
      render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ items: [authored] })} ui={ui} />
        ) : (
          <PublicCourse resource={course({ course: authored })} ui={ui} />
        ),
      );
      const label = screen.getByText('Seviye bilgisi mevcut değil');
      expect(label).toHaveAttribute('lang', 'tr');
      expect(label).toHaveAttribute('dir', 'ltr');
    },
  );
});

describe('opaque public pagination and invalid navigation targets', () => {
  it.each([
    ['catalog', undefined, '20'],
    ['catalog', '900', '100'],
    ['course', undefined, '20'],
    ['course', '900', '100'],
  ] as const)(
    'renders only owned %s pagination with requested limit %s normalized to %s',
    (view, limit, expected) => {
      const token = 'eyJvcGFxdWUiOiJ1bmRlY29kZWQifQ_-';
      // Inconsistent previous data cannot cause fabricated backward navigation.
      const pageInfo = {
        hasNext: true,
        nextCursor: token,
        hasPrevious: true,
        previousCursor: 'do_not_render',
      };
      const query =
        view === 'catalog'
          ? `cursor=previous_opaque&lessonCursor=unowned&lessonLimit=1${limit ? `&limit=${limit}` : ''}`
          : `lessonCursor=previous_opaque&cursor=unowned&limit=1${limit ? `&lessonLimit=${limit}` : ''}`;
      const ui = createPublicTranslator('en');
      render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ pageInfo }, `/tr-TR/courses?${query}`)} ui={ui} />
        ) : (
          <PublicCourse
            resource={course(
              { lessons: { items: [], pageInfo } },
              `/tr-TR/courses/foundation?${query}`,
            )}
            ui={ui}
          />
        ),
      );
      const next = screen.getByRole('link', {
        name: view === 'catalog' ? 'Next courses' : 'Next lessons',
      });
      const restart = screen.getByRole('link', {
        name: view === 'catalog' ? 'Back to the first courses' : 'Back to the first lessons',
      });
      const base = view === 'catalog' ? '/tr-TR/courses' : '/tr-TR/courses/foundation';
      const limitName = view === 'catalog' ? 'limit' : 'lessonLimit';
      const cursorName = view === 'catalog' ? 'cursor' : 'lessonCursor';
      expect(next).toHaveAttribute(
        'href',
        `${base}?${limitName}=${expected}&${cursorName}=${token}`,
      );
      expect(restart).toHaveAttribute('href', `${base}?${limitName}=${expected}`);
      expect(screen.queryByRole('link', { name: /previous/i })).toBeNull();
      expect(screen.getByRole('navigation')).toHaveAttribute('lang', 'en');
      expect(screen.getByRole('navigation')).toHaveAttribute('dir', 'ltr');
      for (const node of [next, restart]) {
        expect(node.getAttribute('href')).not.toMatch(/do_not_render|unowned|previous_opaque/);
      }
    },
  );

  it.each(['catalog', 'course'] as const)(
    'omits %s restart on the first page and next without hasNext',
    (view) => {
      const pageInfo = { ...firstPage, nextCursor: 'opaque_but_not_next' };
      const ui = createPublicTranslator('en');
      render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ items: [], pageInfo })} ui={ui} />
        ) : (
          <PublicCourse resource={course({ lessons: { items: [], pageInfo } })} ui={ui} />
        ),
      );
      expect(screen.queryByRole('navigation')).toBeNull();
      expect(screen.queryByRole('link')).toBeNull();
    },
  );

  it.each(['catalog', 'course'] as const)(
    'keeps only the %s restart link on a final continuation page',
    (view) => {
      const ui = createPublicTranslator('en');
      render(
        view === 'catalog' ? (
          <PublicCatalog
            resource={catalog({ items: [] }, '/tr-TR/courses?cursor=opaque_final')}
            ui={ui}
          />
        ) : (
          <PublicCourse
            resource={course(
              { lessons: { items: [], pageInfo: firstPage } },
              '/tr-TR/courses/foundation?lessonCursor=opaque_final',
            )}
            ui={ui}
          />
        ),
      );
      const links = screen.getAllByRole('link');
      expect(links).toHaveLength(1);
      expect(links[0]).toHaveTextContent(
        view === 'catalog' ? 'Back to the first courses' : 'Back to the first lessons',
      );
      expect(links[0]).toHaveAttribute(
        'href',
        view === 'catalog' ? '/tr-TR/courses?limit=20' : '/tr-TR/courses/foundation?lessonLimit=20',
      );
    },
  );

  it.each([
    'javascript:alert(1)',
    'data:text/html,secret',
    'https://example.invalid',
    'bad/slug',
    '%66oundation',
    '',
  ])('keeps invalid course and lesson slug %j inert', (slug) => {
    const ui = createPublicTranslator('en');
    const { rerender } = render(
      <PublicCatalog resource={catalog({ items: [{ ...courseSummary, slug }] })} ui={ui} />,
    );
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent(courseSummary.title);
    expect(screen.queryByRole('link')).toBeNull();
    rerender(
      <PublicCourse
        resource={course({
          lessons: { items: [{ slug, title: 'Inert lesson label', sort: 0 }], pageInfo: firstPage },
        })}
        ui={ui}
      />,
    );
    expect(screen.getByText('Inert lesson label')).toBeInTheDocument();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it.each(['catalog', 'course'] as const)(
    'omits an invalid opaque %s next target without echoing it',
    (view) => {
      const pageInfo = {
        ...firstPage,
        hasNext: true,
        nextCursor: 'https://example.invalid/private',
      };
      const ui = createPublicTranslator('en');
      const { container } = render(
        view === 'catalog' ? (
          <PublicCatalog resource={catalog({ items: [], pageInfo })} ui={ui} />
        ) : (
          <PublicCourse resource={course({ lessons: { items: [], pageInfo } })} ui={ui} />
        ),
      );
      expect(screen.queryByRole('link')).toBeNull();
      expect(container.textContent).not.toContain('https://example.invalid/private');
    },
  );

  it('keeps outline labels and pagination inert when the returned course slug is invalid', () => {
    const title = '<script>alert(1)</script> javascript:alert(2)';
    const resource = course(
      {
        course: { ...courseSummary, slug: 'invalid/course' },
        lessons: {
          items: [{ slug: 'first', title, sort: 0 }],
          pageInfo: { ...firstPage, hasNext: true, nextCursor: 'opaque_next' },
        },
      },
      '/tr-TR/courses/foundation?lessonCursor=opaque_current',
    );
    const { container } = render(
      <PublicCourse resource={resource} ui={createPublicTranslator('en')} />,
    );
    expect(screen.getByText(title).tagName).toBe('LI');
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.queryByRole('navigation')).toBeNull();
    expect(container.querySelector('script, [onclick]')).toBeNull();
  });
});
