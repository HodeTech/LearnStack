import { render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import LessonPage, {
  generateMetadata,
} from '@/app/(public)/[locale]/courses/[slug]/lessons/[lessonSlug]/page';
import CoursePage from '@/app/(public)/[locale]/courses/[slug]/page';
import { createPublicTranslator } from '@/i18n/catalogues';
import type { ConfiguredPublicClient } from '@/server/configured-public-client';
import type { PublicRequest } from '@/server/public-request';
import type { PublicResource } from '@/server/public-resource';
import { publicRoute } from '@/server/public-route';

import { PublicChrome } from './chrome';
import { PublicLesson } from './lesson';
import { PublicState } from './state';

const dependencies = vi.hoisted(() => ({
  resource: vi.fn(),
  ui: vi.fn(),
  metadata: vi.fn(),
  notFound: vi.fn(() => {
    throw new Error('Not found');
  }),
}));
vi.mock('@/server/public-resource', () => ({ requirePublicResource: dependencies.resource }));
vi.mock('@/server/public-ui', () => ({ getPublicUi: dependencies.ui }));
vi.mock('@/server/public-metadata', () => ({ publicMetadata: dependencies.metadata }));
vi.mock('next/navigation', () => ({ notFound: dependencies.notFound }));

type LessonResource = Extract<PublicResource, { kind: 'lesson' }>;
type ReadyContent = Extract<LessonResource['data']['content'], { state: 'ready' }>;
const client = {
  getSite: vi.fn<ConfiguredPublicClient['getSite']>(),
  getCourses: vi.fn<ConfiguredPublicClient['getCourses']>(),
  getCourse: vi.fn<ConfiguredPublicClient['getCourse']>(),
  getLesson: vi.fn<ConfiguredPublicClient['getLesson']>(),
};
const fields: ReadyContent['fields'] = [
  { name: 'z_internal', label: { locale: 'en', value: 'First label' }, value: 'First value' },
  { name: 'a_internal', label: { locale: 'tr', value: 'İkinci etiket' }, value: 'İkinci değer' },
];

function lesson(overrides: Partial<LessonResource['data']> = {}): LessonResource {
  const locale = overrides.locale ?? 'tr-TR';
  const target = `/${locale}/courses/foundation/lessons/first`;
  const request: PublicRequest = {
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
  return {
    kind: 'lesson',
    request,
    data: {
      locale,
      lesson: { slug: 'first', title: 'Authored lesson title' },
      course: { slug: 'foundation', title: 'Authored course title' },
      content: {
        state: 'ready',
        rendererKey: 'default-card',
        label: { locale: 'tr-TR', value: 'Authored content label' },
        fields,
      },
      alternates: [],
      ...overrides,
    },
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('public lesson presentation', () => {
  it('renders authored headings and ordered definition pairs without exposing field names', () => {
    const resource = lesson();
    const ui = createPublicTranslator('tr-TR');
    const { container } = render(<PublicLesson resource={resource} ui={ui} />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Authored lesson title');
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('Authored content label');
    expect(screen.getAllByRole('heading')).toHaveLength(2);
    const list = container.querySelector('dl');
    expect(list).not.toBeNull();
    expect(Array.from(list!.querySelectorAll('dt, dd'), (node) => node.textContent)).toEqual([
      'First label',
      'First value',
      'İkinci etiket',
      'İkinci değer',
    ]);
    expect(screen.getByText('First label')).toHaveAttribute('lang', 'en');
    expect(screen.getByText('İkinci etiket')).toHaveAttribute('lang', 'tr');
    expect(container.textContent).not.toMatch(/z_internal|a_internal/);
    expect(screen.getByRole('link')).toHaveAttribute('href', '/tr-TR/courses/foundation');
    expect(screen.getByRole('link')).toHaveTextContent('Authored course title');
    expect(container.querySelector('main')).toBeNull();
    for (const operation of Object.values(client)) expect(operation).not.toHaveBeenCalled();
  });

  it('preserves a present empty string while displaying only API-supplied optional fields', () => {
    const resource = lesson({
      content: {
        state: 'ready',
        rendererKey: 'default-card',
        label: { locale: 'en', value: 'Content' },
        fields: [
          { name: 'present_empty', label: { locale: 'en', value: 'Empty field' }, value: '' },
        ],
      },
    });
    const { container } = render(
      <PublicLesson resource={resource} ui={createPublicTranslator('en')} />,
    );
    expect(container.querySelectorAll('dt')).toHaveLength(1);
    expect(container.querySelectorAll('dd')).toHaveLength(1);
    expect(container.querySelector('dd')).toBeEmptyDOMElement();
    expect(screen.queryByText('This lesson has no content to display yet.')).toBeNull();
    expect(container.textContent).not.toContain('present_empty');
  });

  it.each([
    [
      'en',
      'This lesson has no content to display yet.',
      'This lesson content is currently unavailable.',
    ],
    [
      'tr-TR',
      'Bu derste henüz görüntülenecek içerik yok.',
      'Bu dersin içeriği şu anda kullanılamıyor.',
    ],
  ])(
    'distinguishes empty ready content from unavailable content in %s UI',
    (locale, empty, unavailable) => {
      const ui = createPublicTranslator(locale);
      const resource = lesson({
        content: {
          state: 'ready',
          rendererKey: 'default-card',
          label: { locale: 'en', value: 'Content' },
          fields: [],
        },
      });
      const { container, rerender } = render(<PublicLesson resource={resource} ui={ui} />);
      expect(screen.getByText(empty)).toHaveAttribute('lang', ui.locale);
      expect(screen.getByText(empty)).toHaveAttribute('dir', ui.direction);
      expect(screen.queryByText(unavailable)).toBeNull();
      expect(container.querySelector('dl')).toBeNull();
      expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('Content');
      rerender(<PublicLesson resource={lesson({ content: { state: 'unavailable' } })} ui={ui} />);
      expect(screen.getByText(unavailable)).toHaveAttribute('lang', ui.locale);
      expect(screen.queryByText(empty)).toBeNull();
      expect(screen.queryByRole('heading', { level: 2 })).toBeNull();
      expect(container.querySelector('dl')).toBeNull();
    },
  );

  it.each(['unavailable', 'unknown'] as const)(
    'short-circuits %s content before reading labels or fields and emits no renderer diagnostics',
    (state) => {
      const content = { state: 'ready', rendererKey: 'future-private-renderer' } as ReadyContent;
      const readLabel = vi.fn(() => {
        throw new Error('Unsupported label was read');
      });
      const readFields = vi.fn(() => {
        throw new Error('Unsupported fields were read');
      });
      Object.defineProperty(content, 'label', { get: readLabel });
      Object.defineProperty(content, 'fields', { get: readFields });
      const unsupported =
        state === 'unavailable' ? { ...content, state: 'unavailable' as const } : content;
      Object.defineProperty(unsupported, 'label', { get: readLabel });
      Object.defineProperty(unsupported, 'fields', { get: readFields });
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
      const error = vi.spyOn(console, 'error').mockImplementation(() => {});
      const { container } = render(
        <PublicLesson
          resource={lesson({ content: unsupported })}
          ui={createPublicTranslator('en')}
        />,
      );
      expect(screen.getByText('This lesson content is currently unavailable.')).toBeInTheDocument();
      expect(container.textContent).not.toContain('future-private-renderer');
      expect(container.querySelector('dl')).toBeNull();
      expect(readLabel).not.toHaveBeenCalled();
      expect(readFields).not.toHaveBeenCalled();
      expect(warn).not.toHaveBeenCalled();
      expect(error).not.toHaveBeenCalled();
    },
  );

  it('keeps RTL content, resolved labels, and English UI fallback languages independent', () => {
    const resource = lesson({
      locale: 'ar',
      lesson: { slug: 'first', title: 'عنوان الدرس' },
      content: {
        state: 'ready',
        rendererKey: 'default-card',
        label: { locale: 'tr-TR', value: 'İçerik başlığı' },
        fields: [
          { name: 'internal', label: { locale: 'fa', value: 'برچسب' }, value: 'قيمة الدرس' },
        ],
      },
    });
    const ui = createPublicTranslator('ar');
    const { container, rerender } = render(<PublicLesson resource={resource} ui={ui} />);
    const article = screen.getByRole('article');
    expect(article).toHaveAttribute('lang', 'ar');
    expect(article).toHaveAttribute('dir', 'rtl');
    expect(screen.getByRole('heading', { level: 1 }).closest('[lang]')).toBe(article);
    expect(screen.getByRole('heading', { level: 2 })).toHaveAttribute('lang', 'tr-TR');
    expect(screen.getByRole('heading', { level: 2 })).toHaveAttribute('dir', 'ltr');
    expect(screen.getByText('برچسب')).toHaveAttribute('lang', 'fa');
    expect(screen.getByText('برچسب')).toHaveAttribute('dir', 'rtl');
    expect(container.querySelector('dd')?.closest('[lang]')).toBe(article);
    rerender(
      <PublicLesson
        resource={{ ...resource, data: { ...resource.data, content: { state: 'unavailable' } } }}
        ui={ui}
      />,
    );
    const fallback = screen.getByText('This lesson content is currently unavailable.');
    expect(fallback).toHaveAttribute('lang', 'en');
    expect(fallback).toHaveAttribute('dir', 'ltr');
    expect(screen.getByRole('link')).toHaveAttribute('href', '/ar/courses/foundation');
  });

  it('uses request locale for the backlink independently of payload and UI locales', () => {
    const resource = lesson({ locale: 'ar' });
    render(
      <PublicLesson
        resource={{ ...resource, request: { ...resource.request, locale: 'tr-TR' } }}
        ui={createPublicTranslator('en')}
      />,
    );
    expect(screen.getByRole('article')).toHaveAttribute('lang', 'ar');
    expect(screen.getByRole('link')).toHaveAttribute('href', '/tr-TR/courses/foundation');
  });

  it.each([
    'javascript:alert(1)',
    'data:text/html,secret',
    'https://example.invalid',
    'bad/slug',
    '%66oundation',
    '',
    '..',
  ])('keeps an unsafe course slug %j out of navigation', (slug) => {
    const { container } = render(
      <PublicLesson
        resource={lesson({ course: { slug, title: 'Course' } })}
        ui={createPublicTranslator('en')}
      />,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Authored lesson title');
    expect(screen.queryByRole('link')).toBeNull();
    expect(container.textContent).not.toContain(slug || 'Course');
  });

  it.each([
    '<script>alert("private")</script>',
    '<img src="https://example.invalid/private" onerror="alert(1)">',
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'http://example.invalid/private',
    'https://example.invalid/private',
    'long'.repeat(600),
  ])('renders authored text %j without HTML, linkification or media attributes', (text) => {
    const { container } = render(
      <PublicLesson
        resource={lesson({
          lesson: { slug: 'first', title: text },
          course: { slug: 'foundation', title: text },
          content: {
            state: 'ready',
            rendererKey: 'default-card',
            label: { locale: 'en', value: text },
            fields: [{ name: 'internal', label: { locale: 'en', value: text }, value: text }],
          },
        })}
        ui={createPublicTranslator('en')}
      />,
    );
    expect(container.querySelector('dd')).toHaveTextContent(text);
    expect(container.querySelector('dt')).toHaveTextContent(text);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(text);
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent(text);
    expect(screen.getAllByRole('link')).toHaveLength(1);
    expect(screen.getByRole('link')).toHaveAttribute('href', '/tr-TR/courses/foundation');
    expect(
      container.querySelector(
        'script, img, iframe, audio, video, source, object, embed, [src], [onerror], [onclick]',
      ),
    ).toBeNull();
    expect(
      container.querySelector(
        'a[href^="javascript:"], a[href^="data:"], a[href^="http:"] , a[href^="https:"]',
      ),
    ).toBeNull();
  });

  it.each(['ready', 'empty', 'unavailable', 'unknown'] as const)(
    'retains one chrome-owned main and sequential headings for %s content',
    (state) => {
      const resource = lesson();
      if (resource.data.content.state !== 'ready') throw new Error('Invalid fixture');
      const content =
        state === 'unavailable'
          ? { state: 'unavailable' as const }
          : {
              ...resource.data.content,
              fields: state === 'empty' ? [] : fields,
              rendererKey: state === 'unknown' ? 'future' : 'default-card',
            };
      const ui = createPublicTranslator('tr-TR');
      render(
        <PublicChrome
          site={resource.request.site}
          contentLocale="tr-TR"
          uiLocale={ui.locale}
          uiDirection={ui.direction}
          t={ui.t}
        >
          <PublicLesson resource={{ ...resource, data: { ...resource.data, content } }} ui={ui} />
        </PublicChrome>,
      );
      const main = screen.getByRole('main');
      expect(main).toHaveAttribute('id', 'main-content');
      expect(main).toHaveAttribute('tabindex', '-1');
      expect(screen.getByRole('link', { name: 'İçeriğe geç' })).toHaveAttribute(
        'href',
        '#main-content',
      );
      expect(
        within(main)
          .getAllByRole('heading')
          .map((node) => node.tagName),
      ).toEqual(state === 'ready' || state === 'empty' ? ['H1', 'H2'] : ['H1']);
    },
  );
});

// Dispatch/admission units only; production HTML/RSC is covered by the real fixture.
describe('lesson route dispatch', () => {
  it('passes the admitted resource and UI to the synchronous view and metadata projection', async () => {
    const resource = lesson();
    const ui = createPublicTranslator('en');
    dependencies.resource.mockResolvedValue(resource);
    dependencies.ui.mockResolvedValue(ui);
    dependencies.metadata.mockReturnValue({ title: 'Projected lesson' });
    expect(await LessonPage()).toMatchObject({ type: PublicLesson, props: { resource, ui } });
    expect(await generateMetadata()).toEqual({ title: 'Projected lesson' });
    expect(dependencies.metadata).toHaveBeenCalledOnce();
    expect(dependencies.metadata).toHaveBeenCalledWith(resource, ui);
    for (const operation of Object.values(client)) expect(operation).not.toHaveBeenCalled();
  });

  it.each(
    [CoursePage, LessonPage].flatMap((page) =>
      (['invalid_cursor', 'rate_limited', 'unavailable'] as const).map(
        (state) => [page, state] as const,
      ),
    ),
  )(
    'selects %s controlled %s recovery with an accurate translated destination',
    async (page, state) => {
      const request = lesson().request;
      const ui = createPublicTranslator('tr-TR');
      dependencies.resource.mockResolvedValue({ kind: 'failure', request, state });
      dependencies.ui.mockResolvedValue(ui);
      const view = await page();
      const recoveryPath = state === 'invalid_cursor' ? request.route.path : '/tr-TR/courses';
      expect(view).toMatchObject({
        type: PublicState,
        props: {
          state,
          recoveryPath,
          locale: ui.locale,
          direction: ui.direction,
          t: ui.t,
        },
      });
      render(view);
      expect(
        screen.getByRole('link', {
          name: state === 'invalid_cursor' ? 'İlk sayfaya dön' : 'Kurslara göz at',
        }),
      ).toHaveAttribute('href', recoveryPath);
      expect(dependencies.notFound).not.toHaveBeenCalled();
    },
  );

  it.each([LessonPage, generateMetadata])(
    'honors a loader refusal before UI or metadata work',
    async (consumer) => {
      dependencies.resource.mockRejectedValue(new Error('Admission refused'));
      await expect(consumer()).rejects.toThrow('Admission refused');
      expect(dependencies.ui).not.toHaveBeenCalled();
      expect(dependencies.metadata).not.toHaveBeenCalled();
    },
  );

  it('refuses a mismatched resource kind', async () => {
    dependencies.resource.mockResolvedValue({ kind: 'status', request: lesson().request });
    dependencies.ui.mockResolvedValue(createPublicTranslator('en'));
    await expect(LessonPage()).rejects.toThrow('Not found');
    expect(dependencies.notFound).toHaveBeenCalledOnce();
  });
});
