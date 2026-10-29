// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createBffHttpProtocol } from './http-protocol';
import { bffFixture, bffHost, bffOpaque, bffRequest, bffSource } from '../../test/bff-fixture';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const releases: (() => void)[] = [];
function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  return f;
}
function gate() {
  let release!: () => void;
  const promise = new Promise<void>((yes) => {
    release = yes;
  });
  releases.push(release);
  return { promise, release };
}
afterEach(async () => {
  for (const release of releases.splice(0)) release();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
describe('exclusive store work follows its caller lifetime', () => {
  it('aborted discovery retains pending ownership until actual work settles and never publishes a transaction', async () => {
    const f = fixture();
    const held = gate();
    const original = f.provider.begin.getMockImplementation()!;
    f.provider.begin.mockImplementationOnce(async (uri) => {
      await held.promise;
      return original(uri);
    });
    const controller = new AbortController();
    const pending = f.store.start({
      host: bffHost,
      source: bffSource,
      returnPath: '/',
      signal: controller.signal,
    });
    const refused = expect(pending).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(f.provider.begin).toHaveBeenCalledTimes(1));
    controller.abort();
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
    held.release();
    await refused;
    expect(f.store.counts().owners).toBe(0);
  });
  it.each(['redeem', 'account'] as const)(
    'aborted %s retains actual work and cannot publish late tenant credentials',
    async (phase) => {
      const f = fixture();
      const held = gate();
      const controller = new AbortController();
      const started = await f.store.start({ host: bffHost, source: bffSource, returnPath: '/' });
      if (phase === 'redeem') {
        const original = f.provider.redeem.getMockImplementation()!;
        f.provider.redeem.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      } else {
        const original = f.account.getMockImplementation()!;
        f.account.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      }
      const completionInput = {
        host: bffHost,
        source: bffSource,
        browser: started.browser,
        state: started.state,
        proof: started.proof,
        responseUrl:
          'https://' +
          bffHost +
          '/api/auth/callback/keycloak?state=' +
          started.state +
          '&code=owned',
        signal: phase === 'redeem' ? controller.signal : undefined,
      };
      const entered = phase === 'account' ? await f.store.complete(completionInput) : undefined;
      const pending = entered
        ? f.store.select(
            { host: bffHost, browser: entered.browser, handle: entered.handle },
            bffSource,
            entered.entry.csrfToken,
            f.scope,
            undefined,
            controller.signal,
          )
        : f.store.complete(completionInput);
      const refused = expect(pending).rejects.toMatchObject({
        reason: 'cancelled',
        ...(phase === 'redeem' ? { clearTransaction: started.state } : {}),
      });
      await vi.waitFor(() =>
        expect(phase === 'redeem' ? f.provider.redeem : f.account).toHaveBeenCalledTimes(1),
      );
      controller.abort();
      expect(f.store.counts().owners).toBe(1);
      held.release();
      await refused;
      expect(f.store.counts().owners).toBe(phase === 'redeem' ? 0 : 1);
      if (entered)
        expect(
          f.store.peek({ host: bffHost, browser: entered.browser, handle: entered.handle }),
        ).toBeNull();
    },
  );
  it('carries only the current selection/read trace to AccountSite, never a stored login request trace', async () => {
    const f = fixture();
    const started = await f.store.start({ host: bffHost, source: bffSource, returnPath: '/' });
    const finished = await f.store.complete({
      host: bffHost,
      source: '203.0.113.2',
      traceparent: 'callback-trace',
      browser: started.browser,
      state: started.state,
      proof: started.proof,
      responseUrl:
        'https://' + bffHost + '/api/auth/callback/keycloak?state=' + started.state + '&code=owned',
    });
    expect(f.account).not.toHaveBeenCalled();
    const selected = await f.store.select(
      {
        host: bffHost,
        browser: finished.browser,
        handle: finished.handle,
        traceparent: 'selection-trace',
      },
      '203.0.113.4',
      finished.entry.csrfToken,
      f.scope,
    );
    if (selected.kind !== 'session') throw new Error('Expected session');
    expect(f.account).toHaveBeenLastCalledWith(
      'private-access-canary',
      { host: bffHost, source: '203.0.113.4', traceparent: 'selection-trace', ...f.scope },
      expect.any(AbortSignal),
    );
    await f.store.admit(
      {
        host: bffHost,
        browser: selected.browser,
        handle: selected.handle,
        traceparent: 'read-trace',
      },
      '203.0.113.3',
      'read',
    );
    expect(f.account).toHaveBeenLastCalledWith(
      'private-access-canary',
      { host: bffHost, source: '203.0.113.3', traceparent: 'read-trace', ...f.scope },
      expect.any(AbortSignal),
    );
  });
});

describe('HTTP protocol cancellation reaches the real session owner', () => {
  it('sign-in abort keeps exclusive discovery owned until settlement and never emits browser authority', async () => {
    const f = fixture();
    const held = gate();
    const original = f.provider.begin.getMockImplementation()!;
    f.provider.begin.mockImplementationOnce(async (uri) => {
      await held.promise;
      return original(uri);
    });
    const controller = new AbortController();
    const csrf = bffOpaque();
    const pending = f.http.handle(
      bffRequest('/api/auth/signin/keycloak', {
        method: 'POST',
        cookie: '__Host-ls-csrf=' + csrf,
        form: new URLSearchParams({ csrfToken: csrf, returnTo: '/studio' }).toString(),
        signal: controller.signal,
      }),
    );
    await vi.waitFor(() => expect(f.provider.begin).toHaveBeenCalledTimes(1));
    controller.abort();
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, operations: 1 });
    held.release();
    const response = (await pending)!;
    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ code: 'cancelled' });
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts()).toMatchObject({ owners: 0, loginStates: 0 });
  });

  it.each(['redeem', 'account'] as const)(
    'abort during %s withholds publication and retains entry work only as applicable',
    async (phase) => {
      const f = fixture();
      const started = await f.store.start({
        host: bffHost,
        source: bffSource,
        returnPath: '/studio',
      });
      const held = gate();
      const controller = new AbortController();
      const collaborator = phase === 'redeem' ? f.provider.redeem : f.account;
      if (phase === 'redeem') {
        const original = f.provider.redeem.getMockImplementation()!;
        f.provider.redeem.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      } else {
        const original = f.account.getMockImplementation()!;
        f.account.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      }
      const entered =
        phase === 'account'
          ? await f.store.complete({
              host: bffHost,
              source: bffSource,
              browser: started.browser,
              state: started.state,
              proof: started.proof,
              responseUrl:
                'https://' +
                bffHost +
                '/api/auth/callback/keycloak?state=' +
                started.state +
                '&code=owned',
            })
          : undefined;
      const pending = f.http.handle(
        entered
          ? bffRequest('/api/auth/entry/select', {
              method: 'POST',
              cookie:
                '__Host-ls-browser=' + entered.browser + '; __Host-ls-entry=' + entered.handle,
              form: new URLSearchParams({
                csrfToken: entered.entry.csrfToken,
                tenantId: f.scope.tenantId,
              }).toString(),
              signal: controller.signal,
            })
          : bffRequest('/api/auth/callback/keycloak?state=' + started.state + '&code=owned', {
              cookie:
                '__Host-ls-browser=' +
                started.browser +
                '; __Host-ls-login-' +
                started.state +
                '=' +
                started.proof,
              signal: controller.signal,
            }),
      );
      await vi.waitFor(() => expect(collaborator).toHaveBeenCalledTimes(1));
      controller.abort();
      expect(f.store.counts()).toMatchObject({
        owners: 1,
        pending: 1,
        operations: 1,
        credentialRecords: phase === 'redeem' ? 0 : 1,
      });
      held.release();
      const response = (await pending)!;
      expect(response.status).toBe(400);
      expect(await response.json()).toEqual({ code: 'cancelled' });
      expect(response.headers.getSetCookie()).toHaveLength(phase === 'redeem' ? 1 : 0);
      if (phase === 'redeem')
        expect(response.headers.getSetCookie()[0]).toContain(
          '__Host-ls-login-' + started.state + '=;',
        );
      expect(response.headers.getSetCookie().join(';')).not.toContain('__Host-ls-session=');
      expect(response.headers.getSetCookie().join(';')).not.toContain('__Host-ls-browser=');
      expect(f.store.counts()).toMatchObject({
        owners: phase === 'redeem' ? 0 : 1,
        transactionRecords: 0,
        loginStates: 0,
      });
    },
  );

  it('extend abort cancels its HTTP waiter while the shared grant remains owned until actual completion', async () => {
    const f = fixture();
    const active = await f.login();
    const held = gate();
    const original = f.provider.refresh.getMockImplementation()!;
    f.provider.refresh.mockImplementationOnce(async () => {
      await held.promise;
      return original();
    });
    const controller = new AbortController();
    const pending = f.http.handle(
      bffRequest('/api/auth/extend', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams({ csrfToken: active.finished.session.csrfToken }).toString(),
        signal: controller.signal,
      }),
    );
    await vi.waitFor(() => expect(f.provider.refresh).toHaveBeenCalledTimes(1));
    controller.abort();
    const response = (await pending)!;
    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ code: 'cancelled' });
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(f.store.counts()).toMatchObject({ owners: 1, operations: 1 });
    held.release();
    await vi.waitFor(() => expect(f.store.counts().operations).toBe(0));
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
  });

  it('projection abort denies the current HTTP read without abandoning its actual AccountSite operation', async () => {
    const f = fixture();
    const active = await f.login();
    const held = gate();
    const original = f.account.getMockImplementation()!;
    f.account.mockImplementationOnce(async () => {
      await held.promise;
      return original();
    });
    const controller = new AbortController();
    const pending = f.http.project(
      bffRequest('/studio', {
        cookie: active.cookie,
        headers: { 'sec-fetch-dest': 'document' },
        signal: controller.signal,
      }),
    );
    await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(2));
    controller.abort();
    expect(await pending).toEqual({ session: null, cookies: [], unavailable: true });
    // Both actual AccountSite work and its retained snapshot still own credit.
    expect(f.store.counts()).toMatchObject({ owners: 1, operations: 2, pageSnapshots: 1 });
    held.release();
    await vi.waitFor(() => expect(f.store.counts().operations).toBe(0));
    expect(f.store.peek(active.binding)).not.toBeNull();
  });

  it.each(['start', 'complete'] as const)(
    'rechecks cancellation at the %s-to-response handoff after the real owner has returned',
    async (method) => {
      const f = fixture();
      const controller = new AbortController();
      const adapter = new Proxy(f.store, {
        get(target, key) {
          const value = Reflect.get(target, key);
          if (key === method)
            return async (...args: unknown[]) => {
              const result = await value.apply(target, args);
              controller.abort();
              return result;
            };
          return typeof value === 'function' ? value.bind(target) : value;
        },
      });
      const http = createBffHttpProtocol(adapter);
      const csrf = bffOpaque();
      const started =
        method === 'complete'
          ? await f.store.start({ host: bffHost, source: bffSource, returnPath: '/studio' })
          : undefined;
      const request = started
        ? bffRequest('/api/auth/callback/keycloak?state=' + started.state + '&code=owned', {
            cookie:
              '__Host-ls-browser=' +
              started.browser +
              '; __Host-ls-login-' +
              started.state +
              '=' +
              started.proof,
            signal: controller.signal,
          })
        : bffRequest('/api/auth/signin/keycloak', {
            method: 'POST',
            cookie: '__Host-ls-csrf=' + csrf,
            form: new URLSearchParams({ csrfToken: csrf, returnTo: '/studio' }).toString(),
            signal: controller.signal,
          });
      const response = (await http.handle(request))!;
      expect(response.status).toBe(400);
      expect(await response.json()).toEqual({ code: 'invalid_request' });
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(method === 'start' ? f.provider.begin : f.provider.redeem).toHaveBeenCalledTimes(1);
    },
  );
});
