// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffRequest } from './bff-fixture';
import { BFF_COOKIES } from '../server/auth/cookies';
import { renderBffDocument } from '../server/auth/html';

const alias = 'lsf-' + 'a'.repeat(32);
const site = {
  displayName: 'Institution',
  defaultLocale: 'en',
  enabledLocales: ['en', 'tr'],
  theme: null,
  showPlatformAttribution: true,
};
const fixtures: ReturnType<typeof bffFixture>[] = [];
afterEach(async () => {
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
function federated(option: { alias: string; displayName: string } | null = null) {
  const federationTest = {
    start: vi.fn(async () => ({
      alias,
      attemptId: '019a154b-0000-7000-8000-0000000000aa',
      version: '4',
      connectionId: '019a154b-0000-7000-8000-0000000000cc',
    })),
    record: vi.fn(async () => ({ outcome: 'recorded' })),
  };
  const publicSignIn = vi.fn(async () => option);
  const f = bffFixture({ federationTest, publicSignIn });
  fixtures.push(f);
  return { ...f, federationTest, publicSignIn };
}
const render = (page: Parameters<typeof renderBffDocument>[1]) =>
  renderBffDocument(site, page as never);

describe('ADR-0099 sign-in option and federation_test through the BFF protocol', () => {
  it('renders the federation option only from the server read, beside the unchanged local action', async () => {
    const shown = federated({ alias, displayName: 'Acme <SSO>' });
    const page = await (await shown.http.handle(bffRequest('/api/auth/signin'), render))!.text();
    expect(page).toContain('Sign in with Acme &lt;SSO&gt;');
    expect(page).toContain('name="federation" value="1"');
    expect(page).not.toContain(alias);
    expect(page.match(/action="\/api\/auth\/signin\/keycloak"/g)).toHaveLength(2);
    const hidden = federated(null);
    const plain = await (await hidden.http.handle(bffRequest('/api/auth/signin'), render))!.text();
    expect(plain).not.toContain('name="federation"');
    expect(plain.match(/action="\/api\/auth\/signin\/keycloak"/g)).toHaveLength(1);
  });

  it('starts a federation sign-in with the server alias and refuses a browser-chosen value', async () => {
    const f = federated({ alias, displayName: 'Acme SSO' });
    const csrf = 'c'.repeat(43);
    const response = (await f.http.handle(
      bffRequest('/api/auth/signin/keycloak', {
        method: 'POST',
        cookie: BFF_COOKIES.csrf + '=' + csrf,
        form: new URLSearchParams({ csrfToken: csrf, returnTo: '/', federation: '1' }).toString(),
      }),
    ))!;
    expect(response.status).toBe(303);
    expect((vi.mocked(f.provider.begin).mock.calls.at(-1) as unknown[])[2]).toEqual({
      kind: 'federation_signin',
      alias,
    });
    for (const federation of ['2', alias])
      expect(
        (await f.http.handle(
          bffRequest('/api/auth/signin/keycloak', {
            method: 'POST',
            cookie: BFF_COOKIES.csrf + '=' + csrf,
            form: new URLSearchParams({ csrfToken: csrf, returnTo: '/', federation }).toString(),
          }),
        ))!.status,
      ).toBe(400);
  });

  it('the test route begins the authorization and the test callback publishes nothing and signs out', async () => {
    const f = federated();
    const session = await f.login();
    const started = (await f.http.handle(
      bffRequest('/api/studio/federation-test', {
        method: 'POST',
        cookie: session.cookie,
        form: new URLSearchParams({ csrfToken: session.finished.session.csrfToken }).toString(),
      }),
    ))!;
    expect(started.status).toBe(303);
    expect(started.headers.get('location')).toBe('https://identity.example.test/authorize');
    const body = await started.text();
    expect(body + started.headers.get('set-cookie')).not.toContain('0000000000aa');
    const cookies = started.headers.getSetCookie();
    const login = cookies.find((value) => value.startsWith(BFF_COOKIES.login))!;
    const [name, proof] = login.split(';', 1)[0]!.split('=');
    const state = name!.slice(BFF_COOKIES.login.length);
    const original = await f.provider.redeem();
    f.provider.redeem.mockResolvedValueOnce({
      ...original,
      identity: { ...original.identity, idp: alias },
    });
    const callback = (await f.http.handle(
      bffRequest('/api/auth/callback/keycloak?state=' + state + '&code=owned', {
        cookie: session.cookie + '; ' + BFF_COOKIES.login + state + '=' + proof,
      }),
    ))!;
    expect(callback.status).toBe(303);
    expect(callback.headers.get('location')).toBe('https://identity.example.test/logout');
    const edits = callback.headers.getSetCookie();
    expect(edits.some((value) => value.startsWith(BFF_COOKIES.session + '=;'))).toBe(true);
    expect(edits.some((value) => value.startsWith(BFF_COOKIES.logout))).toBe(true);
    expect(f.federationTest.record).toHaveBeenCalledTimes(1);
    expect(f.store.peek(session.binding)).toBeNull();
  });

  it('the test route refuses a missing or wrong CSRF proof before any work', async () => {
    const f = federated();
    const session = await f.login();
    const refused = (await f.http.handle(
      bffRequest('/api/studio/federation-test', {
        method: 'POST',
        cookie: session.cookie,
        form: new URLSearchParams({ csrfToken: 'x'.repeat(43) }).toString(),
      }),
    ))!;
    expect(refused.status).toBe(403);
    expect(f.federationTest.start).not.toHaveBeenCalled();
  });
});
