// @vitest-environment node
import type { ApiResult, AppError } from '@learnstack/sdk/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ConfiguredPublicClient } from './configured-public-client';
import { getPublicRequest } from './public-request';
import type { PublicRequest } from './public-request';
import { getPublicResource, requirePublicResource } from './public-resource';
import { publicRoute } from './public-route';

const navigation = vi.hoisted(() => ({
  notFound: vi.fn<() => never>(),
  redirect: vi.fn<(path: string) => never>(),
}));
vi.mock('next/navigation', () => navigation);
vi.mock('./public-request', () => ({
  getPublicRequest: vi.fn(),
  assertPublicRequestActive: vi.fn(),
}));
// Dedicated memo controls and the production RSC fixture prove request reuse;
// these invocations independently exercise dispatch and closed outcome mapping.
vi.mock('./request-memo', () => ({ requestMemo: <T>(load: T): T => load }));

const getRequest = vi.mocked(getPublicRequest);
const catalog = {
  locale: 'tr-TR',
  items: [],
  pageInfo: { hasNext: false, hasPrevious: false, nextCursor: null, previousCursor: null },
};
const course = {
  locale: 'tr-TR',
  alternates: [],
  lessons: null,
  course: {
    slug: 'foundation',
    title: 'Foundation',
    summary: null,
    level: null,
    contentAccess: 'enrollment_required' as const,
  },
};
const lesson = {
  locale: 'tr-TR',
  alternates: [],
  content: { state: 'unavailable' as const },
  course: { slug: 'foundation', title: 'Foundation' },
  lesson: { slug: 'intro', title: 'Introduction' },
};
const client = {
  getSite: vi.fn<ConfiguredPublicClient['getSite']>(),
  getCourses: vi.fn<ConfiguredPublicClient['getCourses']>(),
  getCourse: vi.fn<ConfiguredPublicClient['getCourse']>(),
  getLesson: vi.fn<ConfiguredPublicClient['getLesson']>(),
};

function request(target: string): PublicRequest {
  return {
    context: { host: 'institution.example:3000', peer: '203.0.113.7', method: 'GET', target },
    client,
    site: {
      displayName: 'Institution',
      defaultLocale: 'tr-TR',
      enabledLocales: ['tr-TR'],
      showPlatformAttribution: true,
      theme: null,
    },
    locale: target === '/studio' || target === '/portal' ? null : 'tr-TR',
    route: publicRoute(target),
  };
}

function apiError(status: number, error: AppError): ApiResult<never> {
  return {
    kind: 'api-error',
    status,
    error,
    problem: {
      status,
      code: 'private_backend_code',
      messageKey: 'lockey_private_backend_key',
      title: 'PRIVATE BACKEND TITLE',
      instance: '/private-resource?cursor=private',
      type: 'https://private.example/problem',
      correlationId: 'private-correlation',
      errors: {
        privateField: [{ key: 'lockey_private_field', params: { secret: 'private-value' } }],
      },
    },
  };
}

function expectNoEducation() {
  expect(client.getCourses).not.toHaveBeenCalled();
  expect(client.getCourse).not.toHaveBeenCalled();
  expect(client.getLesson).not.toHaveBeenCalled();
}

beforeEach(() => {
  getRequest.mockReset();
  for (const operation of Object.values(client)) operation.mockReset();
  client.getCourses.mockResolvedValue({ kind: 'success', status: 200, data: catalog });
  client.getCourse.mockResolvedValue({ kind: 'success', status: 200, data: course });
  client.getLesson.mockResolvedValue({ kind: 'success', status: 200, data: lesson });
  navigation.notFound.mockReset().mockImplementation(() => {
    throw new Error('NEXT_NOT_FOUND');
  });
  navigation.redirect.mockReset().mockImplementation(() => {
    throw new Error('NEXT_REDIRECT');
  });
});

afterEach(() => vi.restoreAllMocks());

