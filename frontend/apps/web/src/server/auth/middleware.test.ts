// @vitest-environment node

import { NextRequest, NextResponse } from 'next/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { middleware } from '../../middleware';
import { bffFixture } from '../../test/bff-fixture';
import type * as ingress from '../ingress';

const f = vi.hoisted(() => ({
  enabled: false,
  published: false,
  site: {
    displayName: 'School',
    defaultLocale: 'tr',
    enabledLocales: ['tr', 'en'],
    theme: null,
    showPlatformAttribution: true,
  },
  getSite: vi.fn(),
  bff: vi.fn(),
  flight: vi.fn(),
  refuse: vi.fn(),
}));
vi.mock('../configured-public-client', () => ({
  createConfiguredPublicClient: () => ({ getSite: f.getSite }),
  publicTraceparent: () => 'trace',
}));
vi.mock('../ingress', async (original) => ({
  ...(await original<typeof ingress>()),
  verifyNextProvenance: (
    _: string,
    _secret: string,
    binding: { method: string; target: string },
  ) => ({ ...binding, host: 'institution.example.test:3000', peer: '203.0.113.1' }),
}));
vi.mock('../public-admission', () => ({
  beginPublicAdmission: () => ({
    signal: new AbortController().signal,
    assertActive() {},
    publish() {
      f.published = true;
    },
    refuse: f.refuse,
  }),
}));
vi.mock('./request-owner', () => ({ bffRequestOwner: () => ({ flight: f.flight }) }));
vi.mock('./session-owner', () => ({ bffSessionEnabled: () => f.enabled }));
vi.mock('./http-admission', () => ({ admitBffHttp: f.bff }));

const incoming = (path: string, headers: Record<string, string> = {}) =>
  new NextRequest('https://institution.example.test:3000' + path, {
    headers: { 'x-learnstack-ingress-provenance': 'owned', ...headers },
  });
