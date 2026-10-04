import { describe, expect, expectTypeOf, it, vi } from 'vitest';

import type { operations, paths } from './generated/schema';
import { createServerSdk, PUBLIC_GET_WRAPPERS } from './server';
import type { ApiResult, PublicFetch, RequestOptions, ServerSdk } from './server';

const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
const course = {
  slug: 'foundation',
  title: 'Foundation',
  summary: null,
  contentAccess: 'public',
  level: null,
};
const site = {
  displayName: 'School',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};
const catalog = { locale: 'en', items: [course], pageInfo };
const detail = { locale: 'en', course, alternates: [], lessons: { items: [], pageInfo } };
const lesson = {
  locale: 'en',
  course: { slug: 'foundation', title: 'Foundation' },
  lesson: { slug: 'intro', title: 'Intro' },
  alternates: [],
  content: {
    state: 'ready',
    rendererKey: 'default-card',
    label: { value: 'Text', locale: 'en' },
    fields: [{ name: 'text', label: { value: 'Text', locale: 'en' }, value: 'Welcome' }],
  },
};
const problem = (status = 404, code = 'not_found') => ({
  type: 'https://errors.learnstack.dev/' + code,
  title: 'Refused',
  status,
  instance: '/api/v1/public/site',
  code,
  messageKey: 'lockey_' + code,
  correlationId: 'request-123',
});
const json = (value: unknown, status = 200, headers?: HeadersInit) =>
  new Response(JSON.stringify(value), { status, headers });
const calls: [string, (sdk: ServerSdk, options?: RequestOptions) => Promise<unknown>, unknown][] = [
  ['getSite', (sdk, options) => sdk.getSite(options), site],
  ['getCourses', (sdk, options) => sdk.getCourses({ locale: 'en' }, options), catalog],
  [
    'getCourse',
    (sdk, options) => sdk.getCourse({ slug: 'foundation' }, { locale: 'en' }, options),
    detail,
  ],
  [
    'getLesson',
    (sdk, options) =>
      sdk.getLesson({ slug: 'foundation', lessonSlug: 'intro' }, { locale: 'en' }, options),
    lesson,
  ],
];

