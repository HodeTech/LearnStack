// @vitest-environment node
import { afterEach, describe, expect, it } from 'vitest';

import { bffFixture, bffHost, bffOpaque, bffRequest } from './bff-fixture';
import { BFF_COOKIES, bffCookies } from '../server/auth/cookies';
import { BffProviderError } from '../server/auth/provider-error';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const fixture = () => {
  const f = bffFixture();
  fixtures.push(f);
  return f;
};
afterEach(async () => {
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
const jar = (response: Response) =>
  response.headers
    .getSetCookie()
    .map((value) => value.split(';', 1)[0]!)
    .join('; ');

describe('closed BFF routes with the actual owner', () => {
  it('presession CSRF → redirect → bound callback → safe session, then local-first logout and own return', async () => {
    const f = fixture();
    const csrf = await f.http.handle(bffRequest('/api/auth/csrf'));
    const token = (await csrf!.json()).csrfToken;
    const start = await f.http.handle(
      bffRequest('/api/auth/signin/keycloak', {
        method: 'POST',
        cookie: jar(csrf!),
        form: 'csrfToken=' + token + '&returnTo=%2Fstudio',
      }),
    );
    expect(start!.status).toBe(303);
    expect(f.store.counts().pending).toBe(1);
    const cookies = jar(start!);
    const entries = bffCookies(cookies);
    const name = [...entries.keys()].find((key) => key.startsWith(BFF_COOKIES.login))!;
    const state = name.slice(BFF_COOKIES.login.length);
    const callback = await f.http.handle(
      bffRequest('/api/auth/callback/keycloak?state=' + state + '&code=owned', { cookie: cookies }),
    );
    expect(callback!.headers.get('location')).toBe('/studio');
    const active = jar(callback!)
      .split('; ')
      .filter((value) => !value.endsWith('='))
      .join('; ');
    const session = await f.http.handle(bffRequest('/api/auth/session', { cookie: active }));
    const projection = await session!.json();
    expect(Object.keys(projection).sort()).toEqual([
      'csrfToken',
      'displayName',
      'expiresAt',
      'state',
    ]);
    expect(JSON.stringify(projection)).not.toContain('private-');
    expect(f.provider.refresh).not.toHaveBeenCalled();
    const logout = await f.http.handle(
      bffRequest('/api/auth/signout', {
        method: 'POST',
        cookie: active,
        form: 'csrfToken=' + projection.csrfToken,
      }),
    );
    expect(logout!.status).toBe(303);
    expect(logout!.headers.get('location')).toBe('https://identity.example.test/logout');
    expect((await f.http.handle(bffRequest('/api/auth/session', { cookie: active })))!.status).toBe(
      200,
    );
    expect(
      await (await f.http.handle(bffRequest('/api/auth/session', { cookie: active })))!.json(),
    ).toEqual({ state: 'anonymous' });
    const proofCookie = logout!.headers
      .getSetCookie()
      .find((value) => value.startsWith(BFF_COOKIES.logout))!
      .split(';', 1)[0]!;
    const logoutState = proofCookie.slice(BFF_COOKIES.logout.length).split('=')[0]!;
    const browser = entries.get(BFF_COOKIES.browser)!;
    const returned = await f.http.handle(
      bffRequest('/?state=' + logoutState, {
        cookie: BFF_COOKIES.browser + '=' + browser + '; ' + proofCookie,
      }),
    );
    expect(returned!.headers.get('location')).toBe('/');
    expect(returned!.headers.getSetCookie()).toHaveLength(1);
    expect(returned!.headers.getSetCookie()[0]).toContain(BFF_COOKIES.logout + logoutState + '=;');
    expect(f.store.counts().owners).toBe(0);
  });
  it('a valid failed callback clears only its own transaction, leaving an existing primary intact', async () => {
    const f = fixture();
    const existing = await f.login();
    const started = await f.store.start({
      ...existing.binding,
      source: '203.0.113.1',
      returnPath: '/',
    });
    f.provider.redeem.mockRejectedValueOnce(new BffProviderError('invalid_grant'));
    const response = await f.http.handle(
      bffRequest('/api/auth/callback/keycloak?state=' + started.state + '&error=access_denied', {
        cookie: existing.cookie + '; ' + BFF_COOKIES.login + started.state + '=' + started.proof,
      }),
    );
    expect(response!.status).toBe(400);
    expect(response!.headers.getSetCookie()).toEqual([
      expect.stringContaining(BFF_COOKIES.login + started.state + '=;'),
    ]);
    expect(f.store.peek(existing.binding)?.state).toBe('authenticated');
  });
  it('mismatched state/proof touches no cookie and does not consume a valid transaction', async () => {
    const f = fixture();
    const start = await f.store.start({ host: bffHost, source: '203.0.113.1', returnPath: '/' });
    for (const [state, proof] of [
      [bffOpaque(), start.proof],
      [start.state, bffOpaque()],
    ]) {
      const response = await f.http.handle(
        bffRequest('/api/auth/callback/keycloak?state=' + state + '&code=owned', {
          cookie:
            BFF_COOKIES.browser +
            '=' +
            start.browser +
            '; ' +
            BFF_COOKIES.login +
            state +
            '=' +
            proof,
        }),
      );
      expect(response!.status).toBe(400);
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(f.store.counts().pending).toBe(1);
      expect(f.provider.redeem).not.toHaveBeenCalled();
    }
  });
  it('an old logout return cannot clear a newer primary or its browser binding', async () => {
    const f = fixture();
    const old = await f.login();
    const csrf = old.finished.session.csrfToken;
    const logout = await f.http.handle(
      bffRequest('/api/auth/signout', {
        method: 'POST',
        cookie: old.cookie,
        form: 'csrfToken=' + csrf,
      }),
    );
    const nextStart = await f.store.start({
      host: bffHost,
      source: '203.0.113.1',
      browser: old.binding.browser,
      returnPath: '/',
    });
    const next = await f.store.complete({
      host: bffHost,
      source: '203.0.113.1',
      browser: nextStart.browser,
      state: nextStart.state,
      proof: nextStart.proof,
      responseUrl:
        'https://' +
        bffHost +
        '/api/auth/callback/keycloak?state=' +
        nextStart.state +
        '&code=owned',
    });
    const proof = logout!.headers
      .getSetCookie()
      .find((value) => value.startsWith(BFF_COOKIES.logout))!
      .split(';')[0]!;
    const state = proof.slice(BFF_COOKIES.logout.length).split('=')[0]!;
    const response = await f.http.handle(
      bffRequest('/?state=' + state, {
        cookie:
          BFF_COOKIES.browser +
          '=' +
          next.browser +
          '; ' +
          proof +
          '; ' +
          BFF_COOKIES.session +
          '=' +
          next.handle,
      }),
    );
    expect(response!.headers.getSetCookie()).toHaveLength(1);
    expect(
      f.store.peek({ host: bffHost, browser: next.browser, handle: next.handle }),
    ).not.toBeNull();
  });
  it('inert root state queries remain public and never select a missing logout transaction', async () => {
    const f = fixture();
    for (const target of ['/?state=inert', '/?state=' + bffOpaque()])
      expect(await f.http.handle(bffRequest(target))).toBeNull();
  });
  it('extension refreshes fresh access and returns the actual effective deadline', async () => {
    const f = fixture();
    const current = await f.login();
    f.advance(60_000);
    const response = await f.http.handle(
      bffRequest('/api/auth/extend', {
        method: 'POST',
        cookie: current.cookie,
        form: 'csrfToken=' + current.finished.session.csrfToken,
      }),
    );
    expect(response!.status).toBe(200);
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    expect((await response!.json()).expiresAt).toBe(current.finished.session.expiresAt + 60_000);
  });
  it.each(['HEAD', 'GET'])(
    '%s passive session polling never refreshes or extends expiry',
    async (method) => {
      const f = fixture();
      const current = await f.login();
      f.advance(901_000);
      const response = await f.http.handle(
        bffRequest('/api/auth/session', { method, cookie: current.cookie }),
      );
      expect(response!.status).toBe(200);
      expect(f.provider.refresh).not.toHaveBeenCalled();
      expect(f.store.peek(current.binding)?.expiresAt).toBe(current.finished.session.expiresAt);
      if (method === 'HEAD') expect(await response!.text()).toBe('');
    },
  );
  it('an expired access token refreshes on navigation; prefetch does not', async () => {
    const f = fixture();
    const current = await f.login();
    f.advance(901_000);
    await f.http.project(
      bffRequest('/studio', {
        cookie: current.cookie,
        headers: { rsc: '1', 'next-router-prefetch': '1' },
      }),
    );
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(
      await f.http.project(
        bffRequest('/studio', { cookie: current.cookie, headers: { rsc: '1' } }),
      ),
    ).not.toBeNull();
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
  });
  it('public rendering becomes anonymous after failed refresh without exposing provider data', async () => {
    const f = fixture();
    const current = await f.login();
    f.advance(901_000);
    f.provider.refresh.mockRejectedValueOnce(new Error('secret-provider-canary'));
    expect(
      await f.http.project(
        bffRequest('/en/courses', { cookie: current.cookie, headers: { accept: 'text/html' } }),
      ),
    ).toBeNull();
    expect(f.store.peek(current.binding)).toBeNull();
  });
});