describe('public resource dispatch after shared live admission', () => {
  it('refuses before any content call when request admission fails', async () => {
    getRequest.mockResolvedValue(null);
    expect(await getPublicResource()).toEqual({ kind: 'refused' });
    expectNoEducation();
    expect(client.getSite).not.toHaveBeenCalled();
  });

  it.each(['/tr-TR/status/not-found?cursor=&limit=bad', '/studio', '/portal'])(
    'loads no Education data for %s',
    async (target) => {
      const admitted = request(target);
      getRequest.mockResolvedValue(admitted);
      expect(await getPublicResource()).toEqual({
        kind: target.includes('/status/') ? 'status' : 'scaffold',
        request: admitted,
      });
      expectNoEducation();
      expect(client.getSite).not.toHaveBeenCalled();
    },
  );

  it('dispatches catalog using only its signed owned pagination', async () => {
    const admitted = request(
      '/tr-TR/courses?locale=en&cursor=opaque_1&limit=0005&lessonCursor=&lessonLimit=bad',
    );
    getRequest.mockResolvedValue(admitted);
    expect(await getPublicResource()).toEqual({
      kind: 'catalog',
      request: admitted,
      data: catalog,
      pagination: { cursor: 'opaque_1', limit: '5', isPaginated: true },
    });
    expect(client.getCourses).toHaveBeenCalledWith({ cursor: 'opaque_1', limit: '5' });
    expect(client.getCourses).toHaveBeenCalledOnce();
    expect(client.getCourse).not.toHaveBeenCalled();
    expect(client.getLesson).not.toHaveBeenCalled();
  });

  it('dispatches decoded admitted course slug and outline pagination only', async () => {
    const admitted = request(
      '/tr-TR/courses/%66oundation?slug=other&lessonCursor=opaque_2&lessonLimit=101&cursor=&limit=bad',
    );
    getRequest.mockResolvedValue(admitted);
    expect(await getPublicResource()).toEqual({
      kind: 'course',
      request: admitted,
      data: course,
      pagination: { cursor: 'opaque_2', limit: '100', isPaginated: true },
    });
    expect(client.getCourse).toHaveBeenCalledWith(
      { slug: 'foundation' },
      { lessonCursor: 'opaque_2', lessonLimit: '100' },
    );
    expect(client.getCourse).toHaveBeenCalledOnce();
    expect(client.getCourses).not.toHaveBeenCalled();
    expect(client.getLesson).not.toHaveBeenCalled();
  });

  it('dispatches lesson path slugs and ignores pagination from either list surface', async () => {
    const admitted = request(
      '/tr-TR/courses/%66oundation/lessons/%69ntro?slug=other&lessonSlug=other&cursor=&limit=0&lessonCursor=&lessonLimit=0',
    );
    getRequest.mockResolvedValue(admitted);
    expect(await getPublicResource()).toEqual({ kind: 'lesson', request: admitted, data: lesson });
    expect(client.getLesson).toHaveBeenCalledWith({ slug: 'foundation', lessonSlug: 'intro' });
    expect(client.getLesson).toHaveBeenCalledOnce();
    expect(client.getCourses).not.toHaveBeenCalled();
    expect(client.getCourse).not.toHaveBeenCalled();
  });

  it('diagnoses an unsupported composite once without its key, label, names or values', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const admitted = request('/tr-TR/courses/foundation/lessons/intro');
    getRequest.mockResolvedValue(admitted);
    client.getLesson.mockResolvedValue({
      kind: 'success',
      status: 200,
      data: {
        ...lesson,
        content: {
          state: 'ready',
          rendererKey: 'PRIVATE_COMPOSITE',
          label: { locale: 'en', value: 'PRIVATE_LABEL' },
          fields: [
            {
              name: 'PRIVATE_FIELD',
              label: { locale: 'en', value: 'PRIVATE_FIELD_LABEL' },
              value: 'PRIVATE_VALUE',
            },
          ],
        },
      },
    });
    expect((await getPublicResource()).kind).toBe('lesson');
    expect(client.getLesson).toHaveBeenCalledOnce();
    expect(warn).toHaveBeenCalledOnce();
    expect(warn).toHaveBeenCalledWith('Public lesson presentation unavailable', {
      state: 'unsupported_renderer',
      count: 1,
    });
    expect(JSON.stringify(warn.mock.calls)).not.toMatch(
      /PRIVATE|203\.0|foundation|intro|Institution/,
    );
  });

  it.each(['ready', 'unavailable'] as const)(
    'does not duplicate API diagnostics for %s content',
    async (state) => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
      getRequest.mockResolvedValue(request('/tr-TR/courses/foundation/lessons/intro'));
      client.getLesson.mockResolvedValue({
        kind: 'success',
        status: 200,
        data: {
          ...lesson,
          content:
            state === 'unavailable'
              ? { state: 'unavailable' }
              : {
                  state: 'ready',
                  rendererKey: 'default-card',
                  label: { locale: 'en', value: 'Card' },
                  fields: [],
                },
        },
      });
      expect((await getPublicResource()).kind).toBe('lesson');
      expect(warn).not.toHaveBeenCalled();
    },
  );

  it.each([
    '/tr-TR/courses?cursor=',
    '/tr-TR/courses?cursor=a&cursor=b',
    '/tr-TR/courses?limit=0',
    '/tr-TR/courses?limit=1&limit=1',
    '/tr-TR/courses/foundation?lessonCursor=bad%2Ftoken',
    '/tr-TR/courses/foundation?lessonLimit=',
    '/tr-TR/courses/foundation?lessonCursor=a&lessonCursor=b',
  ])('refuses malformed owned pagination before Education: %s', async (target) => {
    const admitted = request(target);
    getRequest.mockResolvedValue(admitted);
    expect(await getPublicResource()).toEqual({
      kind: 'failure',
      request: admitted,
      state: 'invalid_cursor',
    });
    expectNoEducation();
  });
});

