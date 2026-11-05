// @vitest-environment node
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { Readable } from 'node:stream';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { BFF_COOKIES } from './cookies';
import { createInvitationRenderer } from './invitation-html';
import type { NativeInvitationApi } from './native-invitation-contract';
import { createNativeInvitationHandler, nativeInvitationTarget } from './native-invitation-http';
import { BffSessionError } from './session-contract';
import { BFF_LIMITS } from './session-store';
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
  const signoutResponse = vi.fn(async (_response: Response) => {});
  const server = createServer((request, response) => {
    const binding = {
      host: bffHost,
      peer: bffSource,
      method: request.method!,
      target: request.url!,
    };
    const handle = async () => {
      if (nativeInvitationTarget(binding.target)) return handler(binding, request, response);
      if (binding.target !== '/api/auth/signout') return next();
      const headers = new Headers();
      for (const [name, value] of Object.entries(request.headers))
        if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(',') : value);
      const result = await f.http.handle({
        host: bffHost,
        source: bffSource,
        method: request.method!,
        target: request.url!,
        headers,
        body: Readable.toWeb(request) as ReadableStream<Uint8Array>,
        signal: new AbortController().signal,
      });
      if (!result) throw new Error('Expected generic signout response');
      await signoutResponse(result);
      const outgoing = Object.fromEntries(result.headers);
      response.writeHead(result.status, {
        ...outgoing,
        'set-cookie': result.headers.getSetCookie(),
      });
      response.end(await result.text());
    };
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
    signoutResponse,
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
  it.each([404, 429, 503])(
    'preserves neutral public bootstrap %s before allocation',
    async (status) => {
      const f = await fixture(status);
      const response = await f.request('/invitations');
      expect(response.status).toBe(status);
      expect(response.headers.get('cache-control')).toBe('no-store');
      expect(response.headers.get('retry-after')).toBe(status === 404 ? null : '30');
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(await response.text()).toBe('');
      expect(f.store.counts().owners).toBe(0);
    },
  );
  it.each([
    ['source_capacity', 429],
    ['subject_capacity', 429],
    ['pending_capacity', 503],
    ['global_capacity', 503],
  ] as const)('preserves local %s status without a cookie or owner', async (reason, status) => {
    const f = await fixture();
    vi.spyOn(f.store, 'openInvitation').mockImplementationOnce(() => {
      throw new BffSessionError(reason);
    });
    const response = await f.request('/invitations');
    expect(response.status).toBe(status);
    expect(response.headers.get('retry-after')).toBe('30');
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(await response.text()).toBe('');
    expect(f.store.counts().owners).toBe(0);
  });
  it('actual source saturation refuses429 and passive requests allocate nothing', async () => {
    const f = await fixture();
    for (let i = 0; i < BFF_LIMITS.source; i++)
      f.store.openInvitation({ host: bffHost, source: bffSource });
    const count = f.store.counts().owners;
    for (const init of [
      { method: 'HEAD' },
      { headers: { purpose: 'prefetch' } },
      { headers: { rsc: '1' } },
    ] as RequestInit[]) {
      const passive = await f.request('/invitations', init);
      expect(passive.status).toBe(200);
      expect(passive.headers.getSetCookie()).toEqual([]);
    }
    const refused = await f.request('/invitations');
    expect(refused.status).toBe(429);
    expect(refused.headers.get('retry-after')).toBe('30');
    expect(refused.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts().owners).toBe(count);
  });
  it.each(['entry', 'live'] as const)(
    'late generic %s signout cannot clear newer invitation custody',
    async (kind) => {
      const f = await fixture();
      const live = kind === 'live' ? await f.login() : undefined;
      const landing = await f.request('/invitations', {
        headers: live ? { cookie: live.cookie } : {},
      });
      const initialCookies = cookie(landing);
      const original = new Map(
        initialCookies.split('; ').map((pair) => pair.split('=') as [string, string]),
      );
      const oldBinding = {
        host: bffHost,
        browser: original.get(BFF_COOKIES.browser)!,
        handle: original.get(BFF_COOKIES.invitation)!,
      };
      const code = '019a154b-0000-7000-8000-000000000077.' + bffOpaque();
      await f.request('/api/auth/invitation/capture', {
        method: 'POST',
        headers: { cookie: initialCookies, origin: 'https://' + bffHost },
        body: new URLSearchParams({ csrfToken: csrf(await landing.text()), code }),
      });
      const entry =
        kind === 'entry'
          ? await f.enter({ browser: oldBinding.browser, invitation: oldBinding.handle })
          : undefined;
      const token = live ? f.store.peek(live.binding)!.csrfToken : entry!.finished.entry.csrfToken;
      const originalCookies = [
        ...new Map(
          (initialCookies + '; ' + (live ? live.cookie : entry!.cookie))
            .split('; ')
            .map((pair) => pair.split('=') as [string, string]),
        ),
      ]
        .map(([name, value]) => name + '=' + value)
        .join('; ');
      let release!: () => void;
      let began!: () => void;
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });
      const started = new Promise<void>((resolve) => {
        began = resolve;
      });
      f.signoutResponse.mockImplementationOnce(async () => {
        began();
        await held;
      });
      const signout = f.request('/api/auth/signout', {
        method: 'POST',
        headers: { cookie: originalCookies, origin: 'https://' + bffHost },
        body: new URLSearchParams({ csrfToken: token }),
      });
      await started;
      const revoked = await f.store.invitation(oldBinding, bffSource).then(
        () => false,
        () => true,
      );
      const replacement = await f.request('/invitations', {
        headers: { cookie: BFF_COOKIES.browser + '=' + oldBinding.browser },
      });
      const latestCookies = cookie(replacement);
      await f.request('/api/auth/invitation/capture', {
        method: 'POST',
        headers: { cookie: latestCookies, origin: 'https://' + bffHost },
        body: new URLSearchParams({ csrfToken: csrf(await replacement.text()), code }),
      });
      release();
      const late = await signout;
      expect(revoked).toBe(true);
      expect(late.status).toBe(303);
      expect(
        late.headers.getSetCookie().some((value) => value.startsWith(BFF_COOKIES.invitation + '=')),
      ).toBe(false);
      expect(
        late.headers
          .getSetCookie()
          .some((value) =>
            value.startsWith((live ? BFF_COOKIES.session : BFF_COOKIES.entry) + '=;'),
          ),
      ).toBe(true);
      const current = await f.request('/invitations/confirm', {
        headers: { cookie: latestCookies },
      });
      expect(current.status).toBe(200);
      expect(await current.text()).toContain('data-invitation-state="signin"');
    },
  );
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
