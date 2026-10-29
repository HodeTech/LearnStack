// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffOpaque, bffRequest } from './bff-fixture';
import { NativeApiError } from '../server/auth/native-contract';
import { BffProviderError } from '../server/auth/provider-error';

const fixtures: ReturnType<typeof bffFixture>[] = [];
function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  return f;
}
afterEach(async () => {
  vi.restoreAllMocks();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});

describe('native entry failures retain closed meaning without publishing upstream details', () => {
  it.each([400, 401, 403, 404, 409, 429, 500, 503, 599])(
    'preserves verified native API status %i and no implicit retry or missing state',
    async (status) => {
      const f = fixture();
      const entry = await f.enter();
      const failure = new NativeApiError('provider_refused', status);
      failure.cause = new Error('private-upstream-canary');
      f.native.self.mockRejectedValueOnce(failure);
      const response = await f.http.handle(bffRequest('/api/auth/entry', { cookie: entry.cookie }));
      expect(response!.status).toBe(status);
      expect(await response!.text()).toBe('{"code":"provider_refused"}');
      expect(response!.headers.get('cache-control')).toBe('no-store');
      expect(response!.headers.get('retry-after')).toBeNull();
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(f.native.self).toHaveBeenCalledTimes(1);
      expect(f.native.contexts).not.toHaveBeenCalled();
      expect(f.native.bootstrap).not.toHaveBeenCalled();
      expect(f.account).not.toHaveBeenCalled();
      expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
    },
  );
  it.each([1, 37, 86400])(
    'retains the bounded explicit retry hint %i on 429 entry recovery',
    async (retryAfterSeconds) => {
      const f = fixture();
      const entry = await f.enter();
      f.native.self.mockRejectedValueOnce(
        new NativeApiError('provider_refused', 429, retryAfterSeconds),
      );
      const render = vi.fn(() => '<p>localized wait</p>');
      const response = await f.http.handle(
        bffRequest('/api/auth/entry', { cookie: entry.cookie, headers: { accept: 'text/html' } }),
        render,
      );
      expect(response!.status).toBe(429);
      expect(response!.headers.get('retry-after')).toBe(String(retryAfterSeconds));
      expect(render).toHaveBeenCalledWith({
        kind: 'entry_error',
        reason: 'busy',
        retryAfterSeconds,
      });
      expect(f.native.self).toHaveBeenCalledTimes(1);
      expect(f.native.bootstrap).not.toHaveBeenCalled();
    },
  );
  it('creation disabled stays 403, retains the entry and renders a creation-specific recovery without retrying POST', async () => {
    const f = fixture();
    const entry = await f.enter();
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    f.native.bootstrap.mockRejectedValueOnce(new NativeApiError('provider_refused', 403));
    const render = vi.fn(() => 'localized creation unavailable');
    const response = await f.http.handle(
      bffRequest('/api/auth/entry/create', {
        method: 'POST',
        cookie: entry.cookie,
        form: 'csrfToken=' + entry.finished.entry.csrfToken,
      }),
      render,
    );
    expect(response!.status).toBe(403);
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(render).toHaveBeenCalledWith({
      kind: 'entry_error',
      reason: 'creation_disabled',
      retryAfterSeconds: undefined,
    });
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    expect(f.native.self).toHaveBeenCalledTimes(1);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
  });
  it('401 during deliberate creation is terminal for that request and never calls bootstrap', async () => {
    const f = fixture();
    const entry = await f.enter();
    f.native.self.mockRejectedValueOnce(new NativeApiError('provider_refused', 401));
    const render = vi.fn(() => 'localized sign in again');
    const response = await f.http.handle(
      bffRequest('/api/auth/entry/create', {
        method: 'POST',
        cookie: entry.cookie,
        form: 'csrfToken=' + entry.finished.entry.csrfToken,
      }),
      render,
    );
    expect(response!.status).toBe(401);
    expect(render).toHaveBeenCalledWith({
      kind: 'entry_error',
      reason: 'reauthenticate',
      retryAfterSeconds: undefined,
    });
    expect(f.native.self).toHaveBeenCalledTimes(1);
    expect(f.native.bootstrap).not.toHaveBeenCalled();
    expect(f.native.contexts).not.toHaveBeenCalled();
    expect(response!.headers.getSetCookie()).toEqual([]);
  });
  it('audited selection preserves native denial status without calling AccountSite or changing a live source session', async () => {
    const f = fixture();
    const active = await f.login();
    const entry = await f.enter(active.binding);
    f.native.admit.mockRejectedValueOnce(new NativeApiError('provider_refused', 403));
    const response = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: entry.cookie,
        form: new URLSearchParams({
          csrfToken: entry.finished.entry.csrfToken,
          tenantId: f.scope.tenantId,
        }).toString(),
      }),
    );
    expect(response!.status).toBe(403);
    expect(await response!.json()).toEqual({ code: 'provider_refused' });
    expect(f.native.admit).toHaveBeenCalledTimes(2);
    expect(f.account).toHaveBeenCalledTimes(1);
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(response!.headers.getSetCookie()).toEqual([]);
  });
  it.each(['transport', 'deadline', 'response', 'configuration', 'provider_refused'] as const)(
    'AccountSite %s keeps its established 503 classification without native status inference',
    async (reason) => {
      const f = fixture();
      const entry = await f.enter();
      const failure = new BffProviderError(reason);
      Object.assign(failure, {
        status: 401,
        retryAfterSeconds: 9999999,
        cause: new Error('private-account-canary'),
      });
      f.account.mockRejectedValueOnce(failure);
      const response = await f.http.handle(
        bffRequest('/api/auth/entry/select', {
          method: 'POST',
          cookie: entry.cookie,
          form: new URLSearchParams({
            csrfToken: entry.finished.entry.csrfToken,
            tenantId: f.scope.tenantId,
          }).toString(),
        }),
      );
      expect(response!.status).toBe(503);
      expect(await response!.json()).toEqual({ code: reason });
      expect(response!.headers.get('retry-after')).toBe('30');
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(f.account).toHaveBeenCalledTimes(1);
      expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
    },
  );
  it('unknown creation transport does not claim rollback and a deliberate retry resolves the durable binding before any further POST', async () => {
    const f = fixture();
    const entry = await f.enter();
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    f.native.bootstrap.mockRejectedValueOnce(new Error('private-unknown-commit-canary'));
    const post = () =>
      bffRequest('/api/auth/entry/create', {
        method: 'POST',
        cookie: entry.cookie,
        form: 'csrfToken=' + entry.finished.entry.csrfToken,
      });
    const failed = await f.http.handle(post());
    expect(failed!.status).toBe(503);
    expect(await failed!.json()).toEqual({ code: 'unavailable' });
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
    const repeated = await f.http.handle(post());
    expect(repeated!.status).toBe(303);
    expect(repeated!.headers.get('location')).toBe('/api/auth/entry');
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    expect(repeated!.headers.getSetCookie()).toEqual([]);
  });
  it.each(['/api/auth/entry/create', '/api/auth/entry/select'])(
    '%s rejects a wrong well-formed proof with 403 before any API call',
    async (path) => {
      const f = fixture();
      const entry = await f.enter();
      const fields = new URLSearchParams({ csrfToken: bffOpaque() });
      if (path.endsWith('/select')) fields.set('tenantId', f.scope.tenantId);
      const response = await f.http.handle(
        bffRequest(path, {
          method: 'POST',
          cookie: entry.cookie,
          form: fields.toString(),
        }),
      );
      expect(response!.status).toBe(403);
      expect(await response!.json()).toEqual({ code: 'csrf' });
      expect(f.native.self).not.toHaveBeenCalled();
      expect(f.native.contexts).not.toHaveBeenCalled();
      expect(f.native.bootstrap).not.toHaveBeenCalled();
      expect(f.native.admit).not.toHaveBeenCalled();
      expect(f.account).not.toHaveBeenCalled();
    },
  );
  it('explicit JSON entry reads and failures stay JSON even with the real middleware renderer installed', async () => {
    const f = fixture();
    const entry = await f.enter();
    const render = vi.fn(() => '<p>should not render</p>');
    const request = () =>
      bffRequest('/api/auth/entry', {
        cookie: entry.cookie,
        headers: { accept: 'application/json' },
      });
    const success = await f.http.handle(request(), render);
    expect(success!.headers.get('content-type')).toBe('application/json; charset=utf-8');
    expect(await success!.json()).toMatchObject({ state: 'entry', identityState: 'active' });
    f.native.self.mockRejectedValueOnce(new NativeApiError('provider_refused', 429, 11));
    const failure = await f.http.handle(request(), render);
    expect(failure!.status).toBe(429);
    expect(await failure!.json()).toEqual({ code: 'provider_refused' });
    expect(failure!.headers.get('retry-after')).toBe('11');
    expect(render).not.toHaveBeenCalled();
  });
});
