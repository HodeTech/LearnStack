// @vitest-environment node
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { createInvitationRenderer } from './invitation-html';
import type { NativeInvitationApi } from './native-invitation-contract';
import { createNativeInvitationHandler, nativeInvitationTarget } from './native-invitation-http';
import en from '../../i18n/messages/en/invitation.json';
import tr from '../../i18n/messages/tr/invitation.json';
import { bffFixture, bffHost, bffOpaque, bffSource } from '../../test/bff-fixture';

const resources: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const close of resources.splice(0).reverse()) await close();
});
async function fixture(siteStatus = 200, invitations?: NativeInvitationApi) {
  const f = bffFixture({ invitations });
  const site = {
    displayName: 'Institution',
    defaultLocale: 'tr',
    enabledLocales: ['en', 'tr'],
    theme: null,
    showPlatformAttribution: true,
  };
  const transport = vi.fn<typeof fetch>(
    async () =>
      new Response(
        JSON.stringify(
          siteStatus === 200
            ? site
            : {
                type: 'about:blank',
                title: 'Hidden',
                status: siteStatus,
                code: 'not_found',
                instance: '/api/v1/public/site',
                messageKey: 'lockey_not_found',
                correlationId: 'owned-test',
              },
        ),
        {
          status: siteStatus,
          headers: {
            'content-type': siteStatus === 200 ? 'application/json' : 'application/problem+json',
          },
        },
      ),
  );
  const handler = createNativeInvitationHandler({
    configuration: { apiOrigin: 'https://api.example.test', secret: bffOpaque() },
    admission: f.publicAdmission.holder,
    store: f.store,
    responseLease: f.requestRuntime.nativeResponse,
    render: createInvitationRenderer({ en, tr }),
    providerOrigin: 'https://identity.example.test',
    transport,
  });
  const next = vi.fn();
  const server = createServer((request, response) => {
    const binding = {
      host: bffHost,
      peer: bffSource,
      method: request.method!,
      target: request.url!,
    };
    const handle = () =>
      nativeInvitationTarget(binding.target) ? handler(binding, request, response) : next();
    Promise.resolve(
      f.publicAdmission.run(binding, request, response, () =>
        f.requestRuntime.run(binding, request, response, handle),
      ),
    ).catch(() => {
      if (!response.headersSent) response.writeHead(503);
      response.end();
    });
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const url = 'http://127.0.0.1:' + (server.address() as AddressInfo).port;
  resources.push(async () => {
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
    await f.requestRuntime.stop();
    await f.store.stop();
    f.publicAdmission.shutdown();
  });
  return {
    ...f,
    transport,
    next,
    request: (path: string, init?: RequestInit) =>
      // eslint-disable-next-line no-restricted-globals -- HTTP requests target only this test's fresh owned listener.
      fetch(url + path, { redirect: 'manual', ...init }),
  };
}
function cookie(response: Response) {
  return response.headers
    .getSetCookie()
    .map((value) => value.split(';', 1)[0])
    .join('; ');
}
function csrf(html: string) {
  return /name="csrfToken" value="([^"]+)"/.exec(html)![1]!;
}