beforeEach(() => {
  f.enabled = false;
  f.published = false;
  vi.resetAllMocks();
  f.flight.mockReturnValue(false);
  f.getSite.mockResolvedValue({ kind: 'success', data: f.site });
  f.bff.mockImplementation(async () => {
    expect(f.published).toBe(true);
    return { response: null, session: null, cookies: [], unavailable: false };
  });
});
describe('atomic host-first BFF middleware activation', () => {
  it.each([
    ['anonymous', {}],
    ['authorization', { authorization: 'Bearer private-header-canary' }],
    ['malformed-cookie', { cookie: '__Host-ls-session=private-cookie-canary' }],
  ] as const)(
    'enabled public %s input remains readable through the actual protocol',
    async (kind, headers) => {
      f.enabled = true;
      const actual = bffFixture();
      f.bff.mockImplementation(async (request) => ({
        response: null,
        ...(await actual.http.project({
          host: 'institution.example.test:3000',
          source: '203.0.113.1',
          method: request.method,
          target: '/en/courses',
          headers: request.headers,
          body: null,
          signal: request.signal,
        })),
      }));
      try {
        const response = await middleware(
          incoming('/en/courses', { ...headers, 'sec-fetch-dest': 'document' }),
        );
        expect(response.status).toBe(200);
        expect(response.headers.get('x-middleware-next')).toBe('1');
        expect(response.headers.getSetCookie()).toEqual(
          kind === 'malformed-cookie'
            ? ['__Host-ls-session=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax']
            : [],
        );
        expect(actual.account).not.toHaveBeenCalled();
        expect(actual.provider.begin).not.toHaveBeenCalled();
      } finally {
        await actual.store.stop();
      }
    },
  );
  it.each(['/studio', '/en/courses'])(
    'a recoverable account outage at %s does not redirect to sign-in or clear cookies',
    async (path) => {
      f.enabled = true;
      f.bff.mockResolvedValue({ response: null, session: null, cookies: [], unavailable: true });
      const response = await middleware(incoming(path));
      expect(response.status).toBe(path === '/studio' ? 503 : 200);
      expect(response.headers.get('location')).toBeNull();
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(response.headers.get('x-frame-options')).toBe('DENY');
      if (path === '/studio') {
        const html = await response.text();
        expect(html).toContain('Hesap erişimi kullanılamıyor');
        expect(html).toContain('href="/studio"');
        expect(html).toContain('href="/api/auth/signin?returnTo=%2Fstudio"');
        expect(html).not.toContain('csrfToken');
      }
    },
  );
  it('unknown host refuses before mode selection, cookies, owner or provider work', async () => {
    f.enabled = true;
    f.getSite.mockResolvedValue({ kind: 'api-error', status: 404, error: { code: 'not_found' } });
    const response = await middleware(incoming('/api/auth/signin', { cookie: 'malformed' }));
    expect(response.status).toBe(404);
    expect(f.bff).not.toHaveBeenCalled();
    expect(f.published).toBe(false);
  });
  it('disabled mode masks auth and renders a translated unavailable shell without cookies', async () => {
    expect((await middleware(incoming('/api/auth/signin'))).status).toBe(404);
    const response = await middleware(incoming('/studio'));
    expect(response.status).toBe(503);
    expect(await response.text()).toContain('Giriş kullanılamıyor');
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(f.bff).not.toHaveBeenCalled();
  });
  it('enabled anonymous shells redirect only to the same verified host and closed destination', async () => {
    f.enabled = true;
    const response = await middleware(incoming('/portal'));
    expect(response.status).toBe(303);
    expect(response.headers.get('location')).toBe(
      'https://institution.example.test:3000/api/auth/signin?returnTo=%2Fportal',
    );
    expect(f.bff).toHaveBeenCalledOnce();
  });
  it('retains enabled-locale precedence over a same-spelled protected shell', async () => {
    f.getSite.mockResolvedValue({
      kind: 'success',
      data: { ...f.site, enabledLocales: ['studio'], defaultLocale: 'studio' },
    });
    const response = await middleware(incoming('/studio'));
    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toBe(
      'https://institution.example.test:3000/studio/courses',
    );
    expect(f.bff).not.toHaveBeenCalled();
  });
  it('continues authenticated shells with no browser cookie, origin or authorization in downstream headers', async () => {
    f.enabled = true;
    f.bff.mockResolvedValue({
      response: null,
      session: { state: 'authenticated' },
      cookies: [],
      unavailable: false,
    });
    const response = await middleware(
      incoming('/studio', {
        cookie: 'private-canary',
        origin: 'https://other.example',
        authorization: 'Bearer private-canary',
      }),
    );
    expect(response.status).toBe(200);
    for (const key of ['cookie', 'origin', 'authorization'])
      expect(response.headers.get('x-middleware-request-' + key)).toBeNull();
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.get('x-frame-options')).toBe('DENY');
  });
  it('keeps root owned logout ahead of locale redirect and preserves only owner-authorized cookie edits', async () => {
    f.enabled = true;
    const response = new NextResponse(null, {
      status: 303,
      headers: {
        location: 'https://institution.example.test:3000/',
        'set-cookie': '__Host-ls-logout-owned=; Secure; Path=/; Max-Age=0',
      },
    });
    f.bff.mockResolvedValue({ response, session: null, cookies: [], unavailable: false });
    expect(await middleware(incoming('/?state=owned'))).toBe(response);
  });
  it('renders a bounded localized browser recovery document instead of provider error text', async () => {
    f.enabled = true;
    f.bff.mockResolvedValue({
      response: new NextResponse('private-provider-canary', {
        status: 400,
        headers: { 'set-cookie': '__Host-ls-login-owned=; Secure; Path=/; Max-Age=0' },
      }),
      session: null,
      cookies: [],
      unavailable: false,
    });
    const response = await middleware(
      incoming('/api/auth/callback/keycloak?state=x', { accept: 'text/html' }),
    );
    expect(response.status).toBe(400);
    const body = await response.text();
    expect(body).toContain('Giriş tamamlanamadı');
    expect(body).not.toContain('private-provider-canary');
    expect(response.headers.getSetCookie()).toHaveLength(1);
  });
});

it('uses only native captured Flight admission while preserving the exact signed raw target', async () => {
  f.enabled = true;
  f.bff.mockResolvedValue({
    response: null,
    session: { state: 'authenticated' },
    cookies: [],
    unavailable: false,
  });
  const target = '/studio?_rsc=probe';
  expect((await middleware(incoming(target, { rsc: '1' }))).status).toBe(404);
  expect(f.bff).not.toHaveBeenCalled();
  f.flight.mockReturnValue(true);
  const response = await middleware(incoming(target));
  expect(response.status).toBe(200);
  expect(f.bff).toHaveBeenCalledWith(
    expect.anything(),
    expect.objectContaining({ target }),
    expect.any(AbortSignal),
    expect.any(Function),
  );
});
