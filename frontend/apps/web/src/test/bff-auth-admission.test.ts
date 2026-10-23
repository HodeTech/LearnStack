// @vitest-environment node
import { afterEach, describe, expect, it } from 'vitest';

import { bffFixture, bffHost, bffOpaque, bffRequest } from './bff-fixture';
import { AUTH_INPUT_LIMIT, BFF_COOKIES, bffCookie, bffCookies } from '../server/auth/cookies';
import { bffActivity, bffForm, bffQuery, bffRoute } from '../server/auth/http-input';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const fixture = () => {
  const f = bffFixture();
  fixtures.push(f);
  return f;
};
afterEach(async () => {
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});

describe('BFF finite input and CSRF admission', () => {
  it('admits only the exact method/route set', () => {
    for (const path of ['signin', 'csrf', 'session', 'error'])
      for (const method of ['GET', 'HEAD'])
        expect(bffRoute(method, '/api/auth/' + path)).toBe(true);
    for (const path of ['signin/keycloak', 'signout', 'extend'])
      expect(bffRoute('POST', '/api/auth/' + path)).toBe(true);
    expect(bffRoute('GET', '/api/auth/callback/keycloak')).toBe(true);
    for (const [method, path] of [
      ['POST', '/studio'],
      ['POST', '/api/auth/session'],
      ['HEAD', '/api/auth/callback/keycloak'],
      ['PUT', '/api/auth/signout'],
      ['POST', '/api/auth/signin/arbitrary'],
      ['POST', '/api/auth/extend/'],
      ['GET', '/api/auth/providers'],
    ])
      expect(bffRoute(method!, path!)).toBe(false);
  });
  it.each(['GET', 'HEAD', 'POST'])(
    'unknown %s auth actions remain masked without owner/provider work',
    async (method) => {
      const f = fixture();
      const response = await f.http.handle(bffRequest('/api/auth/providers', { method }));
      expect(response!.status).toBe(404);
      expect(response!.headers.get('cache-control')).toBe('no-store');
      expect(f.store.counts().owners).toBe(0);
      expect(f.provider.begin).not.toHaveBeenCalled();
      if (method === 'HEAD') expect(await response!.text()).toBe('');
    },
  );
  it.each([
    'csrfToken=x&csrfToken=y',
    'unknown=x',
    'csrfToken=%FF',
    'csrfToken=%',
    'csrfToken=x&&returnTo=%2F',
    'csrfToken',
    'csrfToken=x&returnTo=%C0%AF',
  ])('refuses undeclared/duplicate/malformed scalar form: %s', async (form) => {
    await expect(
      bffForm(bffRequest('/api/auth/signin/keycloak', { method: 'POST', form }), [
        'csrfToken',
        'returnTo',
      ]),
    ).rejects.toThrow('BFF input refused');
  });
  it('accepts exact UTF-8 byte limit and refuses its next byte', async () => {
    const good = 'value=' + 'x'.repeat(AUTH_INPUT_LIMIT - 6);
    expect((await bffForm(bffRequest('/', { form: good }), ['value'])).get('value')?.length).toBe(
      AUTH_INPUT_LIMIT - 6,
    );
    await expect(bffForm(bffRequest('/', { form: good + 'x' }), ['value'])).rejects.toThrow();
  });
  it.each(['application/json', 'text/plain', 'application/x-www-form-urlencoded;charset=latin1'])(
    'refuses unsupported media %s',
    async (media) => {
      await expect(
        bffForm(bffRequest('/', { form: 'value=x', headers: { 'content-type': media } }), [
          'value',
        ]),
      ).rejects.toThrow();
    },
  );
  it.each(['state=x&state=y', 'state=%FF', 'state=%', 'state=x&other=y'])(
    'rejects ambiguous query %s',
    (query) => {
      expect(() => bffQuery('/?' + query, ['state'])).toThrow();
    },
  );
  it('the actual stream is cancelled on request abort, without waiting for more attacker body', async () => {
    let cancel = 0;
    const controller = new AbortController();
    const body = new ReadableStream<Uint8Array>({
      cancel() {
        ++cancel;
      },
    });
    const request = { ...bffRequest('/', { form: 'value=x', signal: controller.signal }), body };
    const pending = bffForm(request, ['value']);
    controller.abort();
    await expect(pending).rejects.toThrow();
    expect(cancel).toBe(1);
  });
  it.each([null, 'https://foreign.example', 'null', 'http://' + bffHost])(
    'rejects missing/mismatched Origin %s before any reservation',
    async (origin) => {
      const f = fixture();
      const token = bffOpaque();
      const request = bffRequest('/api/auth/signin/keycloak', {
        method: 'POST',
        cookie: BFF_COOKIES.csrf + '=' + token,
        form: 'csrfToken=' + token,
      });
      if (origin === null) request.headers.delete('origin');
      else request.headers.set('origin', origin);
      expect((await f.http.handle(request))!.status).toBe(400);
      expect(f.store.counts().owners).toBe(0);
      expect(f.provider.begin).not.toHaveBeenCalled();
    },
  );
  it('same-origin alone cannot pass either presession or authenticated CSRF', async () => {
    const f = fixture();
    const token = bffOpaque();
    expect(
      (await f.http.handle(
        bffRequest('/api/auth/signin/keycloak', {
          method: 'POST',
          cookie: BFF_COOKIES.csrf + '=' + token,
          form: 'csrfToken=' + bffOpaque(),
        }),
      ))!.status,
    ).toBe(403);
    const active = await f.login();
    for (const path of ['signout', 'extend'])
      expect(
        (await f.http.handle(
          bffRequest('/api/auth/' + path, {
            method: 'POST',
            cookie: active.cookie,
            form: 'csrfToken=' + bffOpaque(),
          }),
        ))!.status,
      ).toBe(403);
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(f.provider.refresh).not.toHaveBeenCalled();
  });
  it('browser Authorization and redirect selectors never reach provider or store admission', async () => {
    const f = fixture();
    for (const request of [
      bffRequest('/api/auth/csrf', { headers: { authorization: 'Bearer private-canary' } }),
      bffRequest('/api/auth/signin?returnTo=https%3A%2F%2Fevil.example'),
      bffRequest('/api/auth/signin?callbackUrl=%2Fstudio'),
    ]) {
      const response = await f.http.handle(request);
      expect(response!.status).toBe(400);
      expect(await response!.text()).not.toContain('private-canary');
      expect(response!.headers.getSetCookie()).toEqual([]);
    }
    expect(f.provider.begin).not.toHaveBeenCalled();
  });
  it('all emitted cookies are single opaque host cookies without credentials, Domain or chunks', () => {
    const token = bffOpaque();
    for (const name of [
      BFF_COOKIES.session,
      BFF_COOKIES.browser,
      BFF_COOKIES.csrf,
      BFF_COOKIES.login + token,
      BFF_COOKIES.logout + token,
    ]) {
      const cookie = bffCookie(name, token, 300);
      expect(cookie).toContain('; Path=/; Max-Age=300; HttpOnly; Secure; SameSite=Lax');
      expect(cookie).not.toMatch(/Domain=|private-|Bearer/);
    }
  });
  it.each([
    '__Host-ls-session=x',
    '__Host-ls-session.0=' + bffOpaque(),
    '__Host-ls-unknown=' + bffOpaque(),
    '__Host-ls-session=' + bffOpaque() + '; __Host-ls-session=' + bffOpaque(),
    '__Host-ls-login-x=' + bffOpaque(),
  ])('refuses malformed/chunked/duplicate auth cookies %s', (cookie) => {
    expect(() => bffCookies(cookie)).toThrow();
  });
  it('bounds the whole incoming auth family and ignores unrelated cookies without forwarding them', () => {
    const value = bffOpaque();
    const members = Array.from(
      { length: 130 },
      () => BFF_COOKIES.login + bffOpaque() + '=' + value,
    );
    expect(() => bffCookies(members.join('; '))).not.toThrow();
    expect(() =>
      bffCookies(
        [
          ...members,
          ...Array.from({ length: 40 }, () => BFF_COOKIES.logout + bffOpaque() + '=' + value),
        ].join('; '),
      ),
    ).toThrow();
    expect([...bffCookies('foreign=arbitrary%FF; ' + BFF_COOKIES.session + '=' + value)]).toEqual([
      [BFF_COOKIES.session, value],
    ]);
  });
  it.each([
    ['HEAD', { accept: 'text/html' }, 'passive'],
    ['GET', { rsc: '1' }, 'read'],
    ['GET', { rsc: '1', 'next-router-prefetch': '1' }, 'passive'],
    ['GET', { 'sec-purpose': 'prefetch;prerender', accept: 'text/html' }, 'passive'],
    ['GET', { 'sec-fetch-dest': 'document' }, 'read'],
    ['GET', {}, 'passive'],
  ] as const)('activity classification %s %j is %s', (method, headers, expected) => {
    expect(bffActivity(method, new Headers(headers))).toBe(expected);
  });
});