describe('native invitation response before Next', () => {
  it('admits the actual host before allocating custody, and HEAD/prefetch allocate nothing', async () => {
    const f = await fixture();
    for (const init of [
      { method: 'HEAD' },
      { headers: { purpose: 'prefetch' } },
      { headers: { rsc: '1' } },
    ] as RequestInit[]) {
      const response = await f.request('/invitations', init);
      expect(await response.text()).toBe('');
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(f.store.counts().owners).toBe(0);
    }
    const response = await f.request('/invitations');
    const html = await response.text();
    expect(response.status).toBe(200);
    expect(html).toContain('lang="tr"');
    expect(html.indexOf('<script')).toBeLessThan(html.indexOf('<body>'));
    expect(response.headers.get('cache-control')).toBe('no-store');
    const nonce = /<script nonce="([A-Za-z0-9+/]{22}==)">/.exec(html)![1]!;
    expect(response.headers.get('content-security-policy')).toContain(
      "script-src 'nonce-" + nonce + "'",
    );
    expect(response.headers.get('content-security-policy')).not.toContain(
      "script-src 'unsafe-inline'",
    );
    const nextResponse = await f.request('/invitations', { headers: { cookie: cookie(response) } });
    const nextHtml = await nextResponse.text();
    expect(/<script nonce="([A-Za-z0-9+/]{22}==)">/.exec(nextHtml)![1]).not.toBe(nonce);
    expect(f.store.counts().owners).toBe(1);
    expect(f.next).not.toHaveBeenCalled();
    expect(f.transport).toHaveBeenCalledWith(
      'https://api.example.test/api/v1/public/site',
      expect.objectContaining({
        headers: expect.objectContaining({
          'X-LearnStack-Host': bffHost,
          'X-LearnStack-Visitor-Address': bffSource,
        }),
      }),
    );
  });
  it('fails host admission before cookie parsing or private allocation', async () => {
    const f = await fixture(404);
    const response = await f.request('/invitations', {
      headers: { cookie: '__Host-ls-invitation=invalid' },
    });
    expect(response.status).toBe(404);
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts().owners).toBe(0);
    expect(f.next).not.toHaveBeenCalled();
  });
  it('supports clean no-script capture with exact Origin/CSRF and emits only clean redirects', async () => {
    const f = await fixture();
    const landing = await f.request('/invitations');
    const html = await landing.text();
    const cookies = cookie(landing);
    const token = csrf(html);
    const code = '019a154b-0000-7000-8000-000000000077.' + bffOpaque();
    const post = (body: URLSearchParams, origin?: string) =>
      f.request('/api/auth/invitation/capture', {
        method: 'POST',
        headers: { cookie: cookies, ...(origin === undefined ? {} : { origin }) },
        body,
      });
    for (const origin of [undefined, 'null', 'https://foreign.example.test']) {
      const refused = await post(new URLSearchParams({ csrfToken: token, code }), origin);
      expect(refused.status).toBe(400);
      expect(await refused.text()).not.toContain(code);
    }
    const duplicate = await post(
      new URLSearchParams([
        ['csrfToken', token],
        ['code', code],
        ['code', code],
      ]),
      'https://' + bffHost,
    );
    expect(duplicate.status).toBe(400);
    const invalid = await post(
      new URLSearchParams({ csrfToken: token, code: 'invalid-private-code' }),
      'https://' + bffHost,
    );
    expect(invalid.status).toBe(400);
    const invalidHtml = await invalid.text();
    expect(invalidHtml).toContain('id="invitation-invalid" role="alert">');
    expect(invalidHtml).toContain('aria-invalid="true"');
    expect(invalidHtml).not.toContain('invalid-private-code');
    expect(csrf(invalidHtml)).toBe(token);
    const captured = await post(
      new URLSearchParams({ csrfToken: token, code }),
      'https://' + bffHost,
    );
    expect(captured.status).toBe(303);
    expect(captured.headers.get('location')).toBe('/invitations/confirm');
    const confirm = await f.request('/invitations/confirm', { headers: { cookie: cookies } });
    const body = await confirm.text();
    expect(body).not.toContain(code);
    expect(body).toContain('name="invitation" value="continue"');
    expect(body).toContain('content="same-origin"');
    expect(f.provider.begin).not.toHaveBeenCalled();
    const cancelled = await f.request('/api/auth/invitation/cancel', {
      method: 'POST',
      headers: { cookie: cookies, origin: 'https://' + bffHost },
      body: new URLSearchParams({ csrfToken: token }),
    });
    expect(cancelled.status).toBe(303);
    expect(cancelled.headers.get('location')).toBe('/invitations');
    expect(f.next).not.toHaveBeenCalled();
  });
  it('a late replaced inspection cannot erase the newer invitation cookie or custody', async () => {
    let settled!: (value: Awaited<ReturnType<NativeInvitationApi['inspect']>>) => void;
    let began!: () => void;
    const started = new Promise<void>((resolve) => {
      began = resolve;
    });
    const api: NativeInvitationApi = {
      inspect: vi.fn<NativeInvitationApi['inspect']>(
        () =>
          new Promise((resolve) => {
            settled = resolve;
            began();
          }),
      ),
      accept: vi.fn(),
      result: vi.fn(),
    };
    const f = await fixture(200, api);
    const session = await f.login();
    const landing = await f.request('/invitations', { headers: { cookie: session.cookie } });
    const proof = csrf(await landing.text());
    const initialCookies = '__Host-ls-session=' + session.binding.handle + '; ' + cookie(landing);
    const post = (action: string, values: Record<string, string>, cookies = initialCookies) =>
      f.request('/api/auth/invitation/' + action, {
        method: 'POST',
        headers: { cookie: cookies, origin: 'https://' + bffHost },
        body: new URLSearchParams({ csrfToken: proof, ...values }),
      });
    const invitationId = '019a154b-0000-7000-8000-000000000077';
    await post('capture', { code: invitationId + '.' + bffOpaque() });
    const previous = post('inspect', {});
    await started;
    const replacement = await post('capture', { code: invitationId + '.' + bffOpaque() });
    expect(replacement.status).toBe(303);
    const replacementCookie = replacement.headers.getSetCookie()[0]!;
    expect(replacementCookie).toMatch(/^__Host-ls-invitation=/);
    settled({
      invitationId,
      version: '1',
      expiresAt: '2026-11-06T12:00:00Z',
      tenantLabel: 'Institution',
      organizationLabel: null,
      roleId: '019a154b-0000-7000-8000-000000000088',
      roleKey: 'Learner',
      roleScope: 'Tenant',
      verifiedEmail: 'recipient@example.test',
    });
    const late = await previous;
    expect(late.headers.getSetCookie()).toEqual([]);
    const latestCookies = initialCookies
      .split('; ')
      .filter((value) => !value.startsWith('__Host-ls-invitation='))
      .concat(replacementCookie.split(';', 1)[0]!)
      .join('; ');
    const current = await f.request('/invitations/confirm', { headers: { cookie: latestCookies } });
    expect(current.status).toBe(200);
    expect(await current.text()).toContain('data-invitation-state="inspect"');
    expect(api.accept).not.toHaveBeenCalled();
  });
  it('a delayed cancellation response cannot erase a subsequently opened continuation', async () => {
    const f = await fixture();
    const landing = await f.request('/invitations');
    const proof = csrf(await landing.text());
    const originalCookies = cookie(landing);
    let release!: () => void;
    let began!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const started = new Promise<void>((resolve) => {
      began = resolve;
    });
    const invitation = f.store.invitation.bind(f.store);
    vi.spyOn(f.store, 'invitation').mockImplementation(async (...args) => {
      const result = await invitation(...args);
      if (args[2]?.kind === 'cancel') {
        began();
        await held;
      }
      return result;
    });
    const cancellation = f.request('/api/auth/invitation/cancel', {
      method: 'POST',
      headers: { cookie: originalCookies, origin: 'https://' + bffHost },
      body: new URLSearchParams({ csrfToken: proof }),
    });
    await started;
    const replacement = await f.request('/invitations', { headers: { cookie: originalCookies } });
    expect(replacement.status).toBe(200);
    const latestCookies = cookie(replacement);
    release();
    expect((await cancellation).headers.getSetCookie()).toEqual([]);
    const current = await f.request('/invitations/confirm', { headers: { cookie: latestCookies } });
    expect(current.status).toBe(200);
    expect(await current.text()).toContain('data-invitation-state="signin"');
  });
  it('refuses query aliases and nonexact recipient routes without Next', async () => {
    const f = await fixture();
    expect((await f.request('/invitations?code=not-a-carrier')).status).toBe(400);
    expect((await f.request('/api/auth/invitation/unreviewed')).status).toBe(404);
    expect(f.transport).not.toHaveBeenCalled();
    expect(f.next).not.toHaveBeenCalled();
  });
});
