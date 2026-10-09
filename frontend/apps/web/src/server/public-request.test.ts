// @vitest-environment node
import { randomBytes } from 'node:crypto';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createConfiguredPublicClient } from './configured-public-client';
import type { ConfiguredPublicClient, publicTraceparent } from './configured-public-client';
import { INGRESS_HEADER, mintProvenance, verifyProvenance } from './ingress';
import type { PublicSite } from './public-entry';
import { getPublicRequest } from './public-request';

const mocks = vi.hoisted(() => ({ headers: vi.fn<() => Promise<Headers>>() }));
vi.mock('next/headers', () => ({ headers: mocks.headers }));
vi.mock('./configured-public-client', async (importOriginal) => ({
  ...(await importOriginal<{ publicTraceparent: typeof publicTraceparent }>()),
  createConfiguredPublicClient: vi.fn(),
}));
// Real memoization has dedicated controls and a production RSC proof. Admission
// cases deliberately execute a fresh loader for every invocation.
vi.mock('./request-memo', () => ({
  requestMemo: (load: (incoming: Headers) => Promise<unknown>) => async () =>
    load(await mocks.headers()),
}));

const secret = randomBytes(32).toString('base64url');
const traceparent = '00-11223344556677889900aabbccddeeff-1234567890abcdef-01';
const createClient = vi.mocked(createConfiguredPublicClient);
const context = {
  host: 'first.example:3000',
  peer: '203.0.113.7',
  method: 'GET',
  target: '/tr-TR/courses?locale=en',
};

function site(enabledLocales = ['tr-TR', 'en', 'ar'], defaultLocale = 'tr-TR'): PublicSite {
  return {
    displayName: 'First institution',
    enabledLocales,
    defaultLocale,
    theme: null,
    showPlatformAttribution: true,
  };
}

