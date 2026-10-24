// @vitest-environment node
import { afterEach, describe, expect, it } from 'vitest';

import { bffFixture, bffHost, bffOpaque, bffRequest } from './bff-fixture';
import {
  AUTH_INPUT_LIMIT,
  BFF_COOKIES,
  bffCookie,
  bffCookies,
  clearMalformedBffCookies,
} from '../server/auth/cookies';
import {
  BFF_POST_PATHS,
  bffActivity,
  bffForm,
  bffQuery,
  bffRoute,
} from '../server/auth/http-input';

const postPaths = ['/api/auth/signin/keycloak', '/api/auth/signout', '/api/auth/extend'];
const readPaths = ['/api/auth/signin', '/api/auth/csrf', '/api/auth/session', '/api/auth/error'];
const methods = [
  'GET',
  'HEAD',
  'POST',
  'PUT',
  'PATCH',
  'DELETE',
  'OPTIONS',
  'CONNECT',
  'TRACE',
  'get',
  'post',
];

const fixtures: ReturnType<typeof bffFixture>[] = [];
const fixture = () => {
  const f = bffFixture();
  fixtures.push(f);
  return f;
};
afterEach(async () => {
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});

describe('Bff_Auth_Admission_And_Csrf_Are_Closed', () => {
  it('admits only the exact method/route set', () => {
    expect(BFF_POST_PATHS).toEqual(postPaths);
    const paths = [
      ...readPaths,
      ...postPaths,
      '/api/auth/callback/keycloak',
      '/api/auth/refresh',
      '/api/auth/providers',
      '/api/auth/signin/arbitrary',
      '/api/auth',
      '/studio',
    ];
    for (const path of [...paths, ...paths.map((path) => path + '/')])
      for (const method of methods)
        expect(bffRoute(method, path), method + ' ' + path).toBe(
          (method === 'POST' && postPaths.includes(path)) ||
            (method === 'GET' && path === '/api/auth/callback/keycloak') ||
            (['GET', 'HEAD'].includes(method) && readPaths.includes(path)),
        );
  });
  it.each(
    postPaths.flatMap((path) =>
      methods.filter((method) => method !== 'POST').map((method) => [method, path]),
    ),
  )('refuses %s %s with valid session/CSRF before any operation', async (method, path) => {
    const f = fixture();
    const current = await f.login();
    const before = f.store.peek(current.binding)!;
    const response = await f.http.handle(
      bffRequest(path!, {
        method,
        cookie: current.cookie,
        form: 'csrfToken=' + before.csrfToken,
      }),
    );
    expect(response!.status).toBe(404);
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(f.store.peek(current.binding)).toEqual(before);
    expect(f.provider.begin).toHaveBeenCalledTimes(1);
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(f.provider.logoutUrl).not.toHaveBeenCalled();
    expect(f.account).toHaveBeenCalledTimes(1);
  });
  it.each(
    readPaths.flatMap((path) => [false, true].map((authenticated) => ({ path, authenticated }))),
  )(
    'HEAD is passive without cookie allocation or rendering: %j',
    async ({ path, authenticated }) => {
      const f = fixture();
      const current = authenticated ? await f.login() : null;
      f.advance(840_000);
      const before = current && f.store.peek(current.binding);
      const counts = f.store.counts();
      let renders = 0;
      const response = await f.http.handle(
        bffRequest(path, { method: 'HEAD', cookie: current?.cookie }),
        () => {
          ++renders;
          return '<form>owned</form>';
        },
      );
      expect(response!.status).toBe(path.endsWith('/error') ? 400 : 200);
      expect(await response!.text()).toBe('');
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(renders).toBe(0);
      expect(f.store.counts()).toEqual(counts);
      if (current) expect(f.store.peek(current.binding)).toEqual(before);
      expect(f.account).toHaveBeenCalledTimes(authenticated ? 1 : 0);
      expect(f.provider.refresh).not.toHaveBeenCalled();
    },
  );
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
  it.each(
    postPaths.flatMap((path) =>
      [
        null,
        'https://foreign.example',
        'null',
        'http://' + bffHost,
        'https://' + bffHost + '/',
      ].map((origin) => ({ path, origin })),
    ),
  )('rejects wrong Origin with otherwise valid CSRF/session: %j', async ({ path, origin }) => {
    const f = fixture();
    const current = path.endsWith('/signin/keycloak') ? null : await f.login();
    f.advance(840_000);
    const before = current && f.store.peek(current.binding);
    const token = before?.csrfToken ?? bffOpaque();
    const counts = f.store.counts();
    const request = bffRequest(path, {
      method: 'POST',
      cookie: current?.cookie ?? BFF_COOKIES.csrf + '=' + token,
      form: 'csrfToken=' + token,
    });
    if (origin === null) request.headers.delete('origin');
    else request.headers.set('origin', origin);
    const response = await f.http.handle(request);
    expect(response!.status).toBe(400);
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts()).toEqual(counts);
    if (current) expect(f.store.peek(current.binding)).toEqual(before);
    expect(f.provider.begin).toHaveBeenCalledTimes(current ? 1 : 0);
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(f.provider.logoutUrl).not.toHaveBeenCalled();
    expect(f.account).toHaveBeenCalledTimes(current ? 1 : 0);
  });
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
  it.each([
    'https://foreign.example/studio',
    '//foreign.example/studio',
    'javascript:alert(1)',
    '/studio/nested',
    '/studio?next=https://foreign.example',
    '/studio#section',
    '/%2f%2fforeign.example',
    '/%73tudio',
    '/\\foreign.example',
    '\r\nhttps://foreign.example',
  ])('POST sign-in rejects returnTo %j after valid Origin and CSRF', async (destination) => {
    const f = fixture();
    const token = bffOpaque();
    const counts = f.store.counts();
    const response = await f.http.handle(
      bffRequest('/api/auth/signin/keycloak', {
        method: 'POST',
        cookie: BFF_COOKIES.csrf + '=' + token,
        form: new URLSearchParams({ csrfToken: token, returnTo: destination }).toString(),
      }),
    );
    expect(response!.status).toBe(400);
    expect(await response!.json()).toEqual({ code: 'invalid_request' });
    expect(response!.headers.has('location')).toBe(false);
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts()).toEqual(counts);
    expect(f.provider.begin).not.toHaveBeenCalled();
    expect(f.account).not.toHaveBeenCalled();
  });
  it.each(['/', '/studio', '/portal'])(
    'POST sign-in accepts only the exact local returnTo %s with valid Origin and CSRF',
    async (destination) => {
      const f = fixture();
      const token = bffOpaque();
      const response = await f.http.handle(
        bffRequest('/api/auth/signin/keycloak', {
          method: 'POST',
          cookie: BFF_COOKIES.csrf + '=' + token,
          form: new URLSearchParams({ csrfToken: token, returnTo: destination }).toString(),
        }),
      );
      expect(response!.status).toBe(303);
      expect(f.provider.begin).toHaveBeenCalledOnce();
      expect(f.store.counts().pending).toBe(1);
    },
  );
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
    ['GET', { accept: 'text/html' }, 'passive'],
    ['GET', { accept: 'text/html', 'sec-fetch-dest': 'empty' }, 'passive'],
    ['GET', {}, 'passive'],
  ] as const)('activity classification %s %j is %s', (method, headers, expected) => {
    expect(bffActivity(method, new Headers(headers))).toBe(expected);
  });
  it('malformed-cookie recovery bounds deletion bytes and never reflects invalid names or values', () => {
    expect(clearMalformedBffCookies('foreign=private; __Host-ls-invalid name=private')).toEqual([]);
    const edits = clearMalformedBffCookies(
      Array.from(
        { length: 500 },
        (_, index) => '__Host-ls-malformed-' + index + '=private-cookie-canary',
      ).join('; '),
    );
    expect(edits.length).toBeGreaterThan(0);
    expect(Buffer.byteLength(edits.join(''))).toBeLessThanOrEqual(AUTH_INPUT_LIMIT);
    expect(edits.join('')).not.toContain('private');
    expect(clearMalformedBffCookies(null)).toEqual([]);
  });
});
