// @vitest-environment node
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { NextRequest } from 'next/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { middleware as handleMiddleware } from './middleware';
import { validatedTraceparent } from './server/configured-public-client';
import type * as ConfiguredClientModule from './server/configured-public-client';
import { INGRESS_HEADER, mintProvenance } from './server/ingress';
import type { IngressContext } from './server/ingress';
import { createPublicAdmissionRuntime } from './server/public-admission-runtime';
import type { PublicSite } from './server/public-entry';

const { factory, bootstrap, beginAdmission } = vi.hoisted(() => ({
  factory: vi.fn(),
  bootstrap: vi.fn(),
  beginAdmission: vi.fn(),
}));
vi.mock('./server/public-admission', () => ({ beginPublicAdmission: beginAdmission }));
vi.mock('./server/configured-public-client', async (original) => {
  const actual = await original<typeof ConfiguredClientModule>();
  return { ...actual, createConfiguredPublicClient: factory };
});

const secret = randomBytes(32).toString('base64url');
const site: PublicSite = {
  displayName: 'Institution',
  enabledLocales: ['tr', 'en'],
  defaultLocale: 'tr',
  theme: null,
  showPlatformAttribution: true,
};
const trace = '00-1234567890abcdef1234567890abcdef-1234567890abcdef-01';

let runtime: ReturnType<typeof createPublicAdmissionRuntime>;
const nativeBindings = new WeakMap<NextRequest, IngressContext>();

function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage,
    response: Object.assign(new EventEmitter(), {
      destroyed: false,
      writableEnded: false,
    }) as ServerResponse,
  };
}

async function middleware(request: NextRequest) {
  const owner = lifetime();
  return runtime.run(nativeBindings.get(request)!, owner.request, owner.response, async () => {
    try {
      return await handleMiddleware(request);
    } finally {
      owner.response.emit('finish');
    }
  });
}

function incoming(
  target: string,
  stamp: 'valid' | 'missing' | 'forged' = 'valid',
  method = 'GET',
  signal?: AbortSignal,
) {
  const headers = new Headers({
    host: 'substituted.invalid',
    cookie: 'session=private',
    authorization: 'Bearer private',
    'accept-language': 'en',
    'x-tenant-id': 'attacker',
    'x-organization-id': 'attacker',
    'x-locale': 'en',
    'x-learnstack-host': 'attacker',
    'x-learnstack-hop-secret': 'attacker',
    'x-learnstack-visitor-address': 'attacker',
    'x-forwarded-host': 'attacker',
    'x-forwarded-for': 'attacker',
    'x-middleware-request-host': 'attacker',
    'next-url': '/attacker',
    'x-arbitrary': 'private',
    accept: 'text/html',
    traceparent: trace,
  });
  if (stamp !== 'missing')
    headers.set(
      INGRESS_HEADER,
      stamp === 'forged'
        ? 'forged'
        : mintProvenance(
            { host: 'tenant.example:9999', peer: '192.0.2.11', method, target },
            secret,
          ),
    );
  // Pinned Next normalizes the full URL before parsing, then removes _rsc.
  const observed = new URL(('https://substituted.invalid' + target).replace(/\.rsc($|\?)/, '$1'));
  observed.searchParams.delete('_rsc');
  const request = new NextRequest(observed, { headers, method, signal });
  nativeBindings.set(request, { host: 'tenant.example:9999', peer: '192.0.2.11', method, target });
  return request;
}

beforeEach(() => {
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  runtime = createPublicAdmissionRuntime();
  beginAdmission
    .mockReset()
    .mockImplementation((binding: IngressContext) => runtime.holder.begin(binding));
  factory.mockReset().mockReturnValue({ getSite: bootstrap });
  bootstrap.mockReset().mockResolvedValue({ kind: 'success', status: 200, data: site });
});
afterEach(() => {
  runtime.shutdown();
  vi.unstubAllEnvs();
});