function client(configuration = site()) {
  return {
    getSite: vi
      .fn<ConfiguredPublicClient['getSite']>()
      .mockResolvedValue({ kind: 'success', status: 200, data: configuration }),
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

beforeEach(() => {
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  mocks.headers.mockReset();
  createClient.mockReset();
});
afterEach(() => vi.unstubAllEnvs());

describe('shared verified public request admission', () => {
  it('uses authentic envelope authority, live enabled membership and signed locale only', async () => {
    const headers = incoming();
    const configured = client();
    mocks.headers.mockResolvedValue(headers);
    createClient.mockReturnValue(configured);

    const request = await getPublicRequest();

    expect(request).toEqual({
      context,
      client: configured,
      site: site(),
      locale: 'tr-TR',
      route: { kind: 'catalog', path: '/tr-TR/courses' },
    });
    expect(createClient).toHaveBeenCalledWith(headers.get(INGRESS_HEADER), { traceparent });
    expect(createClient).toHaveBeenCalledOnce();
    expect(configured.getSite).toHaveBeenCalledOnce();
    expect(configured.getCourses).not.toHaveBeenCalled();
    expect(configured.getCourse).not.toHaveBeenCalled();
    expect(configured.getLesson).not.toHaveBeenCalled();
  });

  it.each([null, '', 'unsigned-host', 'v1.invalid.invalid'])(
    'refuses %s before configured caller or private configuration',
    async (envelope) => {
      const headers = incoming();
      if (envelope === null) headers.delete(INGRESS_HEADER);
      else headers.set(INGRESS_HEADER, envelope);
      mocks.headers.mockResolvedValue(headers);
      createClient.mockImplementation(() => {
        throw new Error('Configuration must not be read');
      });

      expect(await getPublicRequest()).toBeNull();
      expect(createClient).not.toHaveBeenCalled();
    },
  );

  it.each([
    '/favicon.ico',
    '/favicon.ico?x=1',
    '/api/healthz',
    '/_next/static/missing.js',
    '/_next/webpack-hmr',
  ])('never bootstraps UI from a transport fallback: %s', async (target) => {
    mocks.headers.mockResolvedValue(incoming(target));
    createClient.mockImplementation(() => {
      throw new Error('Transport path cannot construct a caller');
    });
    expect(await getPublicRequest()).toBeNull();
    expect(createClient).not.toHaveBeenCalled();
  });

  it('rejects forged and repeated authentic stamps before caller construction', async () => {
    const stamp = mintProvenance(context, secret);
    for (const envelope of [
      mintProvenance(context, randomBytes(32).toString('base64url')),
      `${stamp}, ${stamp}`,
    ]) {
      mocks.headers.mockResolvedValue(new Headers({ [INGRESS_HEADER]: envelope }));
      expect(await getPublicRequest()).toBeNull();
    }
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
    'does not expose a request when live entry refuses or redirects %s',
    async (target, locales, defaultLocale) => {
      mocks.headers.mockResolvedValue(incoming(target));
      const configured = client(site(locales, defaultLocale));
      createClient.mockReturnValue(configured);
      expect(await getPublicRequest()).toBeNull();
      expect(configured.getSite).toHaveBeenCalledOnce();
    },
  );

  it.each(['/ar/courses', '/tr-TR/status/not-found', '/studio', '/portal'])(
    'admits enabled unsupported UI locales, fixed status and scaffolds: %s',
    async (target) => {
      mocks.headers.mockResolvedValue(incoming(target));
      createClient.mockReturnValue(client());
      const request = await getPublicRequest();
      expect(request?.locale).toBe(
        target === '/studio' || target === '/portal' ? null : target.split('/')[1],
      );
      expect(request?.context.target).toBe(target);
    },
  );

  it('bounds configuration exceptions, missing clients and unsuccessful bootstrap results', async () => {
    mocks.headers.mockResolvedValue(incoming());
    createClient.mockImplementationOnce(() => {
      throw new Error('private configuration');
    });
    expect(await getPublicRequest()).toBeNull();
    createClient.mockReturnValueOnce(null);
    expect(await getPublicRequest()).toBeNull();
    for (const result of [
      { kind: 'transport-error' },
      { kind: 'invalid-response', reason: 'invalid-success' },
    ] as const) {
      const configured = client();
      configured.getSite.mockResolvedValue(result);
      createClient.mockReturnValueOnce(configured);
      expect(await getPublicRequest()).toBeNull();
    }
    const configured = client();
    configured.getSite.mockRejectedValue(new Error('private diagnostic'));
    createClient.mockReturnValueOnce(configured);
    expect(await getPublicRequest()).toBeNull();
  });

  it('keeps overlapping host and locale admissions independent and re-reads later requests', async () => {
    let releaseFirst!: (value: Awaited<ReturnType<ConfiguredPublicClient['getSite']>>) => void;
    const first = client();
    first.getSite.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          releaseFirst = resolve;
        }),
    );
    const secondSite = { ...site(['en'], 'en'), displayName: 'Second institution' };
    const second = client(secondSite);
    mocks.headers
      .mockResolvedValueOnce(incoming())
      .mockResolvedValueOnce(incoming('/en/courses', 'second.example:3000'));
    createClient.mockImplementation((envelope) =>
      verifyProvenance(envelope, secret)?.host === context.host ? first : second,
    );

    const pendingFirst = getPublicRequest();
    const pendingSecond = getPublicRequest();
    expect((await pendingSecond)?.site).toBe(secondSite);
    releaseFirst({ kind: 'success', status: 200, data: site() });
    const firstRequest = await pendingFirst;
    expect(firstRequest?.locale).toBe('tr-TR');
    expect(firstRequest?.context.host).toBe(context.host);
    expect(first.getSite).toHaveBeenCalledOnce();
    expect(second.getSite).toHaveBeenCalledOnce();

    // A later live disablement must refuse rather than reuse the earlier admission.
    mocks.headers.mockResolvedValueOnce(incoming());
    first.getSite.mockResolvedValueOnce({ kind: 'success', status: 200, data: site(['en'], 'en') });
    expect(await getPublicRequest()).toBeNull();
    expect(first.getSite).toHaveBeenCalledTimes(2);
  });
});
