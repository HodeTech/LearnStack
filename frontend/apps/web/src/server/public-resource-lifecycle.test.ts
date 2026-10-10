// @vitest-environment node
import { AsyncLocalStorage } from 'node:async_hooks';
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';

import type { ConfiguredPublicClient } from './configured-public-client';
import type { IngressContext } from './ingress';
import { createPublicAdmissionRuntime } from './public-admission-runtime';
import type { PublicAdmissionRead } from './public-admission-runtime';
import type { PublicSite } from './public-entry';
import type * as PublicRequestModule from './public-request';
import { getPublicResource, requirePublicResource } from './public-resource';
import { publicRoute } from './public-route';

type PublicRequest = PublicRequestModule.PublicRequest;

const dependencies = vi.hoisted(() => ({
  headers: vi.fn<() => Promise<Headers>>(),
  request: vi.fn<() => Promise<PublicRequest | null>>(),
  read: vi.fn<(binding: IngressContext) => PublicAdmissionRead>(),
  afterAssertion: vi.fn<() => void>(),
  notFound: vi.fn<() => never>(),
  redirect: vi.fn<(path: string) => never>(),
}));
vi.mock('next/headers', () => ({ headers: dependencies.headers }));
vi.mock('next/navigation', () => ({
  notFound: dependencies.notFound,
  redirect: dependencies.redirect,
}));
vi.mock('./public-admission', () => ({ readPublicAdmission: dependencies.read }));
vi.mock('./public-request', async (importOriginal) => {
  const actual = await importOriginal<typeof PublicRequestModule>();
  return {
    ...actual,
    getPublicRequest: dependencies.request,
    assertPublicRequestActive(request: PublicRequest) {
      // Keep the production snapshot-identity check and the real native lifetime.
      actual.assertPublicRequestActive(request);
      dependencies.afterAssertion();
    },
  };
});

const completed = 'Public admission request completed';
const unavailable = 'Public admission context unavailable';
const site: PublicSite = {
  displayName: 'Institution',
  enabledLocales: ['tr'],
  defaultLocale: 'tr',
  showPlatformAttribution: true,
  theme: null,
};
const catalog: Awaited<ReturnType<ConfiguredPublicClient['getCourses']>> = {
  kind: 'success',
  status: 200,
  data: {
    locale: 'tr',
    items: [],
    pageInfo: { hasNext: false, hasPrevious: false, nextCursor: null, previousCursor: null },
  },
};
const course: Awaited<ReturnType<ConfiguredPublicClient['getCourse']>> = {
  kind: 'success',
  status: 200,
  data: {
    locale: 'tr',
    alternates: [],
    lessons: null,
    course: {
      slug: 'foundation',
      title: 'Foundation',
      summary: null,
      level: null,
      contentAccess: 'enrollment_required',
    },
  },
};
const lesson: Awaited<ReturnType<ConfiguredPublicClient['getLesson']>> = {
  kind: 'success',
  status: 200,
  data: {
    locale: 'tr',
    alternates: [],
    course: { slug: 'foundation', title: 'Foundation' },
    lesson: { slug: 'intro', title: 'Introduction' },
    content: {
      state: 'ready',
      rendererKey: 'unsupported-private-renderer',
      label: { locale: 'tr', value: 'Private renderer label' },
      fields: [],
    },
  },
};

