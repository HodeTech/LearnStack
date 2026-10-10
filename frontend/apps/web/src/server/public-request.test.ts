// @vitest-environment node
import { AsyncLocalStorage } from 'node:async_hooks';
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createConfiguredPublicClient } from './configured-public-client';
import type { ConfiguredPublicClient, publicTraceparent } from './configured-public-client';
import { INGRESS_HEADER, mintProvenance, verifyProvenance } from './ingress';
import type { IngressContext } from './ingress';
import { createPublicAdmissionRuntime } from './public-admission-runtime';
import type { PublicSite } from './public-entry';
import { assertPublicRequestActive, getPublicRequest } from './public-request';

const mocks = vi.hoisted(() => ({
  headers: vi.fn<() => Promise<Headers>>(),
  readAdmission: vi.fn(),
}));
vi.mock('next/headers', () => ({ headers: mocks.headers }));
vi.mock('./public-admission', () => ({ readPublicAdmission: mocks.readAdmission }));
vi.mock('./configured-public-client', async (importOriginal) => ({
  ...(await importOriginal<{ publicTraceparent: typeof publicTraceparent }>()),
  createConfiguredPublicClient: vi.fn(),
}));
// Keep the real requestMemo. This test ALS supplies distinct framework header
// identities while the independent production runtime owns native admission.
const headerScope = new AsyncLocalStorage<Headers>();
const secret = randomBytes(32).toString('base64url');
const traceparent = '00-11223344556677889900aabbccddeeff-1234567890abcdef-01';
const createClient = vi.mocked(createConfiguredPublicClient);
const context: IngressContext = {
  host: 'first.example:3000',
  peer: '203.0.113.7',
  method: 'GET',
  target: '/tr-TR/courses?locale=en',
};
const unavailable = 'Public admission context unavailable';
const completed = 'Public admission request completed';
let runtime: ReturnType<typeof createPublicAdmissionRuntime>;

function site(enabledLocales = ['tr-TR', 'en', 'ar'], defaultLocale = 'tr-TR'): PublicSite {
  return {
    displayName: 'First institution',
    enabledLocales,
    defaultLocale,
    theme: null,
    showPlatformAttribution: true,
  };
}

function client() {
  return {
    getSite: vi
      .fn<ConfiguredPublicClient['getSite']>()
      .mockRejectedValue(new Error('RSC must never bootstrap')),
    getCourses: vi.fn<ConfiguredPublicClient['getCourses']>(),
    getCourse: vi.fn<ConfiguredPublicClient['getCourse']>(),
    getLesson: vi.fn<ConfiguredPublicClient['getLesson']>(),
  };
}

function incoming(target = context.target, host = context.host): Headers {
  return new Headers({
    [INGRESS_HEADER]: mintProvenance({ ...context, target, host }, secret),
    host: 'attacker.example',
    'x-learnstack-host': 'attacker.example',
    'x-forwarded-host': 'attacker.example',
    'accept-language': 'en',
    cookie: 'locale=en; session=attacker',
    'x-next-intl-locale': 'en',
    traceparent,
  });
}

function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false }) as IncomingMessage,
    response: Object.assign(new EventEmitter(), {
      destroyed: false,
      writableEnded: false,
    }) as ServerResponse,
  };
}

function admitted<T>(
  headers: Headers,
  configuration: PublicSite,
  work: (owner: ReturnType<typeof lifetime>) => Promise<T>,
  nativeBinding?: IngressContext,
): Promise<T> {
  const owner = lifetime();
  const binding = nativeBinding ?? verifyProvenance(headers.get(INGRESS_HEADER), secret)!;
  return headerScope.run(headers, () =>
    runtime.run(binding, owner.request, owner.response, async () => {
      runtime.holder.begin(binding).publish(configuration);
      try {
        return await work(owner);
      } finally {
        owner.response.emit('finish');
      }
    }),
  );
}

function deferred() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

beforeEach(() => {
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  runtime = createPublicAdmissionRuntime();
  mocks.headers.mockReset().mockImplementation(async () => {
    const headers = headerScope.getStore();
    if (!headers) throw new Error('Test framework header scope missing');
    return headers;
  });
  mocks.readAdmission
    .mockReset()
    .mockImplementation((binding: IngressContext) => runtime.holder.read(binding));
  createClient.mockReset().mockImplementation(() => client());
});
afterEach(() => {
  runtime.shutdown();
  vi.unstubAllEnvs();
});

