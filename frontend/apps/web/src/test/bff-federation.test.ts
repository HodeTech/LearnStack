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
    // ADR-0099 Amendment 8: the administrator's provider session ended server-side before the redirect.
    expect(f.provider.endSession).toHaveBeenCalledTimes(1);
    expect((f.provider.endSession.mock.calls[0] as unknown[])[0]).toBe('private-refresh-canary');
    const body = await started.text();
    expect(body + started.headers.get('set-cookie')).not.toContain('0000000000aa');
    const cookies = started.headers.getSetCookie();
    const login = cookies.find((value) => value.startsWith(BFF_COOKIES.login))!;
    const [name, proof] = login.split(';', 1)[0]!.split('=');
    const state = name!.slice(BFF_COOKIES.login.length);
    const original = await f.provider.redeem();
    // The fixture clock's UTC origin is 1_800_000_000_000 ms; the test window opened there.
    f.provider.redeem.mockResolvedValueOnce({
      ...original,
      accessToken: 'test-purpose-access-canary',
      refreshToken: 'test-purpose-refresh-canary',
      // A new provider session: the administrator's was ended at the start (ADR-0099 Amendment 8).
      identity: {
        ...original.identity,
        idp: alias,
        authTime: 1_800_000_000,
        sid: 'fresh-provider-session',
      },
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
    const [token, scope, input] = f.federationTest.record.mock.calls[0]! as unknown as [
      string,
      Record<string, unknown>,
      unknown,
    ];
    expect(token).toBe('test-purpose-access-canary');
    expect(scope).toMatchObject({ tenantId: f.scope.tenantId, organizationId: null });
    expect(input).toEqual({
      version: '4',
      attemptId: '019a154b-0000-7000-8000-0000000000aa',
      passed: true,
    });
    // The redemption was the test owner's (the test-record scope admitted and required).
    expect((f.provider.redeem.mock.calls.at(-1) as unknown[])[3]).toBe(true);
    for (let turn = 0; turn < 12; ++turn) await Promise.resolve();
    expect(f.provider.endSession).toHaveBeenCalledWith('test-purpose-refresh-canary');
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

  it('never echoes a submitted secret into a federation recovery document', async () => {
    const f = federated();
    const session = await f.login();
    const secret = 'secret-canary-never-echoed';
    const response = (await f.http.handle(
      bffRequest('/api/studio/federation-configure', {
        method: 'POST',
        cookie: session.cookie,
        form: new URLSearchParams({
          csrfToken: session.finished.session.csrfToken,
          protocol: 'oidc',
          displayName: 'Acme',
          source: 'discovery_url',
          documentUrl: 'http://not-https.example/',
          clientId: 'learnstack',
          clientAuthentication: 'client_secret_basic',
          clientSecret: secret,
        }).toString(),
        headers: { accept: 'text/html' },
      }),
      render,
    ))!;
    expect(response.status).toBe(400);
    const body = await response.text();
    expect(body).not.toContain(secret);
    expect(body).toContain('/studio/federation');
    expect(f.administration).not.toHaveBeenCalledWith(
      expect.anything(),
      expect.anything(),
      expect.objectContaining({ kind: 'configureFederation' }),
      expect.anything(),
    );
  });

  it('prepares the federation page only for a same-origin or user-initiated navigation (S-E, S4-1)', async () => {
    const f = federated();
    const fallback = f.administration.getMockImplementation()!;
    // The control needs a federation administrator, or the page plan denies before any detail read.
    f.administration.mockImplementation(async (token, binding, operation, signal) => {
      if (operation.kind === 'getFederationConnection')
        return {
          kind: 'success',
          status: 200,
          etag: null,
          location: null,
          replayed: false,
          data: { connection: null, provider: 'not_read' },
        };
      const result = await fallback(token, binding, operation, signal);
      return operation.kind === 'getOwnCapabilities' && result.kind === 'success'
        ? ({
            ...result,
            data: { ...(result.data as object), canManageFederation: true },
          } as typeof result)
        : result;
    });
    const session = await f.login();
    const navigate = { 'sec-fetch-mode': 'navigate', 'sec-fetch-dest': 'document' };
    const reads = async (headers: Record<string, string>) => {
      const before = f.administration.mock.calls.length;
      const projection = await f.http.project(
        bffRequest('/studio/federation', { cookie: session.cookie, headers }),
      );
      expect(projection.session).not.toBeNull();
      return f.administration.mock.calls
        .slice(before)
        .filter(([, , operation]) => operation.kind === 'getFederationConnection').length;
    };
    // Positive controls: the same protocol path does read the detail.
    expect(await reads({ 'sec-fetch-site': 'same-origin', ...navigate })).toBe(1);
    expect(await reads({ 'sec-fetch-site': 'none', ...navigate })).toBe(1);
    // An RSC navigation (a client-side transition) from the same origin reads it; from a sibling subdomain it does not.
    const rsc = { rsc: '1', 'sec-fetch-mode': 'cors', 'sec-fetch-dest': 'empty' };
    expect(await reads({ 'sec-fetch-site': 'same-origin', ...rsc })).toBe(1);
    expect(await reads({ 'sec-fetch-site': 'same-site', ...rsc })).toBe(0);
    // Refused: another site, a sibling subdomain, and a same-origin request that is not a navigation.
    expect(await reads({ 'sec-fetch-site': 'cross-site', ...navigate })).toBe(0);
    expect(await reads({ 'sec-fetch-site': 'same-site', ...navigate })).toBe(0);
    expect(
      await reads({
        'sec-fetch-site': 'same-origin',
        'sec-fetch-mode': 'navigate',
        'sec-fetch-dest': 'iframe',
      }),
    ).toBe(0);
    expect(
      await reads({
        'sec-fetch-site': 'same-origin',
        'sec-fetch-mode': 'no-cors',
        'sec-fetch-dest': 'image',
      }),
    ).toBe(0);
    expect(await reads({ 'sec-fetch-site': 'same-site', 'sec-fetch-dest': 'document' })).toBe(0);
    // D4-5 residual: a browser that sends no Sec-Fetch-Site is not decided by this check.
    expect(await reads({ 'sec-fetch-dest': 'document' })).toBe(1);
  });
});
