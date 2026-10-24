import { describe, expect, it, vi } from 'vitest';

import { createAuthenticatedServerSdk } from './authenticated-server';
import type { AuthenticatedFetch } from './authenticated-server';
import { createServerSdk, PUBLIC_GET_CONTRACTS, PUBLIC_GET_WRAPPERS } from './server';

const site = {
  displayName: 'School',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};
const account = {
  actorId: '019a154b-0000-7000-8000-000000000011',
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
  site,
};
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const problem = (status: number, code: string) => ({
  type: 'https://errors.learnstack.dev/' + code,
  title: 'Refused',
  status,
  instance: '/api/v1/account/site',
  code,
  messageKey: 'lockey_' + code,
  correlationId: 'request-123',
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
describe('exact authenticated AccountSite SDK', () => {
  it('has one typed GET/no-store capability; public wrappers remain anonymous', async () => {
    const transport = vi.fn(async () => response(account));
    const sdk = createAuthenticatedServerSdk(transport);
    expect(Object.keys(sdk)).toEqual(['getAccountSite']);
    expect((await sdk.getAccountSite()).kind).toBe('success');
    expect(transport).toHaveBeenCalledWith('/api/v1/account/site', {
      method: 'GET',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal: undefined,
    });
    expect(Object.values(PUBLIC_GET_CONTRACTS)).not.toContain('GetAccountSite');
    expect(PUBLIC_GET_CONTRACTS).not.toHaveProperty('/api/v1/account/site');
    expect(Object.values(PUBLIC_GET_WRAPPERS)).not.toContain('getAccountSite');
    expect(createServerSdk(transport)).not.toHaveProperty('getAccountSite');
  });
  it('accepts the API actor contract without imposing a UUID version', async () => {
    const body = { ...account, actorId: 'FA22AEF7-7175-4CF3-8AD3-A300E2204776' };
    expect(await createAuthenticatedServerSdk(async () => response(body)).getAccountSite()).toEqual(
      {
        kind: 'success',
        status: 200,
        data: body,
      },
    );
  });
  it.each([
    { ...account, actorId: 'not-a-uuid' },
    { ...account, actorId: '00000000-0000-0000-0000-000000000000' },
    { ...account, tenantId: '00000000-0000-0000-0000-000000000000' },
    { ...account, organizationId: 'bad' },
    { ...account, site: {} },
    {},
  ])('refuses malformed identity/projection %j', async (body) => {
    expect(await createAuthenticatedServerSdk(async () => response(body)).getAccountSite()).toEqual(
      { kind: 'invalid-response', reason: 'invalid-success' },
    );
  });
  it('does not turn a failure into a public success or expose thrown provider details', async () => {
    const sdk = createAuthenticatedServerSdk(async () => {
      throw new Error('private-canary');
    });
    expect(await sdk.getAccountSite()).toEqual({ kind: 'transport-error' });
    expect(
      await createAuthenticatedServerSdk(async () => response(account, 201)).getAccountSite(),
    ).toEqual({ kind: 'invalid-response', reason: 'unexpected-status' });
  });
  it('honors cancellation before any transport call', async () => {
    const transport = vi.fn();
    const controller = new AbortController();
    controller.abort();
    expect(
      await createAuthenticatedServerSdk(transport).getAccountSite({ signal: controller.signal }),
    ).toEqual({ kind: 'cancelled' });
    expect(transport).not.toHaveBeenCalled();
  });
  it('passes the exact live caller signal to its sole no-store GET', async () => {
    const controller = new AbortController();
    const transport = vi.fn<AuthenticatedFetch>().mockResolvedValue(response(account));
    expect(
      await createAuthenticatedServerSdk(transport).getAccountSite({ signal: controller.signal }),
    ).toEqual({ kind: 'success', status: 200, data: account });
    expect(controller.signal.aborted).toBe(false);
    expect(transport).toHaveBeenCalledExactlyOnceWith('/api/v1/account/site', {
      method: 'GET',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    });
  });
  it.each(['resolve', 'reject'] as const)(
    'cancellation wins when transport headers %s after abort, without consuming JSON',
    async (outcome) => {
      const controller = new AbortController();
      const held = deferred<Response>();
      const received = response(account);
      const json = vi.spyOn(received, 'json');
      const transport = vi.fn<AuthenticatedFetch>().mockReturnValue(held.promise);
      const pending = createAuthenticatedServerSdk(transport).getAccountSite({
        signal: controller.signal,
      });
      expect(transport).toHaveBeenCalledTimes(1);
      controller.abort();
      if (outcome === 'resolve') held.resolve(received);
      else held.reject(new Error('private-header-transport-canary'));
      expect(await pending).toEqual({ kind: 'cancelled' });
      expect(json).not.toHaveBeenCalled();
    },
  );
  it.each(['resolve', 'malformed JSON', 'body transport failure'] as const)(
    'cancellation wins when JSON consumption finishes with %s after abort',
    async (outcome) => {
      const controller = new AbortController();
      const held = deferred<unknown>();
      const reached = deferred<void>();
      const received = response(account);
      const json = vi.spyOn(received, 'json').mockImplementation(() => {
        reached.resolve();
        return held.promise;
      });
      const pending = createAuthenticatedServerSdk(async () => received).getAccountSite({
        signal: controller.signal,
      });
      await reached.promise;
      controller.abort();
      if (outcome === 'resolve') held.resolve(account);
      else
        held.reject(
          outcome === 'malformed JSON'
            ? new SyntaxError('private-body-syntax-canary')
            : new Error('private-body-transport-canary'),
        );
      expect(await pending).toEqual({ kind: 'cancelled' });
      expect(json).toHaveBeenCalledTimes(1);
    },
  );
  it.each(['malformed JSON', 'body transport failure'] as const)(
    'distinguishes %s without caller cancellation or raw error exposure',
    async (outcome) => {
      const received =
        outcome === 'malformed JSON'
          ? new Response('{')
          : new Response(
              new ReadableStream({
                start(controller) {
                  controller.error(new Error('private-body-canary'));
                },
              }),
            );
      expect(await createAuthenticatedServerSdk(async () => received).getAccountSite()).toEqual(
        outcome === 'malformed JSON'
          ? { kind: 'invalid-response', reason: 'malformed-json' }
          : { kind: 'transport-error' },
      );
    },
  );
  it.each([
    [
      400,
      'validation_failed',
      { code: 'validation_failed', fieldErrors: { locale: ['lockey_required'] } },
    ],
    [401, 'unauthorized', { code: 'unauthorized' }],
    [403, 'forbidden', { code: 'forbidden' }],
    [404, 'not_found', { code: 'not_found' }],
    [429, 'rate_limited', { code: 'rate_limited', retryAfter: 17 }],
    [503, 'dependency_unavailable', { code: 'dependency_unavailable', retryAfter: 17 }],
  ] as const)(
    'delegates a valid %s/%s problem and retry header to the shared response policy',
    async (status, code, error) => {
      const payload = {
        ...problem(status, code),
        ...(status === 400 ? { errors: { locale: [{ key: 'lockey_required' }] } } : {}),
      };
      const received = new Response(
        JSON.stringify({ ...payload, secret: 'private-problem-canary' }),
        {
          status,
          headers: { 'Retry-After': '17' },
        },
      );
      expect(await createAuthenticatedServerSdk(async () => received).getAccountSite()).toEqual({
        kind: 'api-error',
        status,
        problem: payload,
        error,
      });
    },
  );
  it('does not turn an unsafe retry header into an authenticated retry hint', async () => {
    const received = new Response(JSON.stringify(problem(429, 'rate_limited')), {
      status: 429,
      headers: { 'Retry-After': '9999999999999999999' },
    });
    expect(await createAuthenticatedServerSdk(async () => received).getAccountSite()).toEqual({
      kind: 'api-error',
      status: 429,
      problem: problem(429, 'rate_limited'),
      error: { code: 'rate_limited' },
    });
  });
  it.each([{}, { ...problem(401, 'unauthorized'), status: 403 }])(
    'refuses an invalid body or mismatched problem status: %j',
    async (body) => {
      expect(
        await createAuthenticatedServerSdk(async () => response(body, 401)).getAccountSite(),
      ).toEqual({ kind: 'invalid-response', reason: 'invalid-problem' });
    },
  );
  it.each([201, 204, 302, 304])(
    'rejects unexpected status %s before reading JSON',
    async (status) => {
      const received = new Response(null, { status });
      const json = vi.spyOn(received, 'json');
      expect(await createAuthenticatedServerSdk(async () => received).getAccountSite()).toEqual({
        kind: 'invalid-response',
        reason: 'unexpected-status',
      });
      expect(json).not.toHaveBeenCalled();
    },
  );
});