describe('shared verified public request admission', () => {
  it('consumes the immutable middleware snapshot and authentic signed locale with zero site calls', async () => {
    const headers = incoming();
    const configured = client();
    createClient.mockReturnValue(configured);
    await admitted(headers, site(), async () => {
      const [first, second] = await Promise.all([getPublicRequest(), getPublicRequest()]);
      expect(second).toBe(first);
      expect(first).toEqual({
        context,
        client: configured,
        site: site(),
        locale: 'tr-TR',
        route: { kind: 'catalog', path: '/tr-TR/courses' },
      });
      expect(Object.isFrozen(first!.site)).toBe(true);
      expect(Object.isFrozen(first!.site.enabledLocales)).toBe(true);
      const native = runtime.holder.read(context);
      expect(first!.site).toBe(native.site);
      expect(createClient).toHaveBeenCalledWith(headers.get(INGRESS_HEADER), {
        traceparent,
        signal: native.signal,
      });
      expect(createClient).toHaveBeenCalledOnce();
      expect(configured.getSite).not.toHaveBeenCalled();
      expect(configured.getCourses).not.toHaveBeenCalled();
      expect(configured.getCourse).not.toHaveBeenCalled();
      expect(configured.getLesson).not.toHaveBeenCalled();
    });
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it.each([null, '', 'unsigned-host', 'v1.invalid.invalid'])(
    'refuses %s before context lookup or configuration',
    async (envelope) => {
      const headers = incoming();
      if (envelope === null) headers.delete(INGRESS_HEADER);
      else headers.set(INGRESS_HEADER, envelope);
      expect(await headerScope.run(headers, getPublicRequest)).toBeNull();
      expect(mocks.readAdmission).not.toHaveBeenCalled();
      expect(createClient).not.toHaveBeenCalled();
    },
  );

  it.each([
    '/favicon.ico',
    '/favicon.ico?x=1',
    '/api/healthz',
    '/_next/static/missing.js',
    '/_next/webpack-hmr',
  ])('keeps exempt transport fallback tenant-free: %s', async (target) => {
    expect(await headerScope.run(incoming(target), getPublicRequest)).toBeNull();
    expect(mocks.readAdmission).not.toHaveBeenCalled();
    expect(createClient).not.toHaveBeenCalled();
  });

  it('rejects forged and repeated authentic stamps before native context access', async () => {
    const stamp = mintProvenance(context, secret);
    for (const envelope of [
      mintProvenance(context, randomBytes(32).toString('base64url')),
      `${stamp}, ${stamp}`,
    ]) {
      expect(
        await headerScope.run(new Headers({ [INGRESS_HEADER]: envelope }), getPublicRequest),
      ).toBeNull();
    }
    expect(mocks.readAdmission).not.toHaveBeenCalled();
    expect(createClient).not.toHaveBeenCalled();
  });

  it.each([
    ['/en/courses', ['tr-TR'], 'tr-TR'],
    ['/tr-TR/courses', [], 'tr-TR'],
    ['/tr-TR/courses', ['tr-TR', 'tr-TR'], 'tr-TR'],
    ['/tr-TR/courses', ['tr-TR'], 'en'],
    ['/tr-TR/courses', ['TR-tr'], 'TR-tr'],
    ['/TR-tr/courses', ['tr-TR'], 'tr-TR'],
    ['/tr-TR/unknown', ['tr-TR'], 'tr-TR'],
    ['/', ['tr-TR'], 'tr-TR'],
  ])(
    'fails closed on an impossible ready snapshot entry: %s',
    async (target, locales, defaultLocale) => {
      await admitted(incoming(target), site(locales, defaultLocale), async () => {
        await expect(getPublicRequest()).rejects.toThrow(unavailable);
      });
      expect(createClient).toHaveBeenCalledOnce();
      const configured = createClient.mock.results[0]!.value!;
      expect(configured.getSite).not.toHaveBeenCalled();
      expect(configured.getCourses).not.toHaveBeenCalled();
      expect(configured.getCourse).not.toHaveBeenCalled();
      expect(configured.getLesson).not.toHaveBeenCalled();
    },
  );

  it.each(['/ar/courses', '/tr-TR/status/not-found', '/studio', '/portal'])(
    'consumes admission for supported entry %s',
    async (target) => {
      await admitted(incoming(target), site(), async () => {
        const request = await getPublicRequest();
        expect(request?.locale).toBe(
          target === '/studio' || target === '/portal' ? null : target.split('/')[1],
        );
        expect(request?.context.target).toBe(target);
      });
      expect(createClient).toHaveBeenCalledOnce();
      expect(createClient.mock.results[0]!.value!.getSite).not.toHaveBeenCalled();
    },
  );

  it.each(['missing', 'pending', 'loading', 'refused'] as const)(
    'throws a sanitized active %s context defect instead of an API 404',
    async (phase) => {
      const headers = incoming();
      if (phase === 'missing')
        await expect(headerScope.run(headers, getPublicRequest)).rejects.toThrow(unavailable);
      else {
        const owner = lifetime();
        await headerScope.run(headers, () =>
          runtime.run(context, owner.request, owner.response, async () => {
            if (phase !== 'pending') {
              const writing = runtime.holder.begin(context);
              if (phase === 'refused') writing.refuse();
            }
            await expect(getPublicRequest()).rejects.toThrow(unavailable);
          }),
        );
        owner.response.emit('finish');
      }
      expect(createClient).not.toHaveBeenCalled();
    },
  );

  it('refuses a ready context bound to another signed request before caller construction', async () => {
    await admitted(
      incoming(),
      site(),
      async () => {
        await expect(getPublicRequest()).rejects.toThrow(unavailable);
      },
      { ...context, host: 'other.example:3000' },
    );
    expect(createClient).not.toHaveBeenCalled();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it.each(['throw', 'null'] as const)(
    'sanitizes %s configured-client failures without a fallback site read',
    async (mode) => {
      if (mode === 'throw')
        createClient.mockImplementationOnce(() => {
          throw new Error('private configuration secret');
        });
      else createClient.mockReturnValueOnce(null);
      await admitted(incoming(), site(), async () => {
        await expect(getPublicRequest()).rejects.toThrow(unavailable);
      });
      expect(createClient).toHaveBeenCalledOnce();
    },
  );

  it('guards a cached success again after native finish instead of returning retained data', async () => {
    const configured = client();
    createClient.mockReturnValue(configured);
    await admitted(incoming(), site(), async (owner) => {
      const first = await getPublicRequest();
      expect(first).not.toBeNull();
      owner.response.emit('finish');
      await expect(getPublicRequest()).rejects.toThrow(completed);
      expect(() => assertPublicRequestActive(first!)).toThrow(completed);
      expect(configured.getSite).not.toHaveBeenCalled();
      expect(configured.getCourses).not.toHaveBeenCalled();
    });
    expect(createClient).toHaveBeenCalledOnce();
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('rechecks native lifetime after the asynchronous memo result resolves', async () => {
    await admitted(incoming(), site(), async (owner) => {
      const first = await getPublicRequest();
      const cached = getPublicRequest();
      owner.response.emit('finish');
      await expect(cached).rejects.toThrow(completed);
      expect(first).not.toBeNull();
    });
    expect(createClient).toHaveBeenCalledOnce();
  });

  it('rejects an escaped request value inside an identical neighbor by snapshot identity', async () => {
    const firstHeaders = incoming();
    const secondHeaders = incoming();
    const held = deferred();
    const started = deferred();
    let escaped: NonNullable<Awaited<ReturnType<typeof getPublicRequest>>>;
    const first = admitted(firstHeaders, site(), async () => {
      escaped = (await getPublicRequest())!;
      started.release();
      await held.promise;
      assertPublicRequestActive(escaped);
    });
    await started.promise;
    await admitted(secondHeaders, site(), async () => {
      const neighbor = (await getPublicRequest())!;
      expect(neighbor.context).toEqual(escaped.context);
      expect(neighbor.site).not.toBe(escaped.site);
      expect(() => assertPublicRequestActive(escaped)).toThrow(unavailable);
      assertPublicRequestActive(neighbor);
    });
    held.release();
    await first;
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });

  it('isolates eight overlapping identical envelopes and reads a fresh snapshot on the next request', async () => {
    const gate = deferred();
    const allStarted = deferred();
    const records: NonNullable<Awaited<ReturnType<typeof getPublicRequest>>>[] = [];
    const pending = Array.from({ length: 8 }, (_, index) =>
      admitted(incoming(), { ...site(), displayName: `Institution ${index}` }, async () => {
        const request = (await getPublicRequest())!;
        records.push(request);
        if (records.length === 8) allStarted.release();
        await gate.promise;
        expect(await getPublicRequest()).toBe(request);
        expect(request.site.displayName).toBe(`Institution ${index}`);
        assertPublicRequestActive(request);
      }),
    );
    await allStarted.promise;
    expect(runtime.counts()).toEqual({ active: 8, snapshots: 8 });
    gate.release();
    await Promise.all(pending);
    expect(records).toHaveLength(8);
    expect(new Set(records.map((request) => request.site)).size).toBe(8);
    expect(createClient).toHaveBeenCalledTimes(8);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
    await admitted(incoming(), { ...site(), displayName: 'Updated institution' }, async () => {
      expect((await getPublicRequest())?.site.displayName).toBe('Updated institution');
    });
    expect(createClient).toHaveBeenCalledTimes(9);
    for (const result of createClient.mock.results)
      expect(result.value!.getSite).not.toHaveBeenCalled();
  });

  it('keeps concurrent hosts and locales independent without looking up native contexts by headers', async () => {
    const gate = deferred();
    const first = admitted(incoming(), site(), async () => {
      const request = (await getPublicRequest())!;
      await gate.promise;
      expect(request.context.host).toBe(context.host);
      expect(request.locale).toBe('tr-TR');
      expect(request.site.displayName).toBe('First institution');
      assertPublicRequestActive(request);
    });
    const second = admitted(
      incoming('/en/courses', 'second.example:3000'),
      { ...site(['en'], 'en'), displayName: 'Second institution' },
      async () => {
        const request = (await getPublicRequest())!;
        expect(request.context.host).toBe('second.example:3000');
        expect(request.locale).toBe('en');
        expect(request.site.displayName).toBe('Second institution');
        assertPublicRequestActive(request);
      },
    );
    await second;
    gate.release();
    await first;
    expect(createClient).toHaveBeenCalledTimes(2);
    expect(runtime.counts()).toEqual({ active: 0, snapshots: 0 });
  });
});