describe.each([
  { target: '/tr-TR/courses', cursor: 'cursor', operation: client.getCourses },
  {
    target: '/tr-TR/courses/foundation',
    cursor: 'lessonCursor',
    operation: client.getCourse,
  },
])('owned cursor failures: $target', ({ target, cursor, operation }) => {
  it.each([
    [true, { owned: ['private diagnostic'] }, 'invalid_cursor'],
    [false, { owned: ['private diagnostic'] }, 'unavailable'],
    [true, { private: ['private diagnostic'] }, 'unavailable'],
    [true, { owned: ['private diagnostic'], private: ['private diagnostic'] }, 'unavailable'],
    [true, {}, 'unavailable'],
  ] as const)(
    'maps only a present cursor and its exclusive field errors',
    async (present, fields, state) => {
      const admitted = request(`${target}${present ? `?${cursor}=opaque_1` : ''}`);
      getRequest.mockResolvedValue(admitted);
      operation.mockResolvedValue(
        apiError(400, {
          code: 'validation_failed',
          fieldErrors: Object.fromEntries(
            Object.entries(fields).map(([field, messages]) => [
              field === 'owned' ? cursor : field,
              [...messages],
            ]),
          ),
        }),
      );
      expect(await getPublicResource()).toEqual({ kind: 'failure', request: admitted, state });
    },
  );
});

describe.each([
  { target: '/tr-TR/courses', operation: client.getCourses },
  { target: '/tr-TR/courses/foundation', operation: client.getCourse },
  { target: '/tr-TR/courses/foundation/lessons/intro', operation: client.getLesson },
])('controlled resource outcomes: $target', ({ target, operation }) => {
  it.each([
    ['missing', apiError(404, { code: 'not_found', resource: 'PRIVATE RESOURCE' }), undefined],
    ['failure', apiError(429, { code: 'rate_limited', retryAfter: 99 }), 'rate_limited'],
    [
      'failure',
      apiError(400, {
        code: 'validation_failed',
        fieldErrors: { private: ['private diagnostic'] },
      }),
      'unavailable',
    ],
    ['failure', apiError(503, { code: 'validation_failed', fieldErrors: {} }), 'unavailable'],
    [
      'failure',
      apiError(503, { code: 'dependency_unavailable', provider: 'private provider' }),
      'unavailable',
    ],
    [
      'failure',
      apiError(500, { code: 'unknown', correlationId: 'private-correlation' }),
      'unavailable',
    ],
    ['failure', apiError(400, { code: 'request_rejected' }), 'unavailable'],
    ['failure', { kind: 'transport-error' }, 'unavailable'],
    ['failure', { kind: 'invalid-response', reason: 'invalid-success' }, 'unavailable'],
    ['failure', { kind: 'invalid-request' }, 'unavailable'],
    ['failure', { kind: 'cancelled' }, 'unavailable'],
  ] as const)(
    'maps %s without retaining backend messages or problem details',
    async (kind, result, state) => {
      const admitted = request(target);
      getRequest.mockResolvedValue(admitted);
      operation.mockResolvedValue(result);
      const resource = await getPublicResource();
      expect(resource).toEqual(
        state ? { kind, request: admitted, state } : { kind, request: admitted },
      );
      expect(operation).toHaveBeenCalledOnce();
      expect(JSON.stringify(resource)).not.toMatch(/private|PRIVATE|lockey_/);
    },
  );
});

describe('consumer admission and missing-content navigation', () => {
  it('invokes framework refusal when admission failed', async () => {
    getRequest.mockResolvedValue(null);
    await expect(requirePublicResource()).rejects.toThrow('NEXT_NOT_FOUND');
    expect(navigation.notFound).toHaveBeenCalledOnce();
    expect(navigation.redirect).not.toHaveBeenCalled();
    expectNoEducation();
  });

  it('redirects missing content to the fixed locale status path without original input', async () => {
    getRequest.mockResolvedValue(
      request('/tr-TR/courses/private-slug?cursor=private_cursor&next=https://evil.example'),
    );
    client.getCourse.mockResolvedValue(apiError(404, { code: 'not_found' }));
    await expect(requirePublicResource()).rejects.toThrow('NEXT_REDIRECT');
    expect(navigation.redirect).toHaveBeenCalledWith('/tr-TR/status/not-found');
    expect(navigation.redirect).toHaveBeenCalledOnce();
    expect(navigation.notFound).not.toHaveBeenCalled();
  });

  it('returns controlled failures for ordinary rendering without redirecting', async () => {
    const admitted = request('/tr-TR/courses?cursor=');
    getRequest.mockResolvedValue(admitted);
    expect(await requirePublicResource()).toEqual({
      kind: 'failure',
      request: admitted,
      state: 'invalid_cursor',
    });
    expect(navigation.redirect).not.toHaveBeenCalled();
    expect(navigation.notFound).not.toHaveBeenCalled();
  });

  it('refuses a defensive missing-content outcome without an admitted locale', async () => {
    // Content routes normally carry a locale; a broken caller must not redirect to /null/.
    getRequest.mockResolvedValue({ ...request('/tr-TR/courses/private-slug'), locale: null });
    client.getCourse.mockResolvedValue(apiError(404, { code: 'not_found' }));
    await expect(requirePublicResource()).rejects.toThrow('NEXT_NOT_FOUND');
    expect(navigation.notFound).toHaveBeenCalledOnce();
    expect(navigation.redirect).not.toHaveBeenCalled();
  });
});