function client() {
  return {
    getSite: vi.fn<ConfiguredPublicClient['getSite']>(),
    getCourses: vi.fn<ConfiguredPublicClient['getCourses']>().mockResolvedValue(catalog),
    getCourse: vi.fn<ConfiguredPublicClient['getCourse']>().mockResolvedValue(course),
    getLesson: vi.fn<ConfiguredPublicClient['getLesson']>().mockResolvedValue(lesson),
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((release) => {
    resolve = release;
  });
  return { promise, resolve };
}

function pause<Arguments extends unknown[], Result extends { kind: string }>(
  operation: Mock<(...args: Arguments) => Promise<Result>>,
  result: Result,
) {
  const entered = deferred<void>();
  const pending = deferred<Result>();
  const readResult = vi.fn(() => result.kind);
  const observed = Object.defineProperty({ ...result }, 'kind', { get: readResult });
  operation.mockImplementation(() => {
    entered.resolve();
    return pending.promise;
  });
  return {
    entered: entered.promise,
    release: () => pending.resolve(observed),
    readResult,
  };
}

type Frame = {
  request: PublicRequest;
  incoming: Headers;
  nativeRequest: IncomingMessage;
  nativeResponse: ServerResponse;
  signal: AbortSignal;
};
const frames = new AsyncLocalStorage<Frame>();
let runtime: ReturnType<typeof createPublicAdmissionRuntime>;

function current(): Frame {
  const frame = frames.getStore();
  if (!frame) throw new Error('Test request context missing');
  return frame;
}

function withinRequest<T>(
  target: string,
  work: (frame: Frame) => Promise<T>,
  configured = client(),
  incoming = new Headers(),
): Promise<T> {
  const binding = { host: 'institution.example:3000', peer: '203.0.113.9', method: 'GET', target };
  const nativeRequest = Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage;
  const nativeResponse = Object.assign(new EventEmitter(), {
    destroyed: false,
    writableEnded: false,
  }) as ServerResponse;
  return runtime.run(binding, nativeRequest, nativeResponse, async () => {
    runtime.holder.begin(binding).publish(site);
    const admission = runtime.holder.read(binding);
    const frame: Frame = {
      incoming,
      nativeRequest,
      nativeResponse,
      signal: admission.signal,
      request: {
        context: binding,
        site: admission.site as PublicSite,
        client: configured,
        locale: target === '/studio' ? null : 'tr',
        route: publicRoute(target),
      },
    };
    try {
      return await frames.run(frame, () => work(frame));
    } finally {
      nativeResponse.emit('finish');
    }
  });
}

function finish(frame: Frame, event: 'finish' | 'abort') {
  if (event === 'finish') frame.nativeResponse.emit('finish');
  else frame.nativeRequest.emit('aborted');
  expect(frame.signal.aborted).toBe(true);
}

function expectNoEducation(configured: ReturnType<typeof client>) {
  expect(configured.getCourses).not.toHaveBeenCalled();
  expect(configured.getCourse).not.toHaveBeenCalled();
  expect(configured.getLesson).not.toHaveBeenCalled();
}

beforeEach(() => {
  runtime = createPublicAdmissionRuntime();
  dependencies.headers.mockReset().mockImplementation(async () => current().incoming);
  dependencies.request.mockReset().mockImplementation(async () => current().request);
  dependencies.read.mockReset().mockImplementation((binding) => runtime.holder.read(binding));
  dependencies.afterAssertion.mockReset();
  dependencies.notFound.mockReset().mockImplementation(() => {
    throw new Error('NEXT_NOT_FOUND');
  });
  dependencies.redirect.mockReset().mockImplementation(() => {
    throw new Error('NEXT_REDIRECT');
  });
});

afterEach(() => {
  const counts = runtime.counts();
  runtime.shutdown();
  expect(counts).toEqual({ active: 0, snapshots: 0 });
});

describe('public resource native lifetime across real memoization', () => {
  it.each(['finish', 'abort'] as const)(
    'starts no Education operation when admission resumes after native %s',
    async (event) => {
      const configured = client();
      await withinRequest(
        '/tr/courses',
        async (frame) => {
          const entered = deferred<void>();
          const admission = deferred<PublicRequest>();
          dependencies.request.mockImplementationOnce(() => {
            entered.resolve();
            return admission.promise;
          });
          const result = getPublicResource();
          await entered.promise;
          finish(frame, event);
          admission.resolve(frame.request);
          await expect(result).rejects.toThrow(completed);
          expectNoEducation(configured);
          expect(configured.getSite).not.toHaveBeenCalled();
        },
        configured,
      );
    },
  );

  const operations = [
    {
      name: 'catalog',
      target: '/tr/courses',
      hold: (configured: ReturnType<typeof client>) => pause(configured.getCourses, catalog),
    },
    {
      name: 'course',
      target: '/tr/courses/foundation',
      hold: (configured: ReturnType<typeof client>) => pause(configured.getCourse, course),
    },
    {
      name: 'lesson',
      target: '/tr/courses/foundation/lessons/intro',
      hold: (configured: ReturnType<typeof client>) => pause(configured.getLesson, lesson),
    },
  ];

  describe.each(operations)('$name content completion', ({ name, target, hold }) => {
    it.each(['finish', 'abort'] as const)(
      'does not inspect or log a response arriving after native %s',
      async (event) => {
        const configured = client();
        const pending = hold(configured);
        const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
        await withinRequest(
          target,
          async (frame) => {
            const result = getPublicResource();
            await pending.entered;
            finish(frame, event);
            // The stub deliberately ignores cancellation and returns a late body.
            pending.release();
            await expect(result).rejects.toThrow(completed);
            expect(pending.readResult).not.toHaveBeenCalled();
            expect(warn).not.toHaveBeenCalled();
            await expect(getPublicResource()).rejects.toThrow(completed);
            expect(dependencies.request).toHaveBeenCalledOnce();
            expect(configured.getSite).not.toHaveBeenCalled();
          },
          configured,
        );
      },
    );

    it('shares pending and completed content while active, then refuses cached replay', async () => {
      const configured = client();
      const pending = hold(configured);
      vi.spyOn(console, 'warn').mockImplementation(() => {});
      await withinRequest(
        target,
        async (frame) => {
          const first = getPublicResource();
          const second = getPublicResource();
          await pending.entered;
          pending.release();
          const result = await first;
          expect(result.kind).toBe(name);
          expect(await second).toBe(result);
          expect(await getPublicResource()).toBe(result);
          expect(pending.readResult).toHaveBeenCalled();
          expect(dependencies.request).toHaveBeenCalledOnce();
          expect(
            configured.getCourses.mock.calls.length +
              configured.getCourse.mock.calls.length +
              configured.getLesson.mock.calls.length,
          ).toBe(1);
          finish(frame, 'finish');
          await expect(getPublicResource()).rejects.toThrow(completed);
          expect(dependencies.request).toHaveBeenCalledOnce();
          expect(
            configured.getCourses.mock.calls.length +
              configured.getCourse.mock.calls.length +
              configured.getLesson.mock.calls.length,
          ).toBe(1);
        },
        configured,
      );
    });
  });

  it.each(['/tr/status/not-found', '/studio', '/tr/courses?cursor='])(
    'rejects completed cached %s without creating content work',
    async (target) => {
      const configured = client();
      await withinRequest(
        target,
        async (frame) => {
          await getPublicResource();
          finish(frame, 'finish');
          await expect(getPublicResource()).rejects.toThrow(completed);
          expectNoEducation(configured);
          expect(dependencies.request).toHaveBeenCalledOnce();
        },
        configured,
      );
    },
  );

  it('checks the require await before redirecting a completed missing resource', async () => {
    const configured = client();
    configured.getCourse.mockResolvedValue({
      kind: 'api-error',
      status: 404,
      error: { code: 'not_found' },
      problem: {
        status: 404,
        code: 'not_found',
        title: 'Not found',
        correlationId: 'test-correlation',
        instance: '/api/v1/public/courses/foundation',
        messageKey: 'lockey_not_found',
        type: 'https://example.invalid/not-found',
      },
    });
    await withinRequest(
      '/tr/courses/foundation',
      async (frame) => {
        expect((await getPublicResource()).kind).toBe('missing');
        // The cached getPublicResource guard succeeds. Native completion is queued
        // before requirePublicResource resumes its own await continuation.
        dependencies.afterAssertion.mockImplementationOnce(() => {
          queueMicrotask(() => finish(frame, 'finish'));
        });
        await expect(requirePublicResource()).rejects.toThrow(completed);
        expect(dependencies.redirect).not.toHaveBeenCalled();
        expect(dependencies.notFound).not.toHaveBeenCalled();
        expect(configured.getCourse).toHaveBeenCalledOnce();
      },
      configured,
    );
    await withinRequest(
      '/tr/courses/foundation',
      async () => {
        await expect(requirePublicResource()).rejects.toThrow('NEXT_REDIRECT');
        expect(dependencies.redirect).toHaveBeenCalledWith('/tr/status/not-found');
      },
      configured,
    );
  });

  it('does not accept an escaped admission in another active store with identical bindings', async () => {
    const firstClient = client();
    const secondClient = client();
    await withinRequest(
      '/tr/courses',
      async (first) => {
        await withinRequest(
          '/tr/courses',
          async (second) => {
            expect(second.request.context).toEqual(first.request.context);
            expect(second.request.site).toEqual(first.request.site);
            expect(second.request.site).not.toBe(first.request.site);
            dependencies.request.mockResolvedValueOnce(first.request);
            await expect(getPublicResource()).rejects.toThrow(unavailable);
            expectNoEducation(firstClient);
            expectNoEducation(secondClient);
          },
          secondClient,
        );
        expect((await getPublicResource()).kind).toBe('catalog');
        expect(firstClient.getCourses).toHaveBeenCalledOnce();
      },
      firstClient,
    );
  });

  it('rejects a cross-store memo hit even when framework headers and bindings are identical', async () => {
    const firstClient = client();
    const secondClient = client();
    await withinRequest(
      '/tr/courses',
      async (first) => {
        const original = await getPublicResource();
        await withinRequest(
          '/tr/courses',
          async () => {
            await expect(getPublicResource()).rejects.toThrow(unavailable);
            expectNoEducation(secondClient);
          },
          secondClient,
          first.incoming,
        );
        expect(await getPublicResource()).toBe(original);
        expect(firstClient.getCourses).toHaveBeenCalledOnce();
        expect(dependencies.request).toHaveBeenCalledOnce();
      },
      firstClient,
    );
  });

  it('keeps concurrent identical bindings independent with ordinary distinct header objects', async () => {
    const firstClient = client();
    const secondClient = client();
    const pending = pause(firstClient.getCourses, catalog);
    let firstRequest!: PublicRequest;
    const first = withinRequest(
      '/tr/courses',
      async (frame) => {
        firstRequest = frame.request;
        const result = await getPublicResource();
        if (result.kind !== 'catalog') throw new Error('Expected catalog');
        expect(result.request).toBe(frame.request);
        return result;
      },
      firstClient,
    );
    await pending.entered;
    const second = await withinRequest(
      '/tr/courses',
      async (frame) => {
        const result = await getPublicResource();
        if (result.kind !== 'catalog') throw new Error('Expected catalog');
        expect(result.request).toBe(frame.request);
        expect(result.request.context).toEqual(firstRequest.context);
        expect(result.request.site).not.toBe(firstRequest.site);
        return result;
      },
      secondClient,
    );
    pending.release();
    expect(await first).not.toBe(second);
    expect(firstClient.getCourses).toHaveBeenCalledOnce();
    expect(secondClient.getCourses).toHaveBeenCalledOnce();
    expect(dependencies.request).toHaveBeenCalledTimes(2);
  });
});