describe('Node public middleware', () => {
  it('publishes one detached immutable snapshot only after successful continuation', async () => {
    const request = incoming('/tr/courses');
    const owner = lifetime();
    await runtime.run(nativeBindings.get(request)!, owner.request, owner.response, async () => {
      const response = await handleMiddleware(request);
      expect(response.status).toBe(200);
      expect(response.headers.get('x-middleware-next')).toBe('1');
      const snapshot = runtime.holder.read(nativeBindings.get(request)!).site;
      expect(snapshot).toEqual(site);
      expect(snapshot).not.toBe(site);
      expect(Object.isFrozen(snapshot)).toBe(true);
      expect(Object.isFrozen((snapshot as PublicSite).enabledLocales)).toBe(true);
      expect(bootstrap).toHaveBeenCalledOnce();
      expect(runtime.counts()).toEqual({ active: 1, snapshots: 1 });
    });
    owner.response.emit('finish');
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('refuses a missing or mismatched native context before any API call', async () => {
    const request = incoming('/tr/courses');
    expect((await handleMiddleware(request)).status).toBe(503);
    const owner = lifetime();
    await runtime.run(
      { ...nativeBindings.get(request)!, host: 'other.example' },
      owner.request,
      owner.response,
      async () => {
        const response = await handleMiddleware(request);
        expect(response.status).toBe(503);
        expect(await response.text()).toBe('Service unavailable');
        expect(response.headers.get('retry-after')).toBeNull();
      },
    );
    expect(factory).not.toHaveBeenCalled();
    expect(bootstrap).not.toHaveBeenCalled();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it.each(['aborted', 'finish', 'close'] as const)(
    'prevents late bootstrap publication after native %s',
    async (event) => {
      const request = incoming('/tr/courses');
      const owner = lifetime();
      let resolve!: (value: unknown) => void;
      bootstrap.mockReturnValueOnce(
        new Promise((release) => {
          resolve = release;
        }),
      );
      const pending = runtime.run(nativeBindings.get(request)!, owner.request, owner.response, () =>
        handleMiddleware(request),
      );
      expect(bootstrap).toHaveBeenCalledOnce();
      const signal = factory.mock.calls[0]![1].signal as AbortSignal;
      expect(signal).not.toBe(request.signal);
      expect(signal.aborted).toBe(false);
      (event === 'aborted' ? owner.request : owner.response).emit(event);
      expect(signal.aborted).toBe(true);
      resolve({ kind: 'success', status: 200, data: site });
      const response = await pending;
      expect(response.status).toBe(503);
      expect(await response.text()).toBe('Service unavailable');
      expect(response.headers.get('x-middleware-next')).toBeNull();
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    },
  );

  it('propagates framework cancellation and refuses late success before a native abort event', async () => {
    const cancel = new AbortController();
    const request = incoming('/tr/courses', 'valid', 'GET', cancel.signal);
    const owner = lifetime();
    let resolve!: (value: unknown) => void;
    bootstrap.mockReturnValueOnce(
      new Promise((release) => {
        resolve = release;
      }),
    );
    const pending = runtime.run(nativeBindings.get(request)!, owner.request, owner.response, () =>
      handleMiddleware(request),
    );
    const signal = factory.mock.calls[0]![1].signal as AbortSignal;
    cancel.abort();
    expect(signal.aborted).toBe(true);
    resolve({ kind: 'success', status: 200, data: site });
    expect((await pending).status).toBe(503);
    expect(runtime.counts()).toEqual({ active: 1, snapshots: 0 });
    owner.response.emit('finish');
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('refuses conflicting middleware re-entry without a second bootstrap', async () => {
    const request = incoming('/tr/courses');
    const owner = lifetime();
    await runtime.run(nativeBindings.get(request)!, owner.request, owner.response, async () => {
      expect((await handleMiddleware(request)).status).toBe(200);
      expect((await handleMiddleware(request)).status).toBe(503);
      expect(bootstrap).toHaveBeenCalledOnce();
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    });
  });

  it.each([
    [429, 'rate_limited'],
    [503, 'dependency_unavailable'],
    [503, 'audit_unavailable'],
  ] as const)('forwards only bounded integer Retry-After for %s/%s', async (status, code) => {
    for (const retryAfter of [
      0,
      1,
      60,
      -1,
      61,
      0.5,
      Number.MAX_SAFE_INTEGER,
      NaN,
      Infinity,
      '30',
      'Wed, 21 Oct 2015 07:28:00 GMT',
      undefined,
    ]) {
      bootstrap.mockResolvedValueOnce({
        kind: 'api-error',
        status,
        error: { code, retryAfter },
        problem: { detail: secret },
      });
      const response = await middleware(incoming('/tr/courses'));
      expect(response.status).toBe(status);
      expect(response.headers.get('retry-after')).toBe(
        typeof retryAfter === 'number' &&
          Number.isInteger(retryAfter) &&
          retryAfter >= 0 &&
          retryAfter <= 60
          ? String(retryAfter)
          : null,
      );
      expect(response.headers.get('cache-control')).toBe('no-store');
      expect(await response.text()).toBe(
        status === 429 ? 'Too many requests' : 'Service unavailable',
      );
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    }
  });

  it.each([
    [404, 'not_found', 404],
    [429, 'dependency_unavailable', 429],
    [503, 'rate_limited', 503],
    [503, 'not_found', 503],
    [500, 'dependency_unavailable', 503],
    [400, 'validation_failed', 503],
  ] as const)('omits Retry-After from unsupported %s/%s errors', async (status, code, expected) => {
    bootstrap.mockResolvedValueOnce({ kind: 'api-error', status, error: { code, retryAfter: 30 } });
    const response = await middleware(incoming('/tr/courses'));
    expect(response.status).toBe(expected);
    expect(response.headers.get('retry-after')).toBeNull();
    expect(response.headers.get('x-middleware-next')).toBeNull();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it.each([404, 429, 503] as const)(
    'owns bodyless HEAD bootstrap %s before any rendering',
    async (status) => {
      bootstrap.mockResolvedValueOnce({
        kind: 'api-error',
        status,
        error: {
          code:
            status === 404
              ? 'not_found'
              : status === 429
                ? 'rate_limited'
                : 'dependency_unavailable',
          retryAfter: 30,
        },
      });
      const response = await middleware(incoming('/tr/courses', 'valid', 'HEAD'));
      expect(response.status).toBe(status);
      expect(await response.text()).toBe('');
      expect(response.headers.get('cache-control')).toBe('no-store');
      expect(response.headers.get('content-type')).toBe('text/plain; charset=utf-8');
      expect(response.headers.get('retry-after')).toBe(status === 404 ? null : '30');
      expect(response.headers.get('x-middleware-next')).toBeNull();
    },
  );

  it.each(['/courses', '/fr/courses', '/TR/courses'])(
    'makes redirect/refusal terminal with no retained snapshot: %s',
    async (target) => {
      const request = incoming(target);
      const owner = lifetime();
      await runtime.run(nativeBindings.get(request)!, owner.request, owner.response, async () => {
        const response = await handleMiddleware(request);
        expect([307, 308, 404]).toContain(response.status);
        expect(response.headers.get('x-middleware-next')).toBeNull();
        expect(runtime.counts()).toEqual({ active: 1, snapshots: 0 });
        expect(() => runtime.holder.read(nativeBindings.get(request)!)).toThrow(
          'Public admission context unavailable',
        );
      });
      owner.response.emit('finish');
      expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    },
  );

  it('continues only the exact admitted status namespace with a no-store 404', async () => {
    const response = await middleware(incoming('/tr/status/not-found?slug=private'));
    expect(response.status).toBe(404);
    expect(response.headers.get('x-middleware-next')).toBe('1');
    expect(response.headers.get('location')).toBeNull();
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(bootstrap).toHaveBeenCalledTimes(1);
    for (const path of ['/fr/status/not-found', '/tr/status/other', '/tr/status/not-found/extra']) {
      const refused = await middleware(incoming(path));
      expect(refused.status).toBe(404);
      expect(refused.headers.get('x-middleware-next')).toBeNull();
    }
  });

  it.each(['missing', 'forged'] as const)(
    'refuses %s provenance before bootstrap',
    async (stamp) => {
      const response = await middleware(incoming('/tr/courses', stamp));
      expect(response.status).toBe(404);
      expect(await response.text()).toBe('Not found');
      expect(factory).not.toHaveBeenCalled();
      expect(bootstrap).not.toHaveBeenCalled();
    },
  );

  it('binds the verified target rather than accepting a valid stamp for another path', async () => {
    const request = incoming('/tr/courses');
    request.headers.set(
      INGRESS_HEADER,
      mintProvenance(
        { host: 'tenant.example:9999', peer: '192.0.2.11', method: 'GET', target: '/en/courses' },
        secret,
      ),
    );
    expect((await middleware(request)).status).toBe(404);
    expect(factory).not.toHaveBeenCalled();
  });

  it('bootstraps once before disabled-locale refusal and never reuses another request', async () => {
    expect((await middleware(incoming('/fr/courses'))).status).toBe(404);
    expect(bootstrap).toHaveBeenCalledTimes(1);
    bootstrap.mockResolvedValueOnce({
      kind: 'success',
      status: 200,
      data: { ...site, enabledLocales: ['fr'], defaultLocale: 'fr' },
    });
    expect((await middleware(incoming('/fr/courses'))).headers.get('x-middleware-next')).toBe('1');
    expect(bootstrap).toHaveBeenCalledTimes(2);
  });

  it.each([
    '?',
    '?x=%20',
    '?x=~&x=%2f',
    '?x',
    '?x=%41',
    '?_rsc=raw',
    "?x='&x=%27&empty=&blank&x=tail.rsc",
    '?x=tail.rsc?extra=1',
    '?x=tail.rsc&_rsc=a&_rsc=b',
    '?locale=en&next=https://evil.example',
  ])('redirects on verified host/default retaining ordered inert query data %s', async (query) => {
    const response = await middleware(incoming('/courses' + query));
    expect(response.status).toBe(307);
    const destination = new URL(response.headers.get('location') ?? '');
    expect(destination.origin).toBe('https://tenant.example:3000');
    expect(destination.pathname).toBe('/tr/courses');
    expect([...destination.searchParams]).toEqual([
      ...new URL('/courses' + query, 'https://inert.invalid').searchParams,
    ]);
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.get('set-cookie')).toBeNull();
  });

  it('only canonicalizes an enabled locale and retains its resource path', async () => {
    const response = await middleware(incoming('/TR/courses/foundation/lessons/intro?x=%20'));
    expect(response.status).toBe(308);
    expect(response.headers.get('location')).toBe(
      'https://tenant.example:3000/tr/courses/foundation/lessons/intro?x=%20',
    );
    expect((await middleware(incoming('/FR/courses'))).status).toBe(404);
  });

  it('rebuilds downstream request headers, retaining the envelope only as framework request context', async () => {
    const request = incoming('/tr/courses?locale=en');
    // The actual Node adapter hides Flight inputs before invoking middleware.
    // Their later restoration is covered by the real production fixture.
    const response = await middleware(request);
    expect(response.headers.get('x-middleware-next')).toBe('1');
    const overrides = response.headers.get('x-middleware-override-headers')!.split(',');
    expect(new Set(overrides)).toEqual(new Set(['accept', 'traceparent', INGRESS_HEADER]));
    expect(response.headers.get('x-middleware-request-' + INGRESS_HEADER)).toBe(
      request.headers.get(INGRESS_HEADER),
    );
    // Next consumes override headers internally; the production fixture proves
    // they do not reach an HTTP response. No ordinary response carrier is emitted.
    expect(response.headers.get(INGRESS_HEADER)).toBeNull();
    expect(response.headers.get('set-cookie')).toBeNull();
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(factory).toHaveBeenCalledWith(request.headers.get(INGRESS_HEADER), {
      traceparent: trace,
      signal: expect.any(AbortSignal),
    });
    expect(bootstrap).toHaveBeenCalledTimes(1);
  });

  it.each(['/tr/courses.rsc', '/tr/courses.prefetch.rsc', '/tr/courses/foundation.rsc'])(
    'refuses raw suffix route aliases despite their framework projection: %s',
    async (target) => {
      expect((await middleware(incoming(target))).status).toBe(404);
      expect(bootstrap).toHaveBeenCalledTimes(1);
    },
  );

  it.each([null, 'invalid', trace.replace('-01', '-xyz')])(
    'shares a fresh request-local trace between bootstrap and rendering for %s',
    async (incomingTrace) => {
      const request = incoming('/tr/courses');
      if (incomingTrace === null) request.headers.delete('traceparent');
      else request.headers.set('traceparent', incomingTrace);
      const response = await middleware(request);
      const downstream = response.headers.get('x-middleware-request-traceparent');
      expect(validatedTraceparent(downstream)).toBe(downstream);
      expect(downstream).not.toBeNull();
      expect(factory).toHaveBeenCalledWith(request.headers.get(INGRESS_HEADER), {
        traceparent: downstream,
        signal: expect.any(AbortSignal),
      });
      expect(response.headers.get('traceparent')).toBeNull();
    },
  );

  it.each(['/studio', '/portal'])(
    'requires live bootstrap even for exact scaffold %s',
    async (target) => {
      expect((await middleware(incoming(target))).headers.get('x-middleware-next')).toBe('1');
      bootstrap.mockResolvedValueOnce({
        kind: 'api-error',
        status: 404,
        error: { code: 'not_found' },
      });
      expect((await middleware(incoming(target))).status).toBe(404);
      expect(bootstrap).toHaveBeenCalledTimes(2);
    },
  );

  it('preserves a validated API rate refusal and Retry-After without echoing provider data', async () => {
    bootstrap.mockResolvedValueOnce({
      kind: 'api-error',
      status: 429,
      error: { code: 'rate_limited', retryAfter: 60 },
      problem: { detail: secret },
    });
    const response = await middleware(incoming('/'));
    expect(response.status).toBe(429);
    expect(response.headers.get('retry-after')).toBe('60');
    expect(await response.text()).toBe('Too many requests');
    expect(response.headers.get('cache-control')).toBe('no-store');
  });

  it.each(['transport-error', 'invalid-response', 'cancelled', 'invalid-request'])(
    'masks %s as a bounded no-store dependency response',
    async (kind) => {
      bootstrap.mockResolvedValueOnce({ kind });
      const response = await middleware(incoming('/'));
      expect(response.status).toBe(503);
      expect(await response.text()).toBe('Service unavailable');
      expect(response.headers.get('cache-control')).toBe('no-store');
      expect(response.headers.get('set-cookie')).toBeNull();
    },
  );

  it('masks private configuration/provider exceptions and malformed bootstrap', async () => {
    factory.mockImplementationOnce(() => {
      throw new Error(secret);
    });
    expect(await (await middleware(incoming('/'))).text()).toBe('Service unavailable');
    bootstrap.mockResolvedValueOnce({
      kind: 'success',
      status: 200,
      data: { ...site, enabledLocales: ['en'], defaultLocale: 'tr' },
    });
    expect((await middleware(incoming('/'))).status).toBe(503);
  });
});