describe('public GET transport', () => {
  it('covers the four generated GET operations with generated request and result types', () => {
    expect(PUBLIC_GET_WRAPPERS).toEqual({
      '/api/v1/public/site': 'getSite',
      '/api/v1/public/courses': 'getCourses',
      '/api/v1/public/courses/{slug}': 'getCourse',
      '/api/v1/public/courses/{slug}/lessons/{lessonSlug}': 'getLesson',
    });
    type GetPaths = {
      [P in keyof paths]: paths[P]['get'] extends undefined ? never : P;
    }[keyof paths];
    expectTypeOf<keyof typeof PUBLIC_GET_WRAPPERS>().toEqualTypeOf<GetPaths>();
    expectTypeOf<Parameters<ServerSdk['getCourses']>[0]>().toEqualTypeOf<
      operations['GetPublicCourses']['parameters']['query']
    >();
    expectTypeOf<Parameters<ServerSdk['getLesson']>[0]>().toEqualTypeOf<
      operations['GetPublicLesson']['parameters']['path']
    >();
    expectTypeOf<Awaited<ReturnType<ServerSdk['getLesson']>>>().toEqualTypeOf<
      ApiResult<operations['GetPublicLesson']['responses'][200]['content']['application/json']>
    >();
    expectTypeOf<keyof RequestOptions>().toEqualTypeOf<'signal'>();
    expectTypeOf<keyof ServerSdk>().toEqualTypeOf<
      'getSite' | 'getCourses' | 'getCourse' | 'getLesson'
    >();
  });
  it('encodes each path component and query value and omits undefined parameters', async () => {
    const transport = vi.fn<PublicFetch>().mockResolvedValue(json(site));
    const sdk = createServerSdk(transport);
    await sdk.getSite();
    await sdk.getCourses({ locale: 'tr-TR', cursor: 'a+/=?&', limit: '0020' });
    await sdk.getCourse(
      { slug: 'a/b ?#ü' },
      { locale: 'en', lessonCursor: 'a+b&', lessonLimit: '100' },
    );
    await sdk.getLesson({ slug: 'course/one', lessonSlug: 'lesson ?ü' }, { locale: 'en' });
    expect(transport.mock.calls.map(([url]) => url)).toEqual([
      '/api/v1/public/site',
      '/api/v1/public/courses?locale=tr-TR&cursor=a%2B%2F%3D%3F%26&limit=0020',
      '/api/v1/public/courses/a%2Fb%20%3F%23%C3%BC?locale=en&lessonCursor=a%2Bb%26&lessonLimit=100',
      '/api/v1/public/courses/course%2Fone/lessons/lesson%20%3F%C3%BC?locale=en',
    ]);
  });
  it.each(calls)(
    '%s accepts its response, forwards cancellation, and carries no authority',
    async (_name, call, value) => {
      const transport = vi.fn<PublicFetch>().mockResolvedValue(json(value));
      const controller = new AbortController();
      expect(await call(createServerSdk(transport), { signal: controller.signal })).toEqual({
        kind: 'success',
        status: 200,
        data: value,
      });
      expect(transport).toHaveBeenCalledOnce();
      expect(transport.mock.calls[0]?.[1]).toEqual({
        method: 'GET',
        cache: 'no-store',
        headers: { Accept: 'application/json' },
        signal: controller.signal,
      });
    },
  );
  it('drops undeclared authority from query and options supplied by an untyped caller', async () => {
    const transport = vi.fn<PublicFetch>().mockResolvedValue(json(catalog));
    const query = { locale: 'en', tenantId: 'other', organizationId: 'other', host: 'other' };
    const options = {
      headers: { 'X-Tenant-Id': 'other' },
      tenantId: 'other',
      hopSecret: 'untrusted',
      signal: undefined,
    };
    await createServerSdk(transport).getCourses(query, options);
    expect(transport.mock.calls[0]?.[0]).toBe('/api/v1/public/courses?locale=en');
    expect(transport.mock.calls[0]?.[1].headers).toEqual({ Accept: 'application/json' });
  });
  it('accepts empty results, protected marketing, unavailable content and additive members', async () => {
    const transport = vi
      .fn<PublicFetch>()
      .mockResolvedValueOnce(
        json({
          ...site,
          extra: 'future',
          theme: {
            primary: '#123456',
            background: '#ffffff',
            foreground: '#000000',
            muted: '#555555',
          },
        }),
      )
      .mockResolvedValueOnce(json({ ...catalog, items: [] }))
      .mockResolvedValueOnce(
        json({
          ...detail,
          course: {
            ...course,
            contentAccess: 'enrollment_required',
            level: { state: 'unavailable', label: null },
          },
          lessons: null,
        }),
      )
      .mockResolvedValueOnce(json({ ...lesson, content: { state: 'unavailable' } }));
    const sdk = createServerSdk(transport);
    for (const [, call] of calls) expect(await call(sdk)).toMatchObject({ kind: 'success' });
  });
  it.each(calls)('%s refuses a wrong success shape', async (_name, call) => {
    expect(await call(createServerSdk(async () => json({ rawBody: 'wrong' })))).toEqual({
      kind: 'invalid-response',
      reason: 'invalid-success',
    });
  });
  it('narrows generated lesson state before reading display fields', async () => {
    const result = await createServerSdk(async () => json(lesson)).getLesson(
      { slug: 'foundation', lessonSlug: 'intro' },
      { locale: 'en' },
    );
    if (result.kind !== 'success' || result.data.content.state !== 'ready')
      throw new Error('Positive ready control failed');
    expectTypeOf(result.data.content.rendererKey).toEqualTypeOf<string>();
    expect(result.data.content.fields[0]?.value).toBe('Welcome');
  });
  it.each([null, [], 'text', { ...site, theme: {} }, { ...site, enabledLocales: [7] }])(
    'refuses malformed bootstrap data %j',
    async (value) => {
      expect(await createServerSdk(async () => json(value)).getSite()).toEqual({
        kind: 'invalid-response',
        reason: 'invalid-success',
      });
    },
  );
  it('preserves known localization parameters while ignoring unknown extensions', async () => {
    const payload = {
      ...problem(400, 'validation_failed'),
      errors: {
        locale: [{ key: 'lockey_required', params: { maxLength: '35' }, extra: 'ignored' }],
      },
      tenantId: 'hidden',
      provider: 'untrusted',
      latestVersion: 99,
    };
    expect(await createServerSdk(async () => json(payload, 400)).getSite()).toEqual({
      kind: 'api-error',
      status: 400,
      error: { code: 'validation_failed', fieldErrors: { locale: ['lockey_required'] } },
      problem: {
        ...problem(400, 'validation_failed'),
        errors: { locale: [{ key: 'lockey_required', params: { maxLength: '35' } }] },
      },
    });
  });
  it.each([35, { hidden: 'raw' }, null])(
    'refuses non-string localization parameters %j',
    async (value) => {
      const payload = {
        ...problem(400, 'validation_failed'),
        errors: { locale: [{ key: 'lockey_required', params: { maxLength: value } }] },
      };
      expect(await createServerSdk(async () => json(payload, 400)).getSite()).toEqual({
        kind: 'invalid-response',
        reason: 'invalid-problem',
      });
    },
  );
  it.each(['\uD800', '\uDFFF'])('returns a result for an ill-formed path string', async (slug) => {
    const transport = vi.fn();
    const sdk = createServerSdk(transport);
    expect(await sdk.getCourse({ slug }, { locale: 'en' })).toEqual({ kind: 'invalid-request' });
    expect(await sdk.getLesson({ slug: 'foundation', lessonSlug: slug }, { locale: 'en' })).toEqual(
      { kind: 'invalid-request' },
    );
    const controller = new AbortController();
    controller.abort();
    expect(
      await sdk.getLesson(
        { slug: 'foundation', lessonSlug: slug },
        { locale: 'en' },
        { signal: controller.signal },
      ),
    ).toEqual({ kind: 'cancelled' });
    expect(transport).not.toHaveBeenCalled();
  });
  it.each(['unsupported_locale', 'internal_error', 'future_code'])(
    'maps %s to the closed unknown branch',
    async (code) => {
      expect(
        await createServerSdk(async () =>
          json({ ...problem(400, code), secret: 'ignored' }, 400),
        ).getSite(),
      ).toEqual({
        kind: 'api-error',
        status: 400,
        problem: problem(400, code),
        error: { code: 'unknown', correlationId: 'request-123' },
      });
    },
  );
  it.each([
    'not_found',
    'concurrency_conflict',
    'request_in_progress',
    'idempotency_key_reuse',
    'idempotency_outcome_unavailable',
    'method_not_allowed',
    'payload_too_large',
    'unsupported_media_type',
    'request_rejected',
    'forbidden',
    'unauthorized',
    'recording_consent_required',
  ])('retains the closed %s code', async (code) => {
    const result = await createServerSdk(async () =>
      json({ ...problem(404, code), resource: 'ignored', latestVersion: 99 }, 404),
    ).getSite();
    expect(result.kind === 'api-error' && result.error).toEqual({ code });
  });
  it.each(['dependency_unavailable', 'audit_unavailable', 'rate_limited'])(
    'bounds retry-after for %s',
    async (code) => {
      const result = await createServerSdk(async () =>
        json(problem(503, code), 503, { 'Retry-After': '12' }),
      ).getSite();
      expect(result.kind === 'api-error' && result.error).toEqual({ code, retryAfter: 12 });
    },
  );
  it.each(['-1', '1.5', '9999999999999999999', 'Wed, 01 Jan 2030 00:00:00 GMT'])(
    'ignores unsafe retry-after %s',
    async (header) => {
      const result = await createServerSdk(async () =>
        json(problem(429, 'rate_limited'), 429, { 'Retry-After': header }),
      ).getSite();
      expect(result.kind === 'api-error' && result.error).toEqual({ code: 'rate_limited' });
    },
  );
  it('preserves prototype-shaped field keys without changing the result prototype', async () => {
    const errors: unknown = JSON.parse('{"__proto__":[{"key":"lockey_required"}]}');
    const result = await createServerSdk(async () =>
      json({ ...problem(400, 'validation_failed'), errors }, 400),
    ).getSite();
    if (result.kind !== 'api-error' || result.error.code !== 'validation_failed')
      throw new Error('Positive validation control failed');
    expect(Object.hasOwn(result.error.fieldErrors, '__proto__')).toBe(true);
    expect(Object.getPrototypeOf(result.error.fieldErrors)).toBe(Object.prototype);
  });
  it.each([
    null,
    [],
    { ...problem(), status: 400 },
    { ...problem(), messageKey: 'wrong' },
    { ...problem(), correlationId: null },
    { ...problem(), errors: [] },
    { ...problem(), errors: { locale: 'wrong' } },
    { ...problem(), errors: { locale: [{ key: 'wrong' }] } },
    { ...problem(), errors: { locale: [{ key: 'lockey_required', params: [] }] } },
  ])('refuses malformed Problem Details %j', async (value) => {
    expect(await createServerSdk(async () => json(value, 404)).getSite()).toEqual({
      kind: 'invalid-response',
      reason: 'invalid-problem',
    });
  });
  it('distinguishes malformed JSON, failed transport, unexpected status and pre-cancelled calls', async () => {
    expect(await createServerSdk(async () => new Response('{')).getSite()).toEqual({
      kind: 'invalid-response',
      reason: 'malformed-json',
    });
    expect(
      await createServerSdk(async () => {
        throw new Error('Network failed');
      }).getSite(),
    ).toEqual({ kind: 'transport-error' });
    expect(
      await createServerSdk(async () => new Response(null, { status: 204 })).getSite(),
    ).toEqual({ kind: 'invalid-response', reason: 'unexpected-status' });
    const controller = new AbortController();
    controller.abort();
    const unused = vi.fn<PublicFetch>();
    expect(await createServerSdk(unused).getSite({ signal: controller.signal })).toEqual({
      kind: 'cancelled',
    });
    expect(unused).not.toHaveBeenCalled();
  });
  it('distinguishes stream failure and cancellation during fetch or body consumption', async () => {
    const broken = () =>
      new Response(
        new ReadableStream({
          start(stream) {
            stream.error(new Error('Stream failed'));
          },
        }),
      );
    expect(await createServerSdk(async () => broken()).getSite()).toEqual({
      kind: 'transport-error',
    });
    const duringFetch = new AbortController();
    expect(
      await createServerSdk(async () => {
        duringFetch.abort();
        throw new DOMException('Aborted', 'AbortError');
      }).getSite({ signal: duringFetch.signal }),
    ).toEqual({ kind: 'cancelled' });
    const duringBody = new AbortController();
    const response = new Response(
      new ReadableStream({
        pull(stream) {
          duringBody.abort();
          stream.error(new DOMException('Aborted', 'AbortError'));
        },
      }),
    );
    expect(
      await createServerSdk(async () => response).getSite({ signal: duringBody.signal }),
    ).toEqual({ kind: 'cancelled' });
  });
});
